using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Merged view of a FieldDefinition combining original metadata with registered delta mutations.
/// </summary>
public readonly struct OverlayFieldDefinition
{
    public FieldDefinitionHandle Handle { get; }
    public FieldAttributes Attributes { get; }
    public StringHandle Name { get; }
    public BlobHandle Signature { get; }
    public bool IsModified { get; }

    internal OverlayFieldDefinition(
        FieldDefinitionHandle handle,
        FieldAttributes attributes,
        StringHandle name,
        BlobHandle signature,
        bool isModified)
    {
        Handle = handle;
        Attributes = attributes;
        Name = name;
        Signature = signature;
        IsModified = isModified;
    }
}
