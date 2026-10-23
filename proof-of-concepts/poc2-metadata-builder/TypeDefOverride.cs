using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Immutable record of sparse delta mutations for a single TypeDef row.
/// </summary>
public readonly struct TypeDefOverride
{
    public bool IsModified { get; init; }
    public TypeAttributes? Attributes { get; init; }
    public StringHandle? Name { get; init; }
    public StringHandle? Namespace { get; init; }
}
