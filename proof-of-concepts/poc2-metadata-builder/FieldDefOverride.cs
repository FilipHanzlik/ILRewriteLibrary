using System.Reflection;
using System.Reflection.Metadata;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Immutable record of sparse delta mutations for a single Field row.
/// </summary>
public readonly struct FieldDefOverride
{
    public bool IsModified { get; init; }
    public FieldAttributes? Attributes { get; init; }
    public StringHandle? Name { get; init; }
    public BlobHandle? Signature { get; init; }
}
