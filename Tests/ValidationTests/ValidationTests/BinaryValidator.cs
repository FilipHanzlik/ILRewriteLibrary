using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace ValidationTests;

public sealed record ValidationResult(
    bool Success,
    bool NormalizedHashMatch,
    bool StructuralMatch,
    string CandidateSha256,
    string CandidateNormalizedSha256,
    long CandidateLength,
    List<string> Errors,
    List<string> Observations);

public static class BinaryValidator
{
    private const int MethodDefinitionTokenPrefix = 0x06000000;

    public static string HashBytes(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string HashFile(string path) =>
        HashBytes(File.ReadAllBytes(path));

    public static byte[] NormalizeBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using PEReader pe = new(new MemoryStream(bytes));
        foreach (var range in GetNormalizationRanges(pe))
        {
            if (range.Offset >= 0 && range.Offset + range.Length <= bytes.Length)
            {
                bytes.AsSpan(range.Offset, range.Length).Clear();
            }
        }
        return bytes;
    }

    public static string GetNormalizedSha256(string path) =>
        HashBytes(NormalizeBytes(path));

    public static ValidationResult ValidateCandidate(
        string inputPath,
        string candidatePath,
        string expectedNormalizedSha256)
    {
        List<string> errors = [];
        List<string> observations = [];

        if (!File.Exists(candidatePath))
        {
            errors.Add($"Candidate output file does not exist: {candidatePath}");
            return new ValidationResult(false, false, false, "", "", 0, errors, observations);
        }

        byte[] candidateBytes = File.ReadAllBytes(candidatePath);
        string candidateRawSha256 = HashBytes(candidateBytes);
        string candidateNormSha256 = GetNormalizedSha256(candidatePath);

        bool hashMatch = string.Equals(candidateNormSha256, expectedNormalizedSha256, StringComparison.OrdinalIgnoreCase);
        if (!hashMatch)
        {
            errors.Add($"Normalized SHA-256 mismatch! Candidate: {candidateNormSha256}, Expected: {expectedNormalizedSha256}");
        }

        bool structuralMatch = true;
        try
        {
            VerifyStructuralInvariants(inputPath, candidatePath, observations);
        }
        catch (Exception ex)
        {
            structuralMatch = false;
            errors.Add($"Structural verification failed: {ex.Message}");
        }

        bool overallSuccess = hashMatch || structuralMatch;
        return new ValidationResult(
            overallSuccess,
            hashMatch,
            structuralMatch,
            candidateRawSha256,
            candidateNormSha256,
            candidateBytes.Length,
            errors,
            observations);
    }

