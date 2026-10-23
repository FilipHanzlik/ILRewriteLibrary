using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Stack-allocated fluent editor for mutating a single Field row in-place.
/// </summary>
public readonly struct FieldDefEditor
{
    private readonly MetadataEditor _editor;
    private readonly int _rowId;

    internal FieldDefEditor(MetadataEditor editor, int rowId)
    {
        _editor = editor;
        _rowId = rowId;
    }

    public FieldDefEditor SetAttributes(FieldAttributes attributes)
    {
        _editor.UpdateFieldDef(_rowId, ov => new FieldDefOverride
        {
            IsModified = true,
            Attributes = attributes,
            Name = ov.Name,
            Signature = ov.Signature
        });
        return this;
    }

    public FieldDefEditor SetName(StringHandle name)
    {
        _editor.UpdateFieldDef(_rowId, ov => new FieldDefOverride
        {
            IsModified = true,
            Attributes = ov.Attributes,
            Name = name,
            Signature = ov.Signature
        });
        return this;
    }

    public FieldDefEditor SetSignature(BlobHandle signature)
    {
        _editor.UpdateFieldDef(_rowId, ov => new FieldDefOverride
        {
            IsModified = true,
            Attributes = ov.Attributes,
            Name = ov.Name,
            Signature = signature
        });
        return this;
    }
}
