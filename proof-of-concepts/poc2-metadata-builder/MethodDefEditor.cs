using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Stack-allocated fluent editor for mutating a single MethodDef row in-place.
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

    public MethodDefEditor SetBody(EditedMethodBodyHandle bodyHandle)
    {
        _editor.UpdateMethodDef(_rowId, ov => new MethodDefOverride
        {
            IsModified = true,
            ClearBody = false,
            BodyHandle = bodyHandle,
            ImplAttributes = ov.ImplAttributes,
            Attributes = ov.Attributes,
            Name = ov.Name,
            Signature = ov.Signature
        });
        return this;
    }

    public MethodDefEditor ClearBody()
    {
        _editor.UpdateMethodDef(_rowId, ov => new MethodDefOverride
        {
            IsModified = true,
            ClearBody = true,
            BodyHandle = EditedMethodBodyHandle.Nil,
            ImplAttributes = ov.ImplAttributes,
            Attributes = ov.Attributes,
            Name = ov.Name,
            Signature = ov.Signature
        });
        return this;
    }

    public MethodDefEditor SetImplAttributes(MethodImplAttributes attributes)
    {
        _editor.UpdateMethodDef(_rowId, ov => new MethodDefOverride
        {
            IsModified = true,
            ClearBody = ov.ClearBody,
            BodyHandle = ov.BodyHandle,
            ImplAttributes = attributes,
            Attributes = ov.Attributes,
            Name = ov.Name,
            Signature = ov.Signature
        });
        return this;
    }

    public MethodDefEditor SetAttributes(MethodAttributes attributes)
    {
        _editor.UpdateMethodDef(_rowId, ov => new MethodDefOverride
        {
            IsModified = true,
            ClearBody = ov.ClearBody,
            BodyHandle = ov.BodyHandle,
            ImplAttributes = ov.ImplAttributes,
            Attributes = attributes,
            Name = ov.Name,
            Signature = ov.Signature
        });
        return this;
    }

    public MethodDefEditor SetName(StringHandle name)
    {
        _editor.UpdateMethodDef(_rowId, ov => new MethodDefOverride
        {
            IsModified = true,
            ClearBody = ov.ClearBody,
            BodyHandle = ov.BodyHandle,
            ImplAttributes = ov.ImplAttributes,
            Attributes = ov.Attributes,
            Name = name,
            Signature = ov.Signature
        });
        return this;
    }

    public MethodDefEditor SetSignature(BlobHandle signature)
    {
        _editor.UpdateMethodDef(_rowId, ov => new MethodDefOverride
        {
            IsModified = true,
            ClearBody = ov.ClearBody,
            BodyHandle = ov.BodyHandle,
            ImplAttributes = ov.ImplAttributes,
            Attributes = ov.Attributes,
            Name = ov.Name,
            Signature = signature
        });
        return this;
    }
}
