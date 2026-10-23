using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SrmRewrite;

/// <summary>
/// A zero-allocation merged view of a MethodDefinition, reporting real-time mutations
/// applied via MetadataEditor, falling back to the underlying MetadataReader.
/// </summary>
public readonly struct OverlayMethodDefinition
{
    private static readonly byte[] s_sharedRetIL = [0x2a];
    private static readonly byte[] s_sharedRetBodyBlock = [0x06, 0x2a];

    private readonly MethodDefinitionHandle _handle;
    private readonly MethodDefinition _original;
    private readonly PEReader? _peReader;
    private readonly bool _isModified;
    private readonly MethodDefOverride _override;

    internal OverlayMethodDefinition(MethodDefinitionHandle handle, MethodDefinition original, PEReader? peReader)
    {
        _handle = handle;
        _original = original;
        _peReader = peReader;
        _isModified = false;
        _override = default;
    }

    internal OverlayMethodDefinition(MethodDefinitionHandle handle, MethodDefinition original, PEReader? peReader, MethodDefOverride delta)
    {
        _handle = handle;
        _original = original;
        _peReader = peReader;
        _isModified = true;
        _override = delta;
    }

    public MethodDefinitionHandle Handle => _handle;
    public StringHandle Name => _original.Name;
    public MethodAttributes Attributes => _original.Attributes;
    public BlobHandle Signature => _original.Signature;

    public MethodImplAttributes ImplAttributes
    {
        get
        {
            if (!_isModified)
                return _original.ImplAttributes;

            var result = _original.ImplAttributes | _override.ImplAttributesToAdd;
            if (_override.ReplaceWithSharedRet)
                result |= MethodImplAttributes.NoInlining;
            return result;
        }
    }

    public int RelativeVirtualAddress
    {
        get
        {
            if (!_isModified)
                return _original.RelativeVirtualAddress;

            if (_override.ClearBody)
                return 0;

            if (_override.CustomBodyBytes != null)
                return _original.RelativeVirtualAddress != 0 ? _original.RelativeVirtualAddress : 1;

            // If ReplaceWithSharedRet, the exact RVA is assigned during PE layout/write,
            // but if the original had a body, it remains non-zero.
            return _original.RelativeVirtualAddress;
        }
    }

    public bool HasBody
    {
        get
        {
            if (!_isModified)
                return _original.RelativeVirtualAddress != 0;

            if (_override.ClearBody)
                return false;

            if (_override.ReplaceWithSharedRet)
                return true;

            if (_override.CustomBodyBytes != null)
                return true;

            return _original.RelativeVirtualAddress != 0;
        }
    }

    public bool IsBodyStrippedToSharedRet => _isModified && _override.ReplaceWithSharedRet;

    /// <summary>
    /// Returns the logical CIL instructions for this method.
    /// If stripped to shared ret, returns [0x2A] (ret).
    /// </summary>
    public ReadOnlySpan<byte> GetLogicalILBytes()
    {
        if (IsBodyStrippedToSharedRet)
            return s_sharedRetIL;

        if (_isModified && _override.CustomBodyBytes != null)
        {
            if (_override.CustomBodyBytes.Length == 0)
                return ReadOnlySpan<byte>.Empty;

            byte header = _override.CustomBodyBytes[0];
            if ((header & 0x03) == 0x02) // Tiny
            {
                int codeSize = header >> 2;
                return new ReadOnlySpan<byte>(_override.CustomBodyBytes, 1, Math.Min(codeSize, _override.CustomBodyBytes.Length - 1));
            }
            if ((header & 0x03) == 0x03 && _override.CustomBodyBytes.Length >= 12) // Fat
            {
                int headerSize = (_override.CustomBodyBytes[1] >> 4) * 4;
                int codeSize = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(_override.CustomBodyBytes, 4, 4));
                return new ReadOnlySpan<byte>(_override.CustomBodyBytes, headerSize, Math.Min(codeSize, _override.CustomBodyBytes.Length - headerSize));
            }
            return _override.CustomBodyBytes;
        }

        if (!HasBody || _peReader == null || _original.RelativeVirtualAddress == 0)
            return ReadOnlySpan<byte>.Empty;

        return _peReader.GetMethodBody(_original.RelativeVirtualAddress).GetILBytes();
    }

    /// <summary>
    /// Returns the raw method body bytes including header.
    /// If stripped to shared ret, returns [0x06, 0x2A] (tiny header + ret).
    /// </summary>
    public ReadOnlySpan<byte> GetLogicalBodyBlockBytes()
    {
        if (IsBodyStrippedToSharedRet)
            return s_sharedRetBodyBlock;

        if (_isModified && _override.CustomBodyBytes != null)
            return _override.CustomBodyBytes;

        return GetLogicalILBytes();
    }

    public ParameterHandleCollection GetParameters() => _original.GetParameters();
    public GenericParameterHandleCollection GetGenericParameters() => _original.GetGenericParameters();
    public CustomAttributeHandleCollection GetCustomAttributes() => _original.GetCustomAttributes();
    public DeclarativeSecurityAttributeHandleCollection GetDeclarativeSecurityAttributes() => _original.GetDeclarativeSecurityAttributes();
    public MethodImport GetImport() => _original.GetImport();
    public TypeDefinitionHandle GetDeclaringType() => _original.GetDeclaringType();
}
