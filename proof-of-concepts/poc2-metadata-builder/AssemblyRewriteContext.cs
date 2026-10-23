using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// Root context coordinating the Reader, Editor, and MetadataBuilderPeWriter triad for an assembly rewrite operation.
/// </summary>
public sealed class AssemblyRewriteContext : IDisposable
{
    private readonly unsafe byte* _unmanagedPeBuffer;
    private readonly Stream? _ownedStream;
    private readonly PEReader _peReader;
    private readonly MetadataReader _metadataReader;
    private readonly MetadataEditor _editor;
    private readonly OverlayReader _reader;
    private readonly MetadataBuilderPeWriter _writer;
    private bool _disposed;

    private unsafe AssemblyRewriteContext(byte* unmanagedPeBuffer, Stream? ownedStream, PEReader peReader)
    {
        _unmanagedPeBuffer = unmanagedPeBuffer;
        _ownedStream = ownedStream;
        _peReader = peReader;
        _metadataReader = peReader.GetMetadataReader();
        _editor = new MetadataEditor(_metadataReader);
        _reader = new OverlayReader(_peReader, _metadataReader, _editor);
        _writer = new MetadataBuilderPeWriter(_reader);
    }

    /// <summary>
    /// Loads an assembly from disk for rewriting using direct zero-allocation unmanaged mapping.
    /// </summary>
    public static unsafe AssemblyRewriteContext Load(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        using var handle = File.OpenHandle(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        long length = RandomAccess.GetLength(handle);
        if (length > int.MaxValue)
            throw new BadImageFormatException("Assembly exceeds maximum supported PE size.");

        int size = (int)length;
        byte* peImage = (byte*)NativeMemory.Alloc((nuint)size);
        try
        {
            RandomAccess.Read(handle, new Span<byte>(peImage, size), 0);
            var peReader = new PEReader(peImage, size);
            return new AssemblyRewriteContext(peImage, null, peReader);
        }
        catch
        {
            NativeMemory.Free(peImage);
            throw;
        }
    }

    /// <summary>
    /// Loads an assembly from a stream for rewriting.
    /// </summary>
    public static unsafe AssemblyRewriteContext Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var peReader = new PEReader(stream);
        return new AssemblyRewriteContext(null, stream, peReader);
    }

    /// <summary>
    /// Real-time merged reader exposing both high-level SRM entities and raw table/heap spans.
    /// </summary>
    public OverlayReader Reader => _reader;

    /// <summary>
    /// Metadata editor for recording general-purpose entity mutations and custom method bodies.
    /// </summary>
    public MetadataEditor Editor => _editor;

    /// <summary>
    /// High-performance PE container writer driven by MetadataBuilder.
    /// </summary>
    public MetadataBuilderPeWriter Writer => _writer;

    public unsafe void Dispose()
    {
        if (!_disposed)
        {
            _peReader.Dispose();
            _ownedStream?.Dispose();
            if (_unmanagedPeBuffer != null)
            {
                NativeMemory.Free(_unmanagedPeBuffer);
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    ~AssemblyRewriteContext()
    {
        Dispose();
    }
}
