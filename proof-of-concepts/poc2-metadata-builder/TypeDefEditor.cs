using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Stack-allocated fluent editor for mutating a single TypeDef row in-place.
/// </summary>
public readonly struct TypeDefEditor
{
    private readonly MetadataEditor _editor;
    private readonly int _rowId;

    internal TypeDefEditor(MetadataEditor editor, int rowId)
    {
        _editor = editor;
        _rowId = rowId;
    }

    public TypeDefEditor SetAttributes(TypeAttributes attributes)
    {
        _editor.UpdateTypeDef(_rowId, ov => new TypeDefOverride
        {
            IsModified = true,
            Attributes = attributes,
            Name = ov.Name,
            Namespace = ov.Namespace
        });
        return this;
    }

    public TypeDefEditor SetName(StringHandle name)
    {
        _editor.UpdateTypeDef(_rowId, ov => new TypeDefOverride
        {
            IsModified = true,
            Attributes = ov.Attributes,
            Name = name,
            Namespace = ov.Namespace
        });
        return this;
    }

    public TypeDefEditor SetNamespace(StringHandle @namespace)
    {
        _editor.UpdateTypeDef(_rowId, ov => new TypeDefOverride
        {
            IsModified = true,
            Attributes = ov.Attributes,
            Name = ov.Name,
            Namespace = @namespace
        });
        return this;
    }
}
