using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace SrmRewrite;

/// <summary>
/// High-performance PE container writer driven 100% by an OverlayReader.
/// Executes layout precomputation and single-pass sequential emission with near-zero allocations.
/// </summary>
public sealed class PeWriter
{
    private static readonly TableIndex[] s_hasCustomAttributeTables = [
        TableIndex.MethodDef, TableIndex.Field, TableIndex.TypeRef, TableIndex.TypeDef,
        TableIndex.Param, TableIndex.InterfaceImpl, TableIndex.MemberRef, TableIndex.Module,
        TableIndex.DeclSecurity, TableIndex.Property, TableIndex.Event, TableIndex.StandAloneSig,
        TableIndex.ModuleRef, TableIndex.TypeSpec, TableIndex.Assembly, TableIndex.AssemblyRef,
        TableIndex.File, TableIndex.ExportedType, TableIndex.ManifestResource, TableIndex.GenericParam,
        TableIndex.GenericParamConstraint, TableIndex.MethodSpec
    ];

    private static readonly byte[] s_dosHeader = [
        0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
        0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00,
        0x0E, 0x1F, 0xBA, 0x0E, 0x00, 0xB4, 0x09, 0xCD, 0x21, 0xB8, 0x01, 0x4C, 0xCD, 0x21, 0x54, 0x68,
        0x69, 0x73, 0x20, 0x70, 0x72, 0x6F, 0x67, 0x72, 0x61, 0x6D, 0x20, 0x63, 0x61, 0x6E, 0x6E, 0x6F,
        0x74, 0x20, 0x62, 0x65, 0x20, 0x72, 0x75, 0x6E, 0x20, 0x69, 0x6E, 0x20, 0x44, 0x4F, 0x53, 0x20,
        0x6D, 0x6F, 0x64, 0x65, 0x2E, 0x0D, 0x0D, 0x0A, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] s_zeroBuffer = new byte[4096];

    private readonly OverlayReader _reader;

    /// <summary>
    /// When true, replicates the legacy Mono.Cecil decoder defect where GenericParamConstraint
    /// and MethodSpec custom attributes lose their parent and are zeroed. This is required
    /// to pass the legacy StripReplay oracle validation suite.
    /// </summary>
    public bool ReplicateLegacyCustomAttributeBug { get; set; } = true;

