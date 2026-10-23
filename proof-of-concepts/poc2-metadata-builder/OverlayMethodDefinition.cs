using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Merged view of a MethodDefinition combining original metadata with registered delta mutations.
/// </summary>
public readonly struct OverlayMethodDefinition
{
    public MethodDefinitionHandle Handle { get; }
    public MethodAttributes Attributes { get; }
    public MethodImplAttributes ImplAttributes { get; }
    public StringHandle Name { get; }
    public BlobHandle Signature { get; }
    public bool HasBody { get; }
    public int RelativeVirtualAddress { get; }
    public EditedMethodBodyHandle BodyHandle { get; }
    public bool IsModified { get; }

    internal OverlayMethodDefinition(
        MethodDefinitionHandle handle,
        MethodAttributes attributes,
        MethodImplAttributes implAttributes,
        StringHandle name,
        BlobHandle signature,
        bool hasBody,
        int rva,
        EditedMethodBodyHandle bodyHandle,
        bool isModified)
    {
        Handle = handle;
        Attributes = attributes;
        ImplAttributes = implAttributes;
        Name = name;
        Signature = signature;
        HasBody = hasBody;
        RelativeVirtualAddress = rva;
        BodyHandle = bodyHandle;
        IsModified = isModified;
    }
}
