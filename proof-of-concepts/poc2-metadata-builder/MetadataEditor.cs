using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// General-purpose editor coordinating sparse entity delta mutations, custom method bodies,
/// and registered strings/blobs.
/// </summary>
public sealed class MetadataEditor
{
    private readonly MetadataReader _reader;
    private readonly List<RegisteredMethodBody> _registeredBodies = new();
    private readonly Dictionary<StringHandle, string> _customStrings = new();
    private readonly Dictionary<BlobHandle, byte[]> _customBlobs = new();
    private int _nextCustomStringIndex = 1;
    private int _nextCustomBlobIndex = 1;

    private MethodDefOverride[]? _methodDefOverrides;
    private TypeDefOverride[]? _typeDefOverrides;
    private FieldDefOverride[]? _fieldDefOverrides;

    public MetadataEditor(MetadataReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>
    /// Registers a new structured CIL method body created via SRM's <see cref="InstructionEncoder"/>.
    /// </summary>
    public EditedMethodBodyHandle CreateMethodBody(
        InstructionEncoder il,
        int maxStack = 8,
        StandaloneSignatureHandle localVariablesSignature = default,
        MethodBodyAttributes attributes = MethodBodyAttributes.InitLocals)
    {
        byte[] codeBytes = il.CodeBuilder.ToArray();
        var body = new RegisteredMethodBody
        {
            IsRaw = false,
            CodeBytes = codeBytes,
            MaxStack = maxStack,
            LocalVariablesSignature = localVariablesSignature,
            Attributes = attributes
        };

        _registeredBodies.Add(body);
        return new EditedMethodBodyHandle(_registeredBodies.Count);
    }

    /// <summary>
    /// Registers a raw pre-encoded CIL method body byte array (including Tiny or Fat header, IL, and optional EH tables).
    /// </summary>
    public EditedMethodBodyHandle CreateRawMethodBody(byte[] rawBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBodyBytes);
        var body = new RegisteredMethodBody
        {
            IsRaw = true,
            RawBytes = (byte[])rawBodyBytes.Clone()
        };

        _registeredBodies.Add(body);
        return new EditedMethodBodyHandle(_registeredBodies.Count);
    }

    /// <summary>
    /// Registers a new string literal on the editor and returns a handle for assignment to metadata entities.
    /// </summary>
    public StringHandle GetOrAddString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int index = _nextCustomStringIndex++;
        // Use high bit / negative offset representation to distinguish from original reader heap offsets
        var handle = MetadataTokens.StringHandle(0x70000000 | index);
        _customStrings[handle] = value;
        return handle;
    }

    /// <summary>
    /// Registers a new blob on the editor and returns a handle for assignment to metadata entities.
    /// </summary>
    public BlobHandle GetOrAddBlob(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int index = _nextCustomBlobIndex++;
        var handle = MetadataTokens.BlobHandle(0x70000000 | index);
        _customBlobs[handle] = (byte[])value.Clone();
        return handle;
    }

    public MethodDefEditor Edit(MethodDefinitionHandle handle)
    {
        if (handle.IsNil)
            throw new ArgumentException("Cannot edit a nil MethodDefinitionHandle.", nameof(handle));

        int rowId = MetadataTokens.GetRowNumber(handle);
        EnsureMethodDefArray(rowId);
        return new MethodDefEditor(this, rowId);
    }

    public TypeDefEditor Edit(TypeDefinitionHandle handle)
    {
        if (handle.IsNil)
            throw new ArgumentException("Cannot edit a nil TypeDefinitionHandle.", nameof(handle));

        int rowId = MetadataTokens.GetRowNumber(handle);
        EnsureTypeDefArray(rowId);
        return new TypeDefEditor(this, rowId);
    }

    public FieldDefEditor Edit(FieldDefinitionHandle handle)
    {
        if (handle.IsNil)
            throw new ArgumentException("Cannot edit a nil FieldDefinitionHandle.", nameof(handle));

        int rowId = MetadataTokens.GetRowNumber(handle);
        EnsureFieldDefArray(rowId);
        return new FieldDefEditor(this, rowId);
    }

    internal void UpdateMethodDef(int rowId, Func<MethodDefOverride, MethodDefOverride> updater)
    {
        EnsureMethodDefArray(rowId);
        _methodDefOverrides![rowId] = updater(_methodDefOverrides[rowId]);
    }

    internal void UpdateTypeDef(int rowId, Func<TypeDefOverride, TypeDefOverride> updater)
    {
        EnsureTypeDefArray(rowId);
        _typeDefOverrides![rowId] = updater(_typeDefOverrides[rowId]);
    }

    internal void UpdateFieldDef(int rowId, Func<FieldDefOverride, FieldDefOverride> updater)
    {
        EnsureFieldDefArray(rowId);
        _fieldDefOverrides![rowId] = updater(_fieldDefOverrides[rowId]);
    }

    public bool TryGetMethodDefOverride(int rowId, out MethodDefOverride overrideVal)
    {
        if (_methodDefOverrides != null && rowId > 0 && rowId < _methodDefOverrides.Length)
        {
            overrideVal = _methodDefOverrides[rowId];
            return overrideVal.IsModified;
        }

        overrideVal = default;
        return false;
    }

    public bool TryGetTypeDefOverride(int rowId, out TypeDefOverride overrideVal)
    {
        if (_typeDefOverrides != null && rowId > 0 && rowId < _typeDefOverrides.Length)
        {
            overrideVal = _typeDefOverrides[rowId];
            return overrideVal.IsModified;
        }

        overrideVal = default;
        return false;
    }

    public bool TryGetFieldDefOverride(int rowId, out FieldDefOverride overrideVal)
    {
        if (_fieldDefOverrides != null && rowId > 0 && rowId < _fieldDefOverrides.Length)
        {
            overrideVal = _fieldDefOverrides[rowId];
            return overrideVal.IsModified;
        }

        overrideVal = default;
        return false;
    }

    internal bool TryGetRegisteredBody(EditedMethodBodyHandle handle, out RegisteredMethodBody? body)
    {
        if (!handle.IsNil && handle.Id > 0 && handle.Id <= _registeredBodies.Count)
        {
            body = _registeredBodies[handle.Id - 1];
            return true;
        }

        body = null;
        return false;
    }

    internal bool TryGetCustomString(StringHandle handle, out string? value)
    {
        return _customStrings.TryGetValue(handle, out value);
    }

    internal bool TryGetCustomBlob(BlobHandle handle, out byte[]? value)
    {
        return _customBlobs.TryGetValue(handle, out value);
    }

    public bool IsTableModified(TableIndex table) => table switch
    {
        TableIndex.MethodDef => _methodDefOverrides != null,
        TableIndex.TypeDef => _typeDefOverrides != null,
        TableIndex.Field => _fieldDefOverrides != null,
        _ => false
    };

    private void EnsureMethodDefArray(int rowId)
    {
        if (_methodDefOverrides == null)
        {
            int count = _reader.GetTableRowCount(TableIndex.MethodDef);
            _methodDefOverrides = new MethodDefOverride[count + 1];
        }
    }

    private void EnsureTypeDefArray(int rowId)
    {
        if (_typeDefOverrides == null)
        {
            int count = _reader.GetTableRowCount(TableIndex.TypeDef);
            _typeDefOverrides = new TypeDefOverride[count + 1];
        }
    }

    private void EnsureFieldDefArray(int rowId)
    {
        if (_fieldDefOverrides == null)
        {
            int count = _reader.GetTableRowCount(TableIndex.Field);
            _fieldDefOverrides = new FieldDefOverride[count + 1];
        }
    }
}
