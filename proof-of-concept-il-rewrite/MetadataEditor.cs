using System;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SrmRewrite;

/// <summary>
/// Immutable representation of the delta mutation state for a single MethodDef row.
/// </summary>
public readonly struct MethodDefOverride
{
    public readonly bool IsModified;
    public readonly bool ReplaceWithSharedRet;
    public readonly bool ClearBody;
    public readonly MethodImplAttributes ImplAttributesToAdd;
    public readonly byte[]? CustomBodyBytes;

    public MethodDefOverride(
        bool isModified,
        bool replaceWithSharedRet,
        bool clearBody,
        MethodImplAttributes implAttributesToAdd,
        byte[]? customBodyBytes)
    {
        IsModified = isModified;
        ReplaceWithSharedRet = replaceWithSharedRet;
        ClearBody = clearBody;
        ImplAttributesToAdd = implAttributesToAdd;
        CustomBodyBytes = customBodyBytes;
    }
}

/// <summary>
/// Holds sparse or lazy flat arrays storing delta mutations per metadata table.
/// </summary>
public sealed class MetadataEditor
{
    private readonly MetadataReader _reader;
    private MethodDefOverride[]? _methodDefOverrides;
    private int _methodDefModifiedCount;

    internal MetadataEditor(MetadataReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Returns a strongly-typed, zero-allocation editor for the specified MethodDefinition handle.
    /// </summary>
    public MethodDefEditor Edit(MethodDefinitionHandle handle)
    {
        int rowId = MetadataTokens.GetRowNumber(handle);
        EnsureMethodDefArray();
        return new MethodDefEditor(this, rowId);
    }

    internal void SetReplaceWithSharedRet(int rowId)
    {
        EnsureMethodDefArray();
        ref var entry = ref _methodDefOverrides![rowId];
        if (!entry.IsModified)
        {
            _methodDefModifiedCount++;
        }
        entry = new MethodDefOverride(
            isModified: true,
            replaceWithSharedRet: true,
            clearBody: false,
            implAttributesToAdd: entry.ImplAttributesToAdd,
            customBodyBytes: null);
    }

    internal void SetClearBody(int rowId)
    {
        EnsureMethodDefArray();
        ref var entry = ref _methodDefOverrides![rowId];
        if (!entry.IsModified)
        {
            _methodDefModifiedCount++;
        }
        entry = new MethodDefOverride(
            isModified: true,
            replaceWithSharedRet: false,
            clearBody: true,
            implAttributesToAdd: entry.ImplAttributesToAdd,
            customBodyBytes: null);
    }

    internal void SetRawBody(int rowId, byte[] rawMethodBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(rawMethodBodyBytes);
        EnsureMethodDefArray();
        ref var entry = ref _methodDefOverrides![rowId];
        if (!entry.IsModified)
        {
            _methodDefModifiedCount++;
        }
        entry = new MethodDefOverride(
            isModified: true,
            replaceWithSharedRet: false,
            clearBody: false,
            implAttributesToAdd: entry.ImplAttributesToAdd,
            customBodyBytes: rawMethodBodyBytes);
    }

    internal void AddImplAttributes(int rowId, MethodImplAttributes flags)
    {
        EnsureMethodDefArray();
        ref var entry = ref _methodDefOverrides![rowId];
        if (!entry.IsModified)
        {
            _methodDefModifiedCount++;
        }
        entry = new MethodDefOverride(
            isModified: true,
            replaceWithSharedRet: entry.ReplaceWithSharedRet,
            clearBody: entry.ClearBody,
            implAttributesToAdd: entry.ImplAttributesToAdd | flags,
            customBodyBytes: entry.CustomBodyBytes);
    }

    private void EnsureMethodDefArray()
    {
        if (_methodDefOverrides == null)
        {
            int rowCount = _reader.GetTableRowCount(TableIndex.MethodDef);
            _methodDefOverrides = new MethodDefOverride[rowCount + 1];
        }
    }

    internal bool IsTableModified(TableIndex table)
    {
        return table switch
        {
            TableIndex.MethodDef => _methodDefModifiedCount > 0,
            _ => false
        };
    }

    internal bool TryGetMethodDefOverride(int rowId, out MethodDefOverride overrideVal)
    {
        if (_methodDefOverrides != null && rowId > 0 && rowId < _methodDefOverrides.Length)
        {
            overrideVal = _methodDefOverrides[rowId];
            return overrideVal.IsModified;
        }

        overrideVal = default;
        return false;
    }
}