    public static void VerifyStructuralInvariants(string inputPath, string candidatePath, List<string> observations)
    {
        using PEReader before = new(File.OpenRead(inputPath));
        using PEReader after = new(File.OpenRead(candidatePath));
        MetadataReader left = before.GetMetadataReader();
        MetadataReader right = after.GetMetadataReader();

        // 1. MVID
        Guid leftMvid = left.GetGuid(left.GetModuleDefinition().Mvid);
        Guid rightMvid = right.GetGuid(right.GetModuleDefinition().Mvid);
        if (leftMvid != rightMvid)
            throw new InvalidOperationException($"MVID changed! Input: {leftMvid}, Candidate: {rightMvid}");

        // 2. Entry point
        int leftEntry = before.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress;
        int rightEntry = after.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress;
        if (leftEntry != rightEntry)
            throw new InvalidOperationException($"Entry point token/RVA changed! Input: {leftEntry:x8}, Candidate: {rightEntry:x8}");

        // 3. Metadata Heaps (#Strings, #GUID, #US, #Blob)
        byte[] leftMetadata = before.GetMetadata().GetContent().ToArray();
        byte[] rightMetadata = after.GetMetadata().GetContent().ToArray();
        Dictionary<string, byte[]> originalHeaps = ExtractHeaps(leftMetadata);
        Dictionary<string, byte[]> strippedHeaps = ExtractHeaps(rightMetadata);

        foreach (string heapName in new[] { "#Strings", "#GUID", "#US", "#Blob" })
        {
            byte[] orig = originalHeaps.GetValueOrDefault(heapName, []);
            byte[] strip = strippedHeaps.GetValueOrDefault(heapName, []);
            if (!orig.AsSpan().SequenceEqual(strip))
                throw new InvalidOperationException($"Metadata heap changed: {heapName} (Original size: {orig.Length}, Candidate size: {strip.Length})");
        }

        // 4. Metadata Tables
        foreach (TableIndex table in Enum.GetValues<TableIndex>())
        {
            int count = left.GetTableRowCount(table);
            int rightCount = right.GetTableRowCount(table);
            if (count != rightCount)
                throw new InvalidOperationException($"Table count changed for {table}: Original has {count}, Candidate has {rightCount}");

            if (count == 0 || table is TableIndex.MethodDef or TableIndex.FieldRva or TableIndex.ManifestResource or TableIndex.CustomAttribute)
                continue;

            int leftSize = left.GetTableRowSize(table);
            int rightSize = right.GetTableRowSize(table);
            if (leftSize != rightSize)
                throw new InvalidOperationException($"Table row width changed for {table}: Original width {leftSize}, Candidate width {rightSize}");

            ReadOnlySpan<byte> leftTable = leftMetadata.AsSpan(left.GetTableMetadataOffset(table), count * leftSize);
            ReadOnlySpan<byte> rightTable = rightMetadata.AsSpan(right.GetTableMetadataOffset(table), count * rightSize);
            if (!leftTable.SequenceEqual(rightTable))
                throw new InvalidOperationException($"Table contents changed for unmodified table {table}");
        }

        // 5. Method Bodies & RVAs
        HashSet<int> bodyRvas = [];
        foreach (MethodDefinitionHandle handle in left.MethodDefinitions)
        {
            MethodDefinition original = left.GetMethodDefinition(handle);
            MethodDefinition stripped = right.GetMethodDefinition(handle);

            if (original.Name != stripped.Name || original.Signature != stripped.Signature || original.Attributes != stripped.Attributes)
                throw new InvalidOperationException($"Method metadata definition changed unexpectedly: token {MetadataTokens.GetToken(handle):x8}");

            MethodImplAttributes expectedImpl = original.ImplAttributes | MethodImplAttributes.NoInlining;
            if (stripped.ImplAttributes != expectedImpl)
                throw new InvalidOperationException($"Method ImplAttributes missing NoInlining: token {MetadataTokens.GetToken(handle):x8}");

            if (original.RelativeVirtualAddress == 0)
            {
                if (stripped.RelativeVirtualAddress != 0)
                    throw new InvalidOperationException($"Bodyless method gained an RVA in candidate: token {MetadataTokens.GetToken(handle):x8}");
            }
            else
            {
                bodyRvas.Add(stripped.RelativeVirtualAddress);
                MethodBodyBlock? body = after.GetMethodBody(stripped.RelativeVirtualAddress);
                byte[]? ilBytes = body?.GetILBytes();
                if (ilBytes is not [0x2a])
                    throw new InvalidOperationException($"Method body does not contain single 'ret' stub (0x2a) at RVA {stripped.RelativeVirtualAddress:x8}");
            }
        }

        if (bodyRvas.Count > 1)
            throw new InvalidOperationException($"Whole-stripped methods do not share one single body RVA! Distinct RVAs: {bodyRvas.Count}");

        // 6. Managed Resources
        var leftResources = ExtractResources(before, left);
        var rightResources = ExtractResources(after, right);
        if (!leftResources.SequenceEqual(rightResources))
            throw new InvalidOperationException("Managed manifest resources changed during stripping!");

        // 7. Custom Attributes sorted by parent
        uint[] encodedParents = right.CustomAttributes.Select(h => EncodedAttributeParent(right.GetCustomAttribute(h).Parent)).ToArray();
        for (int i = 0; i < encodedParents.Length - 1; i++)
        {
            if (encodedParents[i] > encodedParents[i + 1])
                throw new InvalidOperationException($"CustomAttribute table is not sorted by encoded parent at row {i + 1}!");
        }

        // 8. Debug directory inspection
        foreach (DebugDirectoryEntry entry in before.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.CodeView)
            {
                CodeViewDebugDirectoryData originalCv = before.ReadCodeViewDebugDirectoryData(entry);
                observations.Add($"Debug CodeView original: GUID={originalCv.Guid}, Age={originalCv.Age}, Path={originalCv.Path}");
            }
        }
    }

    private static List<(int Offset, int Length, string Reason)> GetNormalizationRanges(PEReader pe)
    {
        List<(int Offset, int Length, string Reason)> ranges = [
            (pe.PEHeaders.CoffHeaderStartOffset + 4, 4, "COFF TimeDateStamp")
        ];

        int index = 0;
        foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
        {
            if (entry.Type is DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.Reproducible && !entry.IsPortableCodeView)
            {
                int debugDirOffset = GetFileOffset(pe, pe.PEHeaders.PEHeader!.DebugTableDirectory.RelativeVirtualAddress);
                ranges.Add((debugDirOffset + index * 28 + 4, 4, $"Debug[{index}].TimeDateStamp"));
            }
            index++;
        }
        return ranges;
    }

    private static Dictionary<string, byte[]> ExtractHeaps(byte[] metadata)
    {
        int versionLength = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(12, 4));
        int position = checked((16 + versionLength + 3) & ~3);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(position + 2, 2));
        position += 4;
        Dictionary<string, byte[]> streams = [];
        for (int index = 0; index < count; index++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(position, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(position + 4, 4));
            position += 8;
            int start = position;
            while (position < metadata.Length && metadata[position] != 0)
                position++;
            string name = Encoding.ASCII.GetString(metadata, start, position - start);
            position = checked((position + 1 + 3) & ~3);
            if (offset >= 0 && offset + size <= metadata.Length)
                streams.Add(name, metadata.AsSpan(offset, size).ToArray());
        }
        return streams;
    }

    private static uint EncodedAttributeParent(EntityHandle handle)
    {
        uint token = unchecked((uint)MetadataTokens.GetToken(handle));
        uint tag = (token >> 24) switch
        {
            0x06 => 0, 0x04 => 1, 0x01 => 2, 0x02 => 3, 0x08 => 4, 0x09 => 5,
            0x0a => 6, 0x00 => 7, 0x0e => 8, 0x17 => 9, 0x14 => 10, 0x11 => 11,
            0x1a => 12, 0x1b => 13, 0x20 => 14, 0x23 => 15, 0x26 => 16,
            0x27 => 17, 0x28 => 18, 0x2a => 19, 0x2c => 20, 0x2b => 21,
            _ => throw new InvalidDataException($"Unsupported custom attribute parent token {token:x8}.")
        };
        return ((token & 0x00ffffff) << 5) | tag;
    }

    private static int GetFileOffset(PEReader pe, int rva)
    {
        int index = pe.PEHeaders.GetContainingSectionIndex(rva);
        if (index < 0)
            throw new InvalidOperationException($"RVA {rva:x8} is not located within any PE section.");
        SectionHeader section = pe.PEHeaders.SectionHeaders[index];
        return checked(section.PointerToRawData + rva - section.VirtualAddress);
    }

    private static List<string> ExtractResources(PEReader pe, MetadataReader reader)
    {
        List<string> list = [];
        foreach (ManifestResourceHandle handle in reader.ManifestResources)
        {
            ManifestResource res = reader.GetManifestResource(handle);
            list.Add($"{reader.GetString(res.Name)}:{res.Attributes}:{MetadataTokens.GetToken(res.Implementation)}:{res.Offset}");
        }
        return list;
    }
}
