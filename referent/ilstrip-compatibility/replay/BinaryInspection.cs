// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace StripReplay;

internal static class BinaryInspection
{
    private const int MethodDefinitionTokenPrefix = 0x06000000;

    public static BinarySnapshot Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using PEReader pe = new(new MemoryStream(bytes));
        MetadataReader reader = pe.GetMetadataReader();
        List<MethodSnapshot> methods = [];
        foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            int rva = method.RelativeVirtualAddress;
            MethodBodyBlock? body = rva == 0 ? null : pe.GetMethodBody(rva);
            int offset = rva == 0 ? 0 : Offset(pe, rva);
            string headerKind = body is null ? "none" : (bytes[offset] & 3) == 2 ? "tiny" : "fat";
            methods.Add(new(MetadataTokens.GetToken(handle), reader.GetString(method.Name), rva, (int)method.Attributes, (int)method.ImplAttributes,
                headerKind, body?.Size ?? 0, body?.GetILBytes()?.Length ?? 0, body?.ExceptionRegions.Length ?? 0,
                body is null ? null : Program.HashBytes(body.GetILBytes() ?? [])));
        }
        List<NormalizationRange> ranges = NormalizationRanges(pe);
        List<object> debug = [];
        foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
        {
            CodeViewDebugDirectoryData? codeView = entry.Type == DebugDirectoryEntryType.CodeView ? pe.ReadCodeViewDebugDirectoryData(entry) : null;
            debug.Add(new
            {
                type = entry.Type.ToString(),
                entry.Stamp,
                entry.MajorVersion,
                entry.MinorVersion,
                entry.IsPortableCodeView,
                guid = codeView?.Guid,
                age = codeView?.Age,
                path = codeView?.Path
            });
        }

        byte[] normalized = (byte[])bytes.Clone();
        foreach (NormalizationRange range in ranges)
            normalized.AsSpan(range.Offset, range.Length).Clear();
        CorHeader cor = pe.PEHeaders.CorHeader!;
        byte[] strongName = cor.StrongNameSignatureDirectory.Size == 0 ? [] :
            pe.GetSectionData(cor.StrongNameSignatureDirectory.RelativeVirtualAddress).GetContent(0, cor.StrongNameSignatureDirectory.Size).ToArray();
        return new(
            Program.HashBytes(bytes), Program.HashBytes(normalized), bytes.Length,
            reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString(),
            pe.PEHeaders.CoffHeader.Machine.ToString(), cor.EntryPointTokenOrRelativeVirtualAddress,
            (int)cor.Flags, strongName.Length, strongName.All(value => value == 0),
            Enum.GetValues<TableIndex>().ToDictionary(table => table.ToString(), table => reader.GetTableRowCount(table)),
            methods, Resources(pe, reader), debug, ranges);
    }

    public static byte[] NormalizedBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using PEReader pe = new(new MemoryStream(bytes));
        foreach (NormalizationRange range in NormalizationRanges(pe))
            bytes.AsSpan(range.Offset, range.Length).Clear();
        return bytes;
    }

    public static List<string> VerifyWhole(string input, string output)
    {
        using PEReader before = new(File.OpenRead(input));
        using PEReader after = new(File.OpenRead(output));
        MetadataReader left = before.GetMetadataReader();
        MetadataReader right = after.GetMetadataReader();
        Program.Require(left.GetGuid(left.GetModuleDefinition().Mvid) == right.GetGuid(right.GetModuleDefinition().Mvid), $"MVID changed: {input}");
        Program.Require(before.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress == after.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress, $"Entry point changed: {input}");
        byte[] leftMetadata = before.GetMetadata().GetContent().ToArray();
        byte[] rightMetadata = after.GetMetadata().GetContent().ToArray();
        Dictionary<string, byte[]> originalHeaps = Heaps(leftMetadata);
        Dictionary<string, byte[]> strippedHeaps = Heaps(rightMetadata);
        foreach (string name in new[] { "#Strings", "#GUID", "#US", "#Blob" })
        {
            byte[] originalHeap = originalHeaps.GetValueOrDefault(name, []);
            byte[] strippedHeap = strippedHeaps.GetValueOrDefault(name, []);
            Program.Require(originalHeap.SequenceEqual(strippedHeap), $"Metadata heap changed: {name} in {input}");
        }
        foreach (TableIndex table in Enum.GetValues<TableIndex>())
        {
            int count = left.GetTableRowCount(table);
            Program.Require(count == right.GetTableRowCount(table), $"Table count changed: {table} in {input}");
            if (count == 0 || table is TableIndex.MethodDef or TableIndex.FieldRva or TableIndex.ManifestResource or TableIndex.CustomAttribute)
                continue;
            int leftSize = left.GetTableRowSize(table);
            int rightSize = right.GetTableRowSize(table);
            Program.Require(leftSize == rightSize, $"Table row width changed: {table} in {input}");
            Program.Require(leftMetadata.AsSpan(left.GetTableMetadataOffset(table), count * leftSize)
                .SequenceEqual(rightMetadata.AsSpan(right.GetTableMetadataOffset(table), count * rightSize)), $"Table contents changed: {table} in {input}");
        }
        HashSet<int> bodyRvas = [];
        foreach (MethodDefinitionHandle handle in left.MethodDefinitions)
        {
            MethodDefinition original = left.GetMethodDefinition(handle);
            MethodDefinition stripped = right.GetMethodDefinition(handle);
            Program.Require(original.Name == stripped.Name && original.Signature == stripped.Signature && original.Attributes == stripped.Attributes &&
                stripped.ImplAttributes == (original.ImplAttributes | MethodImplAttributes.NoInlining) &&
                original.GetParameters().SequenceEqual(stripped.GetParameters()), $"Method metadata changed unexpectedly: {MetadataTokens.GetToken(handle):x8} in {input}");
            if (original.RelativeVirtualAddress == 0)
                Program.Require(stripped.RelativeVirtualAddress == 0, $"Bodyless method gained a body: {input}");
            else
            {
                bodyRvas.Add(stripped.RelativeVirtualAddress);
                Program.Require(after.GetMethodBody(stripped.RelativeVirtualAddress).GetILBytes() is [0x2a], $"Expected shared ret stub: {input}");
            }
        }
        Program.Require(bodyRvas.Count <= 1, $"Whole-stripped methods do not share one body: {input}");
        Program.Require(Resources(before, left).SequenceEqual(Resources(after, right)), $"Managed resources changed: {input}");
        List<string> observations = [];
        string[] originalAttributes = left.CustomAttributes.Select(handle =>
        {
            CustomAttribute attribute = left.GetCustomAttribute(handle);
            int owner = MetadataTokens.GetToken(attribute.Parent);
            bool unsupportedOwner = attribute.Parent.Kind is HandleKind.GenericParameterConstraint or HandleKind.MethodSpecification;
            if (unsupportedOwner)
                observations.Add($"Legacy HasCustomAttribute decoder loses owner {owner:x8}; encoded output parent is MethodDef RID 0.");
            return AttributeKey(left, handle, unsupportedOwner ? MethodDefinitionTokenPrefix : null);
        }).Order(StringComparer.Ordinal).ToArray();
        string[] strippedAttributes = right.CustomAttributes.Select(handle => AttributeKey(right, handle)).Order(StringComparer.Ordinal).ToArray();
        Program.Require(originalAttributes.SequenceEqual(strippedAttributes), $"Custom attribute associations changed: {input}; removed: {string.Join(", ", originalAttributes.Except(strippedAttributes).Take(5))}; added: {string.Join(", ", strippedAttributes.Except(originalAttributes).Take(5))}");
        uint[] encodedParents = right.CustomAttributes.Select(handle => EncodedAttributeParent(right.GetCustomAttribute(handle).Parent)).ToArray();
        Program.Require(encodedParents.SequenceEqual(encodedParents.Order()), $"Custom attributes are not sorted by their encoded parent: {input}");
        return observations;
    }

    public static byte[] ExpectedSelective(string input, int[] tokens)
    {
        byte[] bytes = File.ReadAllBytes(input);
        byte[] expected = (byte[])bytes.Clone();
        using PEReader pe = new(new MemoryStream(bytes));
        MetadataReader reader = pe.GetMetadataReader();
        HashSet<int> selected = tokens.ToHashSet();
        Program.Require(selected.Count == tokens.Length, "Independent selective oracle requires unique tokens.");
        Dictionary<int, List<int>> owners = [];
        foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            int token = MetadataTokens.GetToken(handle);
            if (!owners.TryGetValue(method.RelativeVirtualAddress, out List<int>? group))
                owners.Add(method.RelativeVirtualAddress, group = []);
            group.Add(token);
        }
        foreach (int token in selected)
            Program.Require((token & unchecked((int)0xff000000)) == MethodDefinitionTokenPrefix && owners.Values.Any(group => group.Contains(token)), $"Unknown MethodDef token {token:x8}.");
        foreach ((int rva, List<int> group) in owners)
        {
            if (!group.All(selected.Contains))
                continue;
            Program.Require(rva != 0, "Bodyless methods must not be selected.");
            int size = pe.GetMethodBody(rva).Size;
            int offset = Offset(pe, rva);
            bool tiny = (bytes[offset] & 3) == 2;
            int preservedPrefix = tiny ? 1 : 4;
            if (tiny)
                expected[offset] = 2;
            expected.AsSpan(offset + preservedPrefix, size - preservedPrefix).Clear();
        }
        return expected;
    }

    private static List<ResourceSnapshot> Resources(PEReader pe, MetadataReader reader)
    {
        List<ResourceSnapshot> resources = [];
        foreach (ManifestResourceHandle handle in reader.ManifestResources)
        {
            ManifestResource resource = reader.GetManifestResource(handle);
            byte[] data = [];
            if (resource.Implementation.IsNil)
            {
                int rva = checked(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress + (int)resource.Offset);
                PEMemoryBlock block = pe.GetSectionData(rva);
                int length = BinaryPrimitives.ReadInt32LittleEndian(block.GetContent(0, 4).AsSpan());
                data = block.GetContent(4, length).ToArray();
            }
            resources.Add(new(reader.GetString(resource.Name), (int)resource.Attributes, MetadataTokens.GetToken(resource.Implementation), data.Length, Program.HashBytes(data)));
        }
        return resources;
    }

    private static string AttributeKey(MetadataReader reader, CustomAttributeHandle handle, int? ownerOverride = null)
    {
        CustomAttribute attribute = reader.GetCustomAttribute(handle);
        return $"{ownerOverride ?? MetadataTokens.GetToken(attribute.Parent):x8}/{MetadataTokens.GetToken(attribute.Constructor):x8}/{Program.HashBytes(reader.GetBlobBytes(attribute.Value))}";
    }

    private static List<NormalizationRange> NormalizationRanges(PEReader pe)
    {
        List<NormalizationRange> ranges = [new(pe.PEHeaders.CoffHeaderStartOffset + 4, 4, "COFF TimeDateStamp")];
        int index = 0;
        foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
        {
            if (entry.Type is DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.Reproducible && !entry.IsPortableCodeView)
                ranges.Add(new(Offset(pe, pe.PEHeaders.PEHeader!.DebugTableDirectory.RelativeVirtualAddress) + index * 28 + 4, 4, $"Debug[{index}].TimeDateStamp (legacy {entry.Type} entry)"));
            index++;
        }
        return ranges;
    }

    private static Dictionary<string, byte[]> Heaps(byte[] metadata)
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
            while (metadata[position] != 0)
                position++;
            string name = Encoding.ASCII.GetString(metadata, start, position - start);
            position = checked((position + 1 + 3) & ~3);
            streams.Add(name, metadata.AsSpan(offset, size).ToArray());
        }
        return streams;
    }

    private static uint EncodedAttributeParent(EntityHandle handle)
    {
        uint token = unchecked((uint)MetadataTokens.GetToken(handle));
        // ECMA-335 II.24.2.6: HasCustomAttribute uses a five-bit tag, not token ordering.
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

    private static int Offset(PEReader pe, int rva)
    {
        int index = pe.PEHeaders.GetContainingSectionIndex(rva);
        Program.Require(index >= 0, $"RVA {rva:x8} is not in a PE section.");
        SectionHeader section = pe.PEHeaders.SectionHeaders[index];
        return checked(section.PointerToRawData + rva - section.VirtualAddress);
    }
}

internal sealed record NormalizationRange(int Offset, int Length, string Reason);
internal sealed record MethodSnapshot(int Token, string Name, int Rva, int Attributes, int ImplAttributes, string HeaderKind, int BodySize, int IlLength, int ExceptionRegions, string? IlSha256);
internal sealed record ResourceSnapshot(string Name, int Attributes, int ImplementationToken, int Length, string Sha256);
internal sealed record BinarySnapshot(string Sha256, string NormalizedSha256, int Length, string Mvid, string Machine, int EntryPoint, int CorFlags, int StrongNameSignatureSize, bool StrongNameSignatureAllZero, Dictionary<string, int> TableRows, List<MethodSnapshot> Methods, List<ResourceSnapshot> Resources, List<object> DebugDirectory, List<NormalizationRange> NormalizationRanges);
