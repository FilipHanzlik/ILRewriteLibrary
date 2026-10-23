using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SrmRewrite;

/// <summary>
/// Provides a real-time merged view of an assembly's metadata (original SRM view + in-memory editor overlay)
/// as well as bulk span and table offset query APIs for high-performance zero-allocation writing.
/// </summary>
public sealed class OverlayReader
{
    private readonly PEReader _peReader;
    private readonly MetadataReader _metadataReader;
    private readonly MetadataEditor _editor;

    internal OverlayReader(PEReader peReader, MetadataReader metadataReader, MetadataEditor editor)
    {
        _peReader = peReader;
        _metadataReader = metadataReader;
        _editor = editor;
    }

    public PEReader PEReader => _peReader;
    public MetadataReader MetadataReader => _metadataReader;

    public bool TryGetMethodDefOverride(int rowId, out MethodDefOverride overrideVal)
        => _editor.TryGetMethodDefOverride(rowId, out overrideVal);

    // ---------------------------------------------------------
    // Tier 1: High-Level SRM-Compatible Merged View
    // ---------------------------------------------------------

    public MethodDefinitionHandleCollection MethodDefinitions => _metadataReader.MethodDefinitions;
    public TypeDefinitionHandleCollection TypeDefinitions => _metadataReader.TypeDefinitions;
    public AssemblyReferenceHandleCollection AssemblyReferences => _metadataReader.AssemblyReferences;
    public CustomAttributeHandleCollection CustomAttributes => _metadataReader.CustomAttributes;
    public ManifestResourceHandleCollection ManifestResources => _metadataReader.ManifestResources;

    public OverlayMethodDefinition GetMethodDefinition(MethodDefinitionHandle handle)
    {
        int rowId = MetadataTokens.GetRowNumber(handle);
        var original = _metadataReader.GetMethodDefinition(handle);
        if (_editor.TryGetMethodDefOverride(rowId, out var delta))
        {
            return new OverlayMethodDefinition(handle, original, _peReader, delta);
        }

        return new OverlayMethodDefinition(handle, original, _peReader);
    }

    public bool HasBody(MethodDefinitionHandle handle)
    {
        int rowId = MetadataTokens.GetRowNumber(handle);
        if (_editor.TryGetMethodDefOverride(rowId, out var delta))
        {
            if (delta.ClearBody) return false;
            if (delta.ReplaceWithSharedRet) return true;
            if (delta.CustomBodyBytes != null) return true;
        }

        int tableOff = _metadataReader.GetTableMetadataOffset(TableIndex.MethodDef);
        int rowSize = _metadataReader.GetTableRowSize(TableIndex.MethodDef);
        int rowOff = tableOff + (rowId - 1) * rowSize;
        unsafe
        {
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_metadataReader.MetadataPointer + rowOff, 4));
            return rva != 0;
        }
    }

    public TypeDefinition GetTypeDefinition(TypeDefinitionHandle handle) => _metadataReader.GetTypeDefinition(handle);
    public AssemblyDefinition GetAssemblyDefinition() => _metadataReader.GetAssemblyDefinition();
    public ModuleDefinition GetModuleDefinition() => _metadataReader.GetModuleDefinition();
    public CustomAttribute GetCustomAttribute(CustomAttributeHandle handle) => _metadataReader.GetCustomAttribute(handle);
    public ManifestResource GetManifestResource(ManifestResourceHandle handle) => _metadataReader.GetManifestResource(handle);
    public string GetString(StringHandle handle) => _metadataReader.GetString(handle);
    public byte[] GetBlobBytes(BlobHandle handle) => _metadataReader.GetBlobBytes(handle);
    public Guid GetGuid(GuidHandle handle) => _metadataReader.GetGuid(handle);

    // ---------------------------------------------------------
    // Tier 2: Bulk Span & Table Offset API (for PeWriter Fast Path)
    // ---------------------------------------------------------

    /// <summary>
    /// Gets a direct ReadOnlySpan over the entire raw BSJB metadata block.
    /// </summary>
    public ReadOnlySpan<byte> GetRawMetadataSpan()
    {
        unsafe
        {
            return new ReadOnlySpan<byte>(_metadataReader.MetadataPointer, _metadataReader.MetadataLength);
        }
    }

    public int MetadataLength => _metadataReader.MetadataLength;

    public bool IsTableModified(TableIndex table) => _editor.IsTableModified(table);

    public int GetTableOffset(TableIndex table) => _metadataReader.GetTableMetadataOffset(table);
    public int GetTableRowSize(TableIndex table) => _metadataReader.GetTableRowSize(table);
    public int GetTableRowCount(TableIndex table) => _metadataReader.GetTableRowCount(table);

    public int GetHeapOffset(HeapIndex heap) => _metadataReader.GetHeapMetadataOffset(heap);
    public int GetHeapSize(HeapIndex heap) => _metadataReader.GetHeapSize(heap);

    // PE structure helpers
    public PEHeaders PEHeaders => _peReader.PEHeaders;
    public CorHeader? CorHeader => _peReader.PEHeaders.CorHeader;
    public ImmutableArray<DebugDirectoryEntry> ReadDebugDirectory() => _peReader.ReadDebugDirectory();
    public PEMemoryBlock GetSectionData(int relativeVirtualAddress) => _peReader.GetSectionData(relativeVirtualAddress);
}
