namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Opaque handle representing an edited or synthesized method body registered in <see cref="MetadataEditor"/>.
/// Multiple method definitions can reference the same handle to share a single emitted body in the CIL stream.
/// </summary>
public readonly record struct EditedMethodBodyHandle(int Id)
{
    public static readonly EditedMethodBodyHandle Nil = default;

    public bool IsNil => Id == 0;

    public override string ToString() => IsNil ? "nil" : $"EditedBody(0x{Id:X8})";
}
