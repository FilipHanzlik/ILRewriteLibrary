using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Merged view of a TypeDefinition combining original metadata with registered delta mutations.
/// </summary>
public readonly struct OverlayTypeDefinition
{
    public TypeDefinitionHandle Handle { get; }
    public TypeAttributes Attributes { get; }
    public StringHandle Name { get; }
    public StringHandle Namespace { get; }
    public EntityHandle BaseType { get; }
    public int FieldStartRow { get; }
    public int MethodStartRow { get; }
    public bool IsModified { get; }

    internal OverlayTypeDefinition(
        TypeDefinitionHandle handle,
        TypeAttributes attributes,
        StringHandle name,
        StringHandle @namespace,
        EntityHandle baseType,
        int fieldStartRow,
        int methodStartRow,
        bool isModified)
    {
        Handle = handle;
        Attributes = attributes;
        Name = name;
        Namespace = @namespace;
        BaseType = baseType;
        FieldStartRow = fieldStartRow;
        MethodStartRow = methodStartRow;
        IsModified = isModified;
    }
}