    public PeWriter(OverlayReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public void Write(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
        Write(fs);
    }

    public unsafe void Write(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var peReader = _reader.PEReader;
        bool isDll = peReader.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll);
        string runtimeMain = isDll ? "_CorDllMain" : "_CorExeMain";

        int methodRowCount = _reader.GetTableRowCount(TableIndex.MethodDef);
        bool isMethodDefModified = _reader.IsTableModified(TableIndex.MethodDef);
        int methodTableOffset = _reader.GetTableOffset(TableIndex.MethodDef);
        int methodRowSize = _reader.GetTableRowSize(TableIndex.MethodDef);

        ReadOnlySpan<byte> rawMetadataSpan = _reader.GetRawMetadataSpan();
        int metadataLength = rawMetadataSpan.Length;

        // Check if any methods have bodies or need rewriting
        bool needsSharedRet = false;
        bool hasNonStrippedBodies = false;

        if (methodRowCount > 0)
        {
            for (int r = 1; r <= methodRowCount; r++)
            {
                MethodDefOverride ov = default;
                bool isMod = isMethodDefModified && _reader.TryGetMethodDefOverride(r, out ov);
                int rowOff = methodTableOffset + (r - 1) * methodRowSize;
                uint origRva = BinaryPrimitives.ReadUInt32LittleEndian(rawMetadataSpan.Slice(rowOff, 4));

                if (isMod)
                {
                    if (ov.ReplaceWithSharedRet)
                    {
                        needsSharedRet = true;
                    }
                    else if (ov.CustomBodyBytes != null)
                    {
                        hasNonStrippedBodies = true;
                    }
                }
                else if (origRva != 0)
                {
                    hasNonStrippedBodies = true;
                }
            }
        }

        const uint textRva = 0x2000;
        var origCor = peReader.PEHeaders.CorHeader ?? throw new InvalidOperationException("Missing COR header in input assembly.");

        // -------------------------------------------------------------
        // Stage 1: Precompute Section & RVA Layout in .text
        // -------------------------------------------------------------

        uint curOffset = 80; // IAT (8) + CorHeader (72)

        // Shared ret method body
        uint sharedRetRva = 0;
        if (needsSharedRet)
        {
            curOffset = (curOffset + 3u) & ~3u;
            sharedRetRva = textRva + curOffset;
            curOffset += 4; // 2 bytes [0x06, 0x2a] + 2 bytes padding
        }

        uint[]? rentedMethodRvas = null;
        if (hasNonStrippedBodies)
        {
            rentedMethodRvas = ArrayPool<uint>.Shared.Rent(methodRowCount + 1);
            Dictionary<uint, uint>? deduplicatedRvas = null;

            for (int r = 1; r <= methodRowCount; r++)
            {
                MethodDefOverride ov = default;
                bool isMod = isMethodDefModified && _reader.TryGetMethodDefOverride(r, out ov);
                int rowOff = methodTableOffset + (r - 1) * methodRowSize;
                uint origRva = BinaryPrimitives.ReadUInt32LittleEndian(rawMetadataSpan.Slice(rowOff, 4));

                if (isMod && ov.ReplaceWithSharedRet)
                {
                    rentedMethodRvas[r] = sharedRetRva;
                }
                else if (isMod && ov.ClearBody)
                {
                    rentedMethodRvas[r] = 0;
                }
                else if (isMod && ov.CustomBodyBytes != null)
                {
                    byte firstByte = ov.CustomBodyBytes.Length > 0 ? ov.CustomBodyBytes[0] : (byte)0;
                    if ((firstByte & 0x03) == 0x03) // Fat format must be 4-byte aligned
                    {
                        curOffset = (curOffset + 3u) & ~3u;
                    }
                    rentedMethodRvas[r] = textRva + curOffset;
                    curOffset += (uint)ov.CustomBodyBytes.Length;
                }
                else if (origRva != 0)
                {
                    if (deduplicatedRvas != null && deduplicatedRvas.TryGetValue(origRva, out uint existingNewRva))
                    {
                        rentedMethodRvas[r] = existingNewRva;
                    }
                    else
                    {
                        var bodyBlock = peReader.GetMethodBody((int)origRva);
                        int bodySize = bodyBlock.Size;
                        var sectionBlock = peReader.GetSectionData((int)origRva);
                        byte firstByte = sectionBlock.Length > 0 ? *(sectionBlock.Pointer) : (byte)0;
                        if ((firstByte & 0x03) == 0x03)
                        {
                            curOffset = (curOffset + 3u) & ~3u;
                        }
                        uint newRva = textRva + curOffset;
                        rentedMethodRvas[r] = newRva;
                        curOffset += (uint)bodySize;

                        deduplicatedRvas ??= new Dictionary<uint, uint>();
                        deduplicatedRvas[origRva] = newRva;
                    }
                }
                else
                {
                    rentedMethodRvas[r] = 0;
                }
            }
        }

        // StrongName signature reservation
        uint strongNameRva = 0;
        uint strongNameSize = (uint)origCor.StrongNameSignatureDirectory.Size;
        if (strongNameSize > 0)
        {
            curOffset = (curOffset + 3u) & ~3u;
            strongNameRva = textRva + curOffset;
            curOffset += strongNameSize;
        }

        // Debug Directory
        var debugEntries = peReader.ReadDebugDirectory();
        uint debugTableRva = 0;
        uint debugTableSize = 0;
        var codeViewEntry = debugEntries.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.CodeView);
        bool hasCodeView = codeViewEntry.Type == DebugDirectoryEntryType.CodeView;
        var reproEntry = debugEntries.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.Reproducible);
        bool hasRepro = reproEntry.Type == DebugDirectoryEntryType.Reproducible;

        uint debugDataRva = 0;
        uint debugDataPtr = 0;
        int debugDataLength = 0;
        PEMemoryBlock debugDataBlock = default;

        if (hasCodeView)
        {
            curOffset = (curOffset + 3u) & ~3u;
            debugTableRva = textRva + curOffset;
            debugTableSize = 28u;
            curOffset += 28;

            debugDataRva = textRva + curOffset;
            debugDataPtr = 512 + curOffset;
            debugDataLength = codeViewEntry.DataSize;
            debugDataBlock = peReader.GetSectionData(codeViewEntry.DataRelativeVirtualAddress);
            curOffset += (uint)debugDataLength;
            curOffset = (curOffset + 3u) & ~3u;
        }
        else if (hasRepro)
        {
            curOffset = (curOffset + 3u) & ~3u;
            debugTableRva = textRva + curOffset;
            debugTableSize = 28u;
            curOffset += 28;

            debugDataRva = textRva + curOffset;
            debugDataPtr = 512 + curOffset;
            if (reproEntry.DataSize > 0 && reproEntry.DataRelativeVirtualAddress != 0)
            {
                debugDataLength = reproEntry.DataSize;
                debugDataBlock = peReader.GetSectionData(reproEntry.DataRelativeVirtualAddress);
                curOffset += (uint)debugDataLength;
                curOffset = (curOffset + 3u) & ~3u;
            }
        }

        // Metadata
        curOffset = (curOffset + 3u) & ~3u;
        uint metadataRva = textRva + curOffset;
        uint metadataSize = (uint)metadataLength;
        curOffset += metadataSize;

        // Managed Resources
        uint resourcesRva = 0;
        uint resourcesSize = (uint)origCor.ResourcesDirectory.Size;
        PEMemoryBlock resBlock = default;
        if (resourcesSize > 0)
        {
            curOffset = (curOffset + 3u) & ~3u;
            resourcesRva = textRva + curOffset;
            resBlock = peReader.GetSectionData(origCor.ResourcesDirectory.RelativeVirtualAddress);
            curOffset += resourcesSize;
        }

        // Field Data & FieldRVA analysis
        int fieldRvaCount = _reader.GetTableRowCount(TableIndex.FieldRva);
        int fieldRvaTableOffset = fieldRvaCount > 0 ? _reader.GetTableOffset(TableIndex.FieldRva) : 0;
        int fieldRvaRowSize = fieldRvaCount > 0 ? _reader.GetTableRowSize(TableIndex.FieldRva) : 0;

        uint minFieldRva = uint.MaxValue;
        uint maxFieldEndRva = 0;
        if (fieldRvaCount > 0)
        {
            for (int r = 1; r <= fieldRvaCount; r++)
            {
                int rowOff = fieldRvaTableOffset + (r - 1) * fieldRvaRowSize;
                uint rva = BinaryPrimitives.ReadUInt32LittleEndian(rawMetadataSpan.Slice(rowOff, 4));
                int fieldRowId = fieldRvaRowSize == 6
                    ? BinaryPrimitives.ReadUInt16LittleEndian(rawMetadataSpan.Slice(rowOff + 4, 2))
                    : (int)BinaryPrimitives.ReadUInt32LittleEndian(rawMetadataSpan.Slice(rowOff + 4, 4));

                int fieldSize = GetFieldDataSize(fieldRowId);
                uint endRva = rva + (uint)fieldSize;

                if (rva < minFieldRva) minFieldRva = rva;
                if (endRva > maxFieldEndRva) maxFieldEndRva = endRva;
            }
        }

        uint newFieldDataRva = 0;
        int fieldRelocDelta = 0;
        int fieldCopyLen = 0;
        PEMemoryBlock fieldDataBlock = default;
        if (fieldRvaCount > 0)
        {
            curOffset = (curOffset + 7u) & ~7u; // align to 8
            newFieldDataRva = textRva + curOffset;
            fieldRelocDelta = (int)newFieldDataRva - (int)minFieldRva;

            var block = peReader.GetSectionData((int)minFieldRva);
            int fieldDataSpan = (int)(maxFieldEndRva - minFieldRva);
            fieldCopyLen = Math.Min(fieldDataSpan, block.Length);
            fieldDataBlock = block;
            curOffset += (uint)fieldCopyLen;
        }

        // Import Table & Startup Stub
        curOffset = (curOffset + 3u) & ~3u;
        uint importTableRva = textRva + curOffset;
        const uint importTableSize = 87u;

        uint iltRva = importTableRva + 40;
        uint hintNameRva = iltRva + 20;
        uint mscoreeNameRva = hintNameRva + 14;

        curOffset += 86; // raw import table size
        curOffset = (curOffset + 3u) & ~3u; // pad to 4 -> 88 bytes

        uint startupStubRva = textRva + curOffset;
        ulong imageBase = peReader.PEHeaders.PEHeader!.ImageBase;
        curOffset += 8; // startup stub

        uint textVirtualSize = curOffset;

        // Reloc section
        uint relocPage = (startupStubRva / 4096) * 4096;
        uint relocOffset = (startupStubRva + 2) % 4096;
        const uint relocVirtualSize = 12u;

        // Rsrc section
        int rsrcIdx = -1;
        for (int i = 0; i < peReader.PEHeaders.SectionHeaders.Length; i++)
        {
            if (peReader.PEHeaders.SectionHeaders[i].Name == ".rsrc")
            {
                rsrcIdx = i;
                break;
            }
        }
        PEMemoryBlock rsrcBlock = default;
        uint rsrcVirtualSize = 0;
        if (rsrcIdx >= 0)
        {
            var rsrcHdr = peReader.PEHeaders.SectionHeaders[rsrcIdx];
            rsrcBlock = peReader.GetSectionData(rsrcHdr.VirtualAddress);
            rsrcVirtualSize = (uint)rsrcBlock.Length;
        }

        // Alignments
        const uint fileAlign = 512;
        const uint secAlign = 8192;

        uint textRawSize = Align(textVirtualSize, fileAlign);
        uint relocRva = Align(textRva + textVirtualSize, secAlign);
        uint relocRawSize = Align(relocVirtualSize, fileAlign);

        uint rsrcRva = 0;
        uint rsrcRawSize = 0;
        if (rsrcIdx >= 0)
        {
            rsrcRva = Align(relocRva + relocVirtualSize, secAlign);
            rsrcRawSize = Align(rsrcVirtualSize, fileAlign);
        }

        uint numSections = rsrcIdx >= 0 ? 3u : 2u;
        uint imageSize = Align((rsrcIdx >= 0 ? rsrcRva + rsrcVirtualSize : relocRva + relocVirtualSize), secAlign);

        // -------------------------------------------------------------
        // Stage 2: In-Place Native Metadata Construction & Table Patching
        // -------------------------------------------------------------

        byte* pMeta = (byte*)NativeMemory.Alloc((nuint)metadataLength);
        try
        {
            Span<byte> metaSpan = new Span<byte>(pMeta, metadataLength);
            rawMetadataSpan.CopyTo(metaSpan);

            // MethodDef table patching
            if (methodRowCount > 0)
            {
                byte* pRow = pMeta + methodTableOffset;
                for (int r = 1; r <= methodRowCount; r++, pRow += methodRowSize)
                {
                    if (hasNonStrippedBodies)
                    {
                        uint rva = rentedMethodRvas![r];
                        ushort impl = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(pRow + 4, 2));

                        MethodDefOverride ov = default;
                        if (isMethodDefModified && _reader.TryGetMethodDefOverride(r, out ov))
                        {
                            if (ov.ReplaceWithSharedRet)
                            {
                                impl |= (ushort)(MethodImplAttributes.NoInlining | ov.ImplAttributesToAdd);
                            }
                            else
                            {
                                impl |= (ushort)ov.ImplAttributesToAdd;
                            }
                        }

                        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(pRow, 4), rva);
                        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(pRow + 4, 2), impl);
                    }
                    else
                    {
                        // Fast Path (ILStrip)
                        if (isMethodDefModified && _reader.TryGetMethodDefOverride(r, out var ov))
                        {
                            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(pRow, 4));
                            ushort impl = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(pRow + 4, 2));

                            if (ov.ReplaceWithSharedRet)
                            {
                                rva = sharedRetRva;
                                impl |= (ushort)(MethodImplAttributes.NoInlining | ov.ImplAttributesToAdd);
                            }
                            else if (ov.ClearBody)
                            {
                                rva = 0;
                                impl |= (ushort)ov.ImplAttributesToAdd;
                            }
                            else
                            {
                                impl |= (ushort)ov.ImplAttributesToAdd;
                            }

                            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(pRow, 4), rva);
                            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(pRow + 4, 2), impl);
                        }
                    }
                }
            }

            // Fast CustomAttribute bug replication
            if (ReplicateLegacyCustomAttributeBug)
            {
                int caRowCount = _reader.GetTableRowCount(TableIndex.CustomAttribute);
                if (caRowCount > 0)
                {
                    int caTableOffset = _reader.GetTableOffset(TableIndex.CustomAttribute);
                    int caRowSize = _reader.GetTableRowSize(TableIndex.CustomAttribute);

                    int maxHasCaRows = 0;
                    foreach (var t in s_hasCustomAttributeTables)
                    {
                        int cnt = _reader.GetTableRowCount(t);
                        if (cnt > maxHasCaRows) maxHasCaRows = cnt;
                    }
                    int parentWidth = maxHasCaRows < 2048 ? 2 : 4;

                    bool anyLost = false;
                    for (int i = 0; i < caRowCount; i++)
                    {
                        int rowOff = caTableOffset + i * caRowSize;
                        uint parent = parentWidth == 2
                            ? BinaryPrimitives.ReadUInt16LittleEndian(metaSpan.Slice(rowOff, 2))
                            : BinaryPrimitives.ReadUInt32LittleEndian(metaSpan.Slice(rowOff, 4));

                        uint tag = parent & 0x1F;
                        if (tag == 20 || tag == 21) // GenericParamConstraint or MethodSpec
                        {
                            anyLost = true;
                            if (parentWidth == 2)
                                BinaryPrimitives.WriteUInt16LittleEndian(metaSpan.Slice(rowOff, 2), 0);
                            else
                                BinaryPrimitives.WriteUInt32LittleEndian(metaSpan.Slice(rowOff, 4), 0);
                        }
                    }

                    if (anyLost)
                    {
                        int[] indices = ArrayPool<int>.Shared.Rent(caRowCount);
                        try
                        {
                            for (int i = 0; i < caRowCount; i++) indices[i] = i;

                            Array.Sort(indices, 0, caRowCount, new CustomAttributeComparer(pMeta, caTableOffset, caRowSize, parentWidth));

                            byte* scratch = (byte*)NativeMemory.Alloc((nuint)(caRowCount * caRowSize));
                            try
                            {
                                for (int i = 0; i < caRowCount; i++)
                                {
                                    metaSpan.Slice(caTableOffset + indices[i] * caRowSize, caRowSize)
                                            .CopyTo(new Span<byte>(scratch + i * caRowSize, caRowSize));
                                }
                                new ReadOnlySpan<byte>(scratch, caRowCount * caRowSize)
                                    .CopyTo(metaSpan.Slice(caTableOffset, caRowCount * caRowSize));
                            }
                            finally
                            {
                                NativeMemory.Free(scratch);
                            }
                        }
                        finally
                        {
                            ArrayPool<int>.Shared.Return(indices);
                        }
                    }
                }
            }

            // Fast FieldRVA patching
            if (fieldRvaCount > 0)
            {
                byte* pFieldRow = pMeta + fieldRvaTableOffset;
                for (int r = 1; r <= fieldRvaCount; r++, pFieldRow += fieldRvaRowSize)
                {
                    uint rva = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(pFieldRow, 4));
                    uint updatedRva = (uint)(rva + fieldRelocDelta);
                    BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(pFieldRow, 4), updatedRva);
                }
            }

            // -------------------------------------------------------------
            // Stage 3: Direct Sequential File Emission (Zero Intermediate Copies)
            // -------------------------------------------------------------

            // 1. PE Headers (Offset 0..512)
            Span<byte> hdr = stackalloc byte[512];
            hdr.Clear();

            // DOS Header (128 bytes)
            s_dosHeader.CopyTo(hdr.Slice(0, 128));

            // PE Signature (4 bytes) at offset 128
            hdr[128] = (byte)'P';
            hdr[129] = (byte)'E';
            hdr[130] = 0;
            hdr[131] = 0;

            // COFF Header (20 bytes) at offset 132
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(132, 2), 0x014c); // Machine.I386
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(134, 2), (ushort)numSections);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(136, 4), 0u); // TimeDateStamp
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(140, 4), 0u); // PointerToSymbolTable
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(144, 4), 0u); // NumberOfSymbols
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(148, 2), 224); // SizeOfOptionalHeader
            ushort peCharacteristics = isDll ? (ushort)0x210e : (ushort)0x010e;
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(150, 2), peCharacteristics);

            // PE Optional Header (224 bytes) at offset 152
            var origPeHdr = peReader.PEHeaders.PEHeader!;
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(152, 2), 0x010b); // Magic (PE32)
            hdr[154] = 6; // MajorLinkerVersion
            hdr[155] = 0; // MinorLinkerVersion
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(156, 4), textRawSize); // SizeOfCode
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(160, 4), textRawSize + (rsrcIdx >= 0 ? rsrcRawSize : 0)); // SizeOfInitializedData
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(164, 4), 0u); // SizeOfUninitializedData
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(168, 4), startupStubRva); // AddressOfEntryPoint
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(172, 4), textRva); // BaseOfCode
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(176, 4), relocRva); // BaseOfData

            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(180, 4), (uint)origPeHdr.ImageBase);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(184, 4), secAlign);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(188, 4), fileAlign);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(192, 2), 4); // MajorOperatingSystemVersion
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(194, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(196, 2), 0); // MajorImageVersion
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(198, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(200, 2), 4); // MajorSubsystemVersion
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(202, 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(204, 4), 0u); // Win32VersionValue
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(208, 4), imageSize);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(212, 4), 512u); // SizeOfHeaders
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(216, 4), 0u); // CheckSum
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(220, 2), (ushort)origPeHdr.Subsystem);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(222, 2), (ushort)origPeHdr.DllCharacteristics);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(224, 4), 0x00100000u); // SizeOfStackReserve
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(228, 4), 0x00001000u); // SizeOfStackCommit
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(232, 4), 0x00100000u); // SizeOfHeapReserve
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(236, 4), 0x00001000u); // SizeOfHeapCommit
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(240, 4), 0u); // LoaderFlags
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(244, 4), 16u); // NumberOfRvaAndSizes

            // 16 Data Directories starting at offset 248
            // Directory 1: Import Table (offset 256)
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(256, 4), importTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(260, 4), importTableSize);

            // Directory 2: Resource Table (offset 264)
            if (rsrcIdx >= 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(264, 4), rsrcRva);
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(268, 4), rsrcVirtualSize);
            }

            // Directory 5: Base Relocation Table (offset 288)
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(288, 4), relocRva);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(292, 4), relocVirtualSize);

            // Directory 6: Debug Directory (offset 296)
            if (debugTableRva != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(296, 4), debugTableRva);
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(300, 4), debugTableSize);
            }

            // Directory 12: IAT (offset 344)
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(344, 4), textRva);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(348, 4), 8u);

            // Directory 14: CLI Header (offset 360)
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(360, 4), textRva + 8);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(364, 4), 72u);

            // Section Headers starting at offset 376
            uint textRawPtr = 512;
            WriteSectionHeader(hdr.Slice(376, 40), ".text", textVirtualSize, textRva, textRawSize, textRawPtr, 0x60000020);

            uint relocRawPtr = textRawPtr + textRawSize;
            WriteSectionHeader(hdr.Slice(416, 40), ".reloc", relocVirtualSize, relocRva, relocRawSize, relocRawPtr, 0x42000040);

            if (rsrcIdx >= 0)
            {
                uint rsrcRawPtr = relocRawPtr + relocRawSize;
                WriteSectionHeader(hdr.Slice(456, 40), ".rsrc", rsrcVirtualSize, rsrcRva, rsrcRawSize, rsrcRawPtr, 0x40000040);
            }

            output.Write(hdr);

            // 2. .text Section Content
            uint pos = 0;

            // IAT (8 bytes)
            Span<byte> iat = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(iat.Slice(0, 4), hintNameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(iat.Slice(4, 4), 0u);
            output.Write(iat);
            pos += 8;

            // CorHeader (72 bytes)
            Span<byte> cor = stackalloc byte[72];
            cor.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(0, 4), 72u);
            BinaryPrimitives.WriteUInt16LittleEndian(cor.Slice(4, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(cor.Slice(6, 2), 5);
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(8, 4), metadataRva);
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(12, 4), metadataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(16, 4), (uint)origCor.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(20, 4), (uint)origCor.EntryPointTokenOrRelativeVirtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(24, 4), resourcesRva);
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(28, 4), resourcesSize);
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(32, 4), strongNameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(cor.Slice(36, 4), strongNameSize);
            output.Write(cor);
            pos += 72;

            // Shared ret
            if (needsSharedRet)
            {
                PadTo(output, ref pos, 4);
                Span<byte> retBuf = stackalloc byte[4] { 0x06, 0x2a, 0x00, 0x00 };
                output.Write(retBuf);
                pos += 4;
            }

            // Custom and unmodified method bodies (General Path only)
            if (hasNonStrippedBodies)
            {
                for (int r = 1; r <= methodRowCount; r++)
                {
                    uint newRva = rentedMethodRvas![r];
                    if (newRva == 0 || newRva == sharedRetRva)
                        continue;

                    if (newRva < textRva + pos)
                        continue;

                    MethodDefOverride ov = default;
                    bool isMod = isMethodDefModified && _reader.TryGetMethodDefOverride(r, out ov);
                    if (isMod && ov.CustomBodyBytes != null)
                    {
                        byte firstByte = ov.CustomBodyBytes.Length > 0 ? ov.CustomBodyBytes[0] : (byte)0;
                        if ((firstByte & 0x03) == 0x03)
                        {
                            PadTo(output, ref pos, 4);
                        }

                        output.Write(ov.CustomBodyBytes);
                        pos += (uint)ov.CustomBodyBytes.Length;
                    }
                    else
                    {
                        int rowOff = methodTableOffset + (r - 1) * methodRowSize;
                        uint origRva = BinaryPrimitives.ReadUInt32LittleEndian(rawMetadataSpan.Slice(rowOff, 4));
                        if (origRva != 0)
                        {
                            var bodyBlock = peReader.GetMethodBody((int)origRva);
                            int bodySize = bodyBlock.Size;
                            var sectionBlock = peReader.GetSectionData((int)origRva);
                            byte firstByte = sectionBlock.Length > 0 ? *(sectionBlock.Pointer) : (byte)0;
                            if ((firstByte & 0x03) == 0x03)
                            {
                                PadTo(output, ref pos, 4);
                            }

                            output.Write(new ReadOnlySpan<byte>(sectionBlock.Pointer, bodySize));
                            pos += (uint)bodySize;
                        }
                    }
                }
            }

            // StrongName
            if (strongNameSize > 0)
            {
                PadTo(output, ref pos, 4);
                WriteZeros(output, (int)strongNameSize);
                pos += strongNameSize;
            }

            // Debug Directory
            if (hasCodeView)
            {
                PadTo(output, ref pos, 4);
                Span<byte> dbg = stackalloc byte[28];
                dbg.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(dbg.Slice(12, 4), (uint)DebugDirectoryEntryType.CodeView);
                BinaryPrimitives.WriteUInt32LittleEndian(dbg.Slice(16, 4), (uint)debugDataLength);
                BinaryPrimitives.WriteUInt32LittleEndian(dbg.Slice(20, 4), debugDataRva);
                BinaryPrimitives.WriteUInt32LittleEndian(dbg.Slice(24, 4), debugDataPtr);
                output.Write(dbg);
                pos += 28;

                if (debugDataLength > 0)
                {
                    output.Write(new ReadOnlySpan<byte>(debugDataBlock.Pointer, debugDataLength));
                    pos += (uint)debugDataLength;
                    PadTo(output, ref pos, 4);
                }
            }
            else if (hasRepro)
            {
                PadTo(output, ref pos, 4);
                Span<byte> dbg = stackalloc byte[28];
                dbg.Clear();
                BinaryPrimitives.WriteUInt16LittleEndian(dbg.Slice(8, 2), reproEntry.MajorVersion);
                BinaryPrimitives.WriteUInt16LittleEndian(dbg.Slice(10, 2), reproEntry.MinorVersion);
                BinaryPrimitives.WriteUInt32LittleEndian(dbg.Slice(12, 4), (uint)DebugDirectoryEntryType.Reproducible);
                BinaryPrimitives.WriteUInt32LittleEndian(dbg.Slice(16, 4), (uint)debugDataLength);
                BinaryPrimitives.WriteUInt32LittleEndian(dbg.Slice(20, 4), debugDataRva);
                BinaryPrimitives.WriteUInt32LittleEndian(dbg.Slice(24, 4), debugDataPtr);
                output.Write(dbg);
                pos += 28;

                if (debugDataLength > 0)
                {
                    output.Write(new ReadOnlySpan<byte>(debugDataBlock.Pointer, debugDataLength));
                    pos += (uint)debugDataLength;
                    PadTo(output, ref pos, 4);
                }
            }

            // Metadata block
            PadTo(output, ref pos, 4);
            output.Write(metaSpan);
            pos += metadataSize;

            // Managed Resources
            if (resourcesSize > 0)
            {
                PadTo(output, ref pos, 4);
                output.Write(new ReadOnlySpan<byte>(resBlock.Pointer, (int)resourcesSize));
                pos += resourcesSize;
            }

            // Field Data
            if (fieldCopyLen > 0)
            {
                PadTo(output, ref pos, 8);
                output.Write(new ReadOnlySpan<byte>(fieldDataBlock.Pointer, fieldCopyLen));
                pos += (uint)fieldCopyLen;
            }

            // Import Table & Startup Stub
            PadTo(output, ref pos, 4);
            Span<byte> importBuf = stackalloc byte[96];
            importBuf.Clear();

            // Import Directory Entry (20 bytes)
            BinaryPrimitives.WriteUInt32LittleEndian(importBuf.Slice(0, 4), iltRva);
            BinaryPrimitives.WriteUInt32LittleEndian(importBuf.Slice(12, 4), mscoreeNameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(importBuf.Slice(16, 4), textRva);

            // ILT Entry (4 bytes at offset 40)
            BinaryPrimitives.WriteUInt32LittleEndian(importBuf.Slice(40, 4), hintNameRva);

            // Hint/Name (14 bytes at offset 60): hint (0), runtimeMain, \0
            Encoding.ASCII.GetBytes(runtimeMain).CopyTo(importBuf.Slice(62, 11));

            // mscoree.dll (12 bytes at offset 74): "mscoree.dll\0"
            Encoding.ASCII.GetBytes("mscoree.dll").CopyTo(importBuf.Slice(74, 11));

            // Startup Stub (8 bytes at offset 88)
            importBuf[88] = 0xff;
            importBuf[89] = 0x25;
            BinaryPrimitives.WriteUInt32LittleEndian(importBuf.Slice(90, 4), (uint)(imageBase + textRva));

            output.Write(importBuf);
            pos += 96;

            // Zero padding up to textRawSize
            if (textRawSize > pos)
            {
                WriteZeros(output, (int)(textRawSize - pos));
            }

            // 3. Reloc Section Content (512 bytes)
            Span<byte> relocBuf = stackalloc byte[512];
            relocBuf.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(relocBuf.Slice(0, 4), relocPage);
            BinaryPrimitives.WriteUInt32LittleEndian(relocBuf.Slice(4, 4), 12u);
            BinaryPrimitives.WriteUInt16LittleEndian(relocBuf.Slice(8, 2), (ushort)(0x3000 | relocOffset));
            output.Write(relocBuf);

            // 4. Rsrc Section Content (if present)
            if (rsrcIdx >= 0)
            {
                output.Write(new ReadOnlySpan<byte>(rsrcBlock.Pointer, (int)rsrcVirtualSize));
                if (rsrcRawSize > rsrcVirtualSize)
                {
                    WriteZeros(output, (int)(rsrcRawSize - rsrcVirtualSize));
                }
            }

            output.Flush();
        }
        finally
        {
            if (rentedMethodRvas != null)
            {
                ArrayPool<uint>.Shared.Return(rentedMethodRvas);
            }
            NativeMemory.Free(pMeta);
        }
    }

    private static void WriteSectionHeader(Span<byte> dest, string name, uint vSize, uint vRva, uint rawSize, uint rawPtr, uint flags)
    {
        dest.Clear();
        Encoding.ASCII.GetBytes(name).CopyTo(dest.Slice(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(8, 4), vSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(12, 4), vRva);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(16, 4), rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(20, 4), rawPtr);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(36, 4), flags);
    }

    private static void PadTo(Stream stream, ref uint curOffset, int align)
    {
        uint rem = curOffset % (uint)align;
        if (rem != 0)
        {
            uint pad = (uint)align - rem;
            WriteZeros(stream, (int)pad);
            curOffset += pad;
        }
    }

    private static void WriteZeros(Stream stream, int count)
    {
        while (count > 0)
        {
            int chunk = Math.Min(count, s_zeroBuffer.Length);
            stream.Write(s_zeroBuffer, 0, chunk);
            count -= chunk;
        }
    }

    private static uint Align(uint val, uint align)
    {
        return (val + align - 1) & ~(align - 1);
    }

    private int GetFieldDataSize(int fieldRowId)
    {
        try
        {
            var fieldHandle = MetadataTokens.FieldDefinitionHandle(fieldRowId);
            var fieldDef = _reader.MetadataReader.GetFieldDefinition(fieldHandle);
            var sigReader = _reader.MetadataReader.GetBlobReader(fieldDef.Signature);

            sigReader.ReadSignatureHeader();

            while (sigReader.RemainingBytes > 0)
            {
                byte b = sigReader.ReadByte();
                if (b == 0x20 || b == 0x1F) // OptionalModifier (0x20) or RequiredModifier (0x1F)
                {
                    sigReader.ReadTypeHandle();
                }
                else
                {
                    sigReader.Offset--;
                    break;
                }
            }

            var typeCode = sigReader.ReadSignatureTypeCode();
            switch (typeCode)
            {
                case SignatureTypeCode.Boolean:
                case SignatureTypeCode.SByte:
                case SignatureTypeCode.Byte:
                    return 1;

                case SignatureTypeCode.Char:
                case SignatureTypeCode.Int16:
                case SignatureTypeCode.UInt16:
                    return 2;

                case SignatureTypeCode.Int32:
                case SignatureTypeCode.UInt32:
                case SignatureTypeCode.Single:
                    return 4;

                case SignatureTypeCode.Int64:
                case SignatureTypeCode.UInt64:
                case SignatureTypeCode.Double:
                case SignatureTypeCode.IntPtr:
                case SignatureTypeCode.UIntPtr:
                    return 8;

                case SignatureTypeCode.TypeHandle:
                    EntityHandle typeHandle = sigReader.ReadTypeHandle();
                    if (typeHandle.Kind == HandleKind.TypeDefinition)
                    {
                        var typeDef = _reader.MetadataReader.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                        int layoutSize = typeDef.GetLayout().Size;
                        if (layoutSize > 0)
                            return layoutSize;
                    }
                    return 16;

                default:
                    return 16;
            }
        }
        catch
        {
            return 16;
        }
    }

    private readonly unsafe struct CustomAttributeComparer : IComparer<int>
    {
        private readonly byte* _pMeta;
        private readonly int _caTableOffset;
        private readonly int _caRowSize;
        private readonly int _parentWidth;

        public CustomAttributeComparer(byte* pMeta, int caTableOffset, int caRowSize, int parentWidth)
        {
            _pMeta = pMeta;
            _caTableOffset = caTableOffset;
            _caRowSize = caRowSize;
            _parentWidth = parentWidth;
        }

        public int Compare(int iA, int iB)
        {
            int offA = _caTableOffset + iA * _caRowSize;
            int offB = _caTableOffset + iB * _caRowSize;
            uint pA = _parentWidth == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(_pMeta + offA, 2))
                : BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_pMeta + offA, 4));
            uint pB = _parentWidth == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(_pMeta + offB, 2))
                : BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_pMeta + offB, 4));
            int cmp = pA.CompareTo(pB);
            return cmp != 0 ? cmp : iA.CompareTo(iB);
        }
    }
}
