using System.Reflection;

namespace SrmRewrite;

/// <summary>
/// A zero-allocation, strongly-typed editor for a single MethodDef metadata row.
/// </summary>
public readonly struct MethodDefEditor
{
    private readonly MetadataEditor _editor;
    private readonly int _rowId;

    internal MethodDefEditor(MetadataEditor editor, int rowId)
    {
        _editor = editor;
        _rowId = rowId;
    }

    /// <summary>
    /// Replaces the method body with a shared 1-byte ret instruction (tiny header + 0x2A).
    /// </summary>
    public MethodDefEditor ReplaceBodyWithSharedRet()
    {
        _editor.SetReplaceWithSharedRet(_rowId);
        return this;
    }

    /// <summary>
    /// Clears the method body entirely (sets RVA to 0).
    /// </summary>
    public MethodDefEditor ClearBody()
    {
        _editor.SetClearBody(_rowId);
        return this;
    }

    /// <summary>
    /// Sets a custom raw CIL method body (tiny or fat block).
    /// </summary>
    public MethodDefEditor SetRawBody(byte[] rawMethodBodyBytes)
    {
        _editor.SetRawBody(_rowId, rawMethodBodyBytes);
        return this;
    }

    /// <summary>
    /// Adds implementation attributes (flags) to the method definition.
    /// </summary>
    public MethodDefEditor AddImplAttributes(MethodImplAttributes flags)
    {
        _editor.AddImplAttributes(_rowId, flags);
        return this;
    }
}
