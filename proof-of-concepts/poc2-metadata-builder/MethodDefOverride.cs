using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Immutable record of sparse delta mutations for a single MethodDef row.
/// </summary>
public readonly struct MethodDefOverride
{
    public bool IsModified { get; init; }
    public bool ClearBody { get; init; }
    public EditedMethodBodyHandle BodyHandle { get; init; }
    public MethodImplAttributes? ImplAttributes { get; init; }
    public MethodAttributes? Attributes { get; init; }
    public StringHandle? Name { get; init; }
    public BlobHandle? Signature { get; init; }
}
