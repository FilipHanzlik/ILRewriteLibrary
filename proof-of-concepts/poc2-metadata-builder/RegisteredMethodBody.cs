using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Internal container holding the definition of a method body registered via <see cref="MetadataEditor"/>.
/// </summary>
internal sealed class RegisteredMethodBody
{
    public bool IsRaw { get; init; }
    public byte[]? RawBytes { get; init; }
    public byte[]? CodeBytes { get; init; }
    public int MaxStack { get; init; }
    public StandaloneSignatureHandle LocalVariablesSignature { get; init; }
    public MethodBodyAttributes Attributes { get; init; }
}
