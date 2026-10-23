using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Merged real-time reader providing both high-level entity views and raw low-level table/heap access.
/// Decoupled from direct write logic to maintain clean layer encapsulation.
/// </summary>
public sealed class OverlayReader
{
    private readonly PEReader _peReader;
    private readonly MetadataReader _metadataReader;
    private readonly MetadataEditor _editor;
    private readonly RawTableReader _rawTableReader;

    public OverlayReader(PEReader peReader, MetadataReader metadataReader, MetadataEditor editor)
    {
        _peReader = peReader ?? throw new ArgumentNullException(nameof(peReader));
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _rawTableReader = new RawTableReader(metadataReader);
    }

    public PEReader PEReader => _peReader;
    public MetadataReader MetadataReader => _metadataReader;
    internal RawTableReader RawTableReader => _rawTableReader;

    public MethodDefinitionHandleCollection MethodDefinitions => _metadataReader.MethodDefinitions;
    public TypeDefinitionHandleCollection TypeDefinitions => _metadataReader.TypeDefinitions;
    public FieldDefinitionHandleCollection FieldDefinitions => _metadataReader.FieldDefinitions;

    public bool HasBody(MethodDefinitionHandle handle)
    {
        var method = GetMethodDefinition(handle);
        return method.HasBody;
    }

    public OverlayMethodDefinition GetMethodDefinition(MethodDefinitionHandle handle)
    {
        if (handle.IsNil)
            throw new ArgumentException("Cannot inspect a nil MethodDefinitionHandle.", nameof(handle));

        int rowId = MetadataTokens.GetRowNumber(handle);
        var orig = _metadataReader.GetMethodDefinition(handle);

        if (_editor.TryGetMethodDefOverride(rowId, out var ov))
        {
            var attrs = ov.Attributes ?? orig.Attributes;
            var implAttrs = ov.ImplAttributes ?? orig.ImplAttributes;
            var name = ov.Name ?? orig.Name;
            var sig = ov.Signature ?? orig.Signature;

            bool hasBody = !ov.ClearBody && (!ov.BodyHandle.IsNil || orig.RelativeVirtualAddress != 0);
            int rva = ov.ClearBody ? 0 : orig.RelativeVirtualAddress;

            return new OverlayMethodDefinition(
                handle,
                attrs,
                implAttrs,
                name,
                sig,
                hasBody,
                rva,
                ov.BodyHandle,
                isModified: true);
        }

        return new OverlayMethodDefinition(
            handle,
            orig.Attributes,
            orig.ImplAttributes,
            orig.Name,
            orig.Signature,
            orig.RelativeVirtualAddress != 0,
            orig.RelativeVirtualAddress,
            EditedMethodBodyHandle.Nil,
            isModified: false);
    }

    public OverlayTypeDefinition GetTypeDefinition(TypeDefinitionHandle handle)
    {
        if (handle.IsNil)
            throw new ArgumentException("Cannot inspect a nil TypeDefinitionHandle.", nameof(handle));

        int rowId = MetadataTokens.GetRowNumber(handle);
        var orig = _metadataReader.GetTypeDefinition(handle);
        _rawTableReader.ReadTypeDefLists(rowId, out int fieldStart, out int methodStart);

        if (_editor.TryGetTypeDefOverride(rowId, out var ov))
        {
            var attrs = ov.Attributes ?? orig.Attributes;
            var name = ov.Name ?? orig.Name;
            var ns = ov.Namespace ?? orig.Namespace;

            return new OverlayTypeDefinition(
                handle,
                attrs,
                name,
                ns,
                orig.BaseType,
                fieldStart,
                methodStart,
                isModified: true);
        }

        return new OverlayTypeDefinition(
            handle,
            orig.Attributes,
            orig.Name,
            orig.Namespace,
            orig.BaseType,
            fieldStart,
            methodStart,
            isModified: false);
    }

    public OverlayFieldDefinition GetFieldDefinition(FieldDefinitionHandle handle)
    {
        if (handle.IsNil)
            throw new ArgumentException("Cannot inspect a nil FieldDefinitionHandle.", nameof(handle));

        int rowId = MetadataTokens.GetRowNumber(handle);
        var orig = _metadataReader.GetFieldDefinition(handle);

        if (_editor.TryGetFieldDefOverride(rowId, out var ov))
        {
            var attrs = ov.Attributes ?? orig.Attributes;
            var name = ov.Name ?? orig.Name;
            var sig = ov.Signature ?? orig.Signature;

            return new OverlayFieldDefinition(
                handle,
                attrs,
                name,
                sig,
                isModified: true);
        }

        return new OverlayFieldDefinition(
            handle,
            orig.Attributes,
            orig.Name,
            orig.Signature,
            isModified: false);
    }

    public string GetString(StringHandle handle)
    {
        if (_editor.TryGetCustomString(handle, out string? custom))
            return custom!;

        return _metadataReader.GetString(handle);
    }

    public byte[] GetBlobBytes(BlobHandle handle)
    {
        if (_editor.TryGetCustomBlob(handle, out byte[]? custom))
            return custom!;

        return _metadataReader.GetBlobBytes(handle);
    }

    public Guid GetGuid(GuidHandle handle) => _metadataReader.GetGuid(handle);

    public string GetUserString(UserStringHandle handle) => _metadataReader.GetUserString(handle);

    // -------------------------------------------------------------
    // Tier 2: Low-Level Bulk Metadata Access
    // -------------------------------------------------------------

    public unsafe ReadOnlySpan<byte> GetRawMetadataSpan() =>
        new ReadOnlySpan<byte>(_metadataReader.MetadataPointer, _metadataReader.MetadataLength);

    public int GetTableOffset(TableIndex table) => _metadataReader.GetTableMetadataOffset(table);
    public int GetTableRowSize(TableIndex table) => _metadataReader.GetTableRowSize(table);
    public int GetTableRowCount(TableIndex table) => _metadataReader.GetTableRowCount(table);

    public int GetHeapOffset(HeapIndex heap) => _metadataReader.GetHeapMetadataOffset(heap);
    public int GetHeapSize(HeapIndex heap) => _metadataReader.GetHeapSize(heap);

    public bool IsTableModified(TableIndex table) => _editor.IsTableModified(table);
    public bool TryGetMethodDefOverride(int rowId, out MethodDefOverride ov) => _editor.TryGetMethodDefOverride(rowId, out ov);
    public bool TryGetTypeDefOverride(int rowId, out TypeDefOverride ov) => _editor.TryGetTypeDefOverride(rowId, out ov);
    public bool TryGetFieldDefOverride(int rowId, out FieldDefOverride ov) => _editor.TryGetFieldDefOverride(rowId, out ov);
    internal bool TryGetRegisteredBody(EditedMethodBodyHandle handle, out RegisteredMethodBody? body) => _editor.TryGetRegisteredBody(handle, out body);
}
