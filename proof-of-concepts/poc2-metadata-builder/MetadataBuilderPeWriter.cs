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
using System.Text;

namespace SrmMetadataBuilderRewrite;

/// <summary>
/// PE container writer driven by System.Reflection.Metadata.Ecma335.MetadataBuilder.
/// Reconstructs metadata tables and heaps via MetadataBuilder and serializes via MetadataRootBuilder.
/// </summary>
public sealed class MetadataBuilderPeWriter
{
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

    private static readonly Func<MetadataSizes, int> s_getMetadataSize =
        typeof(MetadataSizes)
            .GetProperty("MetadataSize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetMethod!.CreateDelegate<Func<MetadataSizes, int>>();

    private readonly OverlayReader _reader;

    public bool ReplicateLegacyCustomAttributeBug { get; set; } = true;
    public bool SuppressValidation { get; set; } = false;

    public MetadataBuilderPeWriter(OverlayReader reader)
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
        var mr = _reader.MetadataReader;
        var rawReader = _reader.RawTableReader;
        var origHeaders = peReader.PEHeaders;
        var origCor = origHeaders.CorHeader ?? throw new InvalidOperationException("Missing COR header in input PE.");
        bool isDll = origHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll);
        string runtimeMain = isDll ? "_CorDllMain" : "_CorExeMain";

        var mb = new MetadataBuilder();

        // -------------------------------------------------------------
        // Step 1: Heap Handle Mappings
        // -------------------------------------------------------------
        var mappedStrings = new Dictionary<StringHandle, StringHandle>();
        var mappedBlobs = new Dictionary<BlobHandle, BlobHandle>();
        var mappedGuids = new Dictionary<GuidHandle, GuidHandle>();

        StringHandle MapString(StringHandle h)
        {
            if (h.IsNil) return default;
            if (mappedStrings.TryGetValue(h, out var m)) return m;
            string s = _reader.GetString(h);
            var res = mb.GetOrAddString(s);
            mappedStrings[h] = res;
            return res;
        }

        BlobHandle MapBlob(BlobHandle h)
        {
            if (h.IsNil) return default;
            if (mappedBlobs.TryGetValue(h, out var m)) return m;
            byte[] b = _reader.GetBlobBytes(h);
            var res = mb.GetOrAddBlob(b);
            mappedBlobs[h] = res;
            return res;
        }

        GuidHandle MapGuid(GuidHandle h)
        {
            if (h.IsNil) return default;
            if (mappedGuids.TryGetValue(h, out var m)) return m;
            Guid g = _reader.GetGuid(h);
            var res = mb.GetOrAddGuid(g);
            mappedGuids[h] = res;
            return res;
        }

        // Copy UserString heap in original sequence
        int usOffset = _reader.GetHeapOffset(HeapIndex.UserString);
        int usSize = _reader.GetHeapSize(HeapIndex.UserString);
        if (usSize > 1)
        {
            byte* usPtr = mr.MetadataPointer + usOffset;
            int offset = 1;
            while (offset < usSize)
            {
                var br = new BlobReader(usPtr + offset, usSize - offset);
                int length = br.ReadCompressedInteger();
                int headerSize = (usSize - offset) - br.RemainingBytes;
                if (length == 0)
                {
                    offset += headerSize;
                    continue;
                }
                string str = br.ReadUTF16(length - 1);
                byte terminal = br.ReadByte();
                mb.GetOrAddUserString(str);
                offset += headerSize + length;
            }
        }

        // -------------------------------------------------------------
        // Step 2: Encode Method Bodies into BlobBuilder
        // -------------------------------------------------------------
        var bodyStreamBuilder = new BlobBuilder();
        var bodyEncoder = new MethodBodyStreamEncoder(bodyStreamBuilder);
        int methodCount = _reader.GetTableRowCount(TableIndex.MethodDef);
        int[] methodBodyOffsets = new int[methodCount + 1];
        var editedBodyOffsets = new Dictionary<int, int>();
        var unmodifiedRvaToNewOffset = new Dictionary<int, int>();

        for (int r = 1; r <= methodCount; r++)
        {
            var mdHandle = MetadataTokens.MethodDefinitionHandle(r);
            var method = _reader.GetMethodDefinition(mdHandle);

            if (!method.HasBody)
            {
                methodBodyOffsets[r] = -1; // Bodyless -> RVA 0
            }
            else if (!method.BodyHandle.IsNil)
            {
                int handleId = method.BodyHandle.Id;
                if (!editedBodyOffsets.TryGetValue(handleId, out int offset))
                {
                    if (_reader.TryGetRegisteredBody(method.BodyHandle, out var regBody) && regBody != null)
                    {
                        if (regBody.IsRaw && regBody.RawBytes != null)
                        {
                            if ((regBody.RawBytes[0] & 0x03) == 0x03)
                                bodyStreamBuilder.Align(4);
                            offset = bodyStreamBuilder.Count;
                            bodyStreamBuilder.WriteBytes(regBody.RawBytes);
                        }
                        else if (regBody.CodeBytes != null)
                        {
                            if (regBody.CodeBytes.Length == 1 && regBody.MaxStack <= 8 && regBody.LocalVariablesSignature.IsNil)
                            {
                                // Single-byte instruction (e.g. ret 0x2A) -> 2-byte Tiny header [0x06, 0x2A]
                                offset = bodyStreamBuilder.Count;
                                bodyStreamBuilder.WriteByte(0x06);
                                bodyStreamBuilder.WriteBytes(regBody.CodeBytes);
                            }
                            else
                            {
                                var codeBlob = new BlobBuilder();
                                codeBlob.WriteBytes(regBody.CodeBytes);
                                var inst = new InstructionEncoder(codeBlob);
                                offset = bodyEncoder.AddMethodBody(inst, regBody.MaxStack, regBody.LocalVariablesSignature, regBody.Attributes);
                            }
                        }
                        else
                        {
                            offset = -1;
                        }
                    }
                    else
                    {
                        offset = -1;
                    }
                    editedBodyOffsets[handleId] = offset;
                }
                methodBodyOffsets[r] = offset;
            }
            else if (method.RelativeVirtualAddress != 0)
            {
                int origRva = method.RelativeVirtualAddress;
                if (!unmodifiedRvaToNewOffset.TryGetValue(origRva, out int offset))
                {
                    int bodySize = peReader.GetMethodBody(origRva).Size;
                    byte* bodyPtr = (byte*)peReader.GetSectionData(origRva).Pointer;
                    if ((bodyPtr[0] & 0x03) == 0x03)
                        bodyStreamBuilder.Align(4);
                    offset = bodyStreamBuilder.Count;
                    bodyStreamBuilder.WriteBytes(bodyPtr, bodySize);
                    unmodifiedRvaToNewOffset[origRva] = offset;
                }
                methodBodyOffsets[r] = offset;
            }
            else
            {
                methodBodyOffsets[r] = -1;
            }
        }

        // -------------------------------------------------------------
        // Step 3: Populate All 34 ECMA-335 Tables in MetadataBuilder
        // -------------------------------------------------------------

        // 0x00: Module
        var origMod = mr.GetModuleDefinition();
        mb.AddModule(origMod.Generation, MapString(origMod.Name), MapGuid(origMod.Mvid), MapGuid(origMod.GenerationId), MapGuid(origMod.BaseGenerationId));

        // 0x01: TypeRef
        int typeRefCount = mr.GetTableRowCount(TableIndex.TypeRef);
        for (int r = 1; r <= typeRefCount; r++)
        {
            var tr = mr.GetTypeReference(MetadataTokens.TypeReferenceHandle(r));
            mb.AddTypeReference(tr.ResolutionScope, MapString(tr.Namespace), MapString(tr.Name));
        }

        // 0x02: TypeDef
        int typeDefCount = mr.GetTableRowCount(TableIndex.TypeDef);
        for (int r = 1; r <= typeDefCount; r++)
        {
            var td = _reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(r));
            mb.AddTypeDefinition(
                td.Attributes,
                MapString(td.Namespace),
                MapString(td.Name),
                td.BaseType,
                MetadataTokens.FieldDefinitionHandle(td.FieldStartRow),
                MetadataTokens.MethodDefinitionHandle(td.MethodStartRow));
        }

        // 0x04: Field
        int fieldCount = mr.GetTableRowCount(TableIndex.Field);
        for (int r = 1; r <= fieldCount; r++)
        {
            var fd = _reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(r));
            mb.AddFieldDefinition(fd.Attributes, MapString(fd.Name), MapBlob(fd.Signature));
        }

        // 0x06: MethodDef
        for (int r = 1; r <= methodCount; r++)
        {
            var md = _reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(r));
            rawReader.ReadMethodDefParamList(r, out int paramListIndex);
            mb.AddMethodDefinition(
                md.Attributes,
                md.ImplAttributes,
                MapString(md.Name),
                MapBlob(md.Signature),
                methodBodyOffsets[r],
                MetadataTokens.ParameterHandle(paramListIndex));
        }

        // 0x08: Param
        int paramCount = mr.GetTableRowCount(TableIndex.Param);
        for (int r = 1; r <= paramCount; r++)
        {
            var p = mr.GetParameter(MetadataTokens.ParameterHandle(r));
            mb.AddParameter(p.Attributes, MapString(p.Name), p.SequenceNumber);
        }

        // 0x09: InterfaceImpl
        int ifaceCount = mr.GetTableRowCount(TableIndex.InterfaceImpl);
        for (int r = 1; r <= ifaceCount; r++)
        {
            rawReader.ReadInterfaceImpl(r, out var classType, out var ifaceHandle);
            mb.AddInterfaceImplementation(classType, ifaceHandle);
        }

        // 0x0A: MemberRef
        int memberRefCount = mr.GetTableRowCount(TableIndex.MemberRef);
        for (int r = 1; r <= memberRefCount; r++)
        {
            var mrRef = mr.GetMemberReference(MetadataTokens.MemberReferenceHandle(r));
            mb.AddMemberReference(mrRef.Parent, MapString(mrRef.Name), MapBlob(mrRef.Signature));
        }

        // 0x0B: Constant
        int constCount = mr.GetTableRowCount(TableIndex.Constant);
        for (int r = 1; r <= constCount; r++)
        {
            var c = mr.GetConstant(MetadataTokens.ConstantHandle(r));
            var br = mr.GetBlobReader(c.Value);
            object? val = br.ReadConstant(c.TypeCode);
            mb.AddConstant(c.Parent, val);
        }

        // 0x0C: CustomAttribute
        int caCount = mr.GetTableRowCount(TableIndex.CustomAttribute);
        for (int r = 1; r <= caCount; r++)
        {
            var ca = mr.GetCustomAttribute(MetadataTokens.CustomAttributeHandle(r));
            EntityHandle parent = ca.Parent;
            if (ReplicateLegacyCustomAttributeBug &&
                (parent.Kind == HandleKind.GenericParameterConstraint || parent.Kind == HandleKind.MethodSpecification))
            {
                parent = MetadataTokens.MethodDefinitionHandle(0);
            }
            mb.AddCustomAttribute(parent, ca.Constructor, MapBlob(ca.Value));
        }

        // 0x0D: FieldMarshal
        int fmCount = mr.GetTableRowCount(TableIndex.FieldMarshal);
        for (int r = 1; r <= fmCount; r++)
        {
            rawReader.ReadFieldMarshal(r, out var parent, out var nativeType);
            mb.AddMarshallingDescriptor(parent, MapBlob(nativeType));
        }

        // 0x0E: DeclSecurity
        int dsCount = mr.GetTableRowCount(TableIndex.DeclSecurity);
        for (int r = 1; r <= dsCount; r++)
        {
            var ds = mr.GetDeclarativeSecurityAttribute(MetadataTokens.DeclarativeSecurityAttributeHandle(r));
            mb.AddDeclarativeSecurityAttribute(ds.Parent, ds.Action, MapBlob(ds.PermissionSet));
        }

        // 0x0F: ClassLayout
        int clCount = mr.GetTableRowCount(TableIndex.ClassLayout);
        for (int r = 1; r <= clCount; r++)
        {
            rawReader.ReadClassLayout(r, out ushort pack, out uint size, out var parent);
            mb.AddTypeLayout(parent, pack, size);
        }

        // 0x10: FieldLayout
        int flCount = mr.GetTableRowCount(TableIndex.FieldLayout);
        for (int r = 1; r <= flCount; r++)
        {
            rawReader.ReadFieldLayout(r, out uint offsetVal, out var field);
            mb.AddFieldLayout(field, (int)offsetVal);
        }

        // 0x11: StandAloneSig
        int sasCount = mr.GetTableRowCount(TableIndex.StandAloneSig);
        for (int r = 1; r <= sasCount; r++)
        {
            var sas = mr.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(r));
            mb.AddStandaloneSignature(MapBlob(sas.Signature));
        }

        // 0x12: EventMap
        int emCount = mr.GetTableRowCount(TableIndex.EventMap);
        for (int r = 1; r <= emCount; r++)
        {
            rawReader.ReadEventMap(r, out var parent, out var eventList);
            mb.AddEventMap(parent, eventList);
        }

        // 0x14: Event
        int evCount = mr.GetTableRowCount(TableIndex.Event);
        for (int r = 1; r <= evCount; r++)
        {
            var ev = mr.GetEventDefinition(MetadataTokens.EventDefinitionHandle(r));
            mb.AddEvent(ev.Attributes, MapString(ev.Name), ev.Type);
        }

        // 0x15: PropertyMap
        int pmCount = mr.GetTableRowCount(TableIndex.PropertyMap);
        for (int r = 1; r <= pmCount; r++)
        {
            rawReader.ReadPropertyMap(r, out var parent, out var propList);
            mb.AddPropertyMap(parent, propList);
        }

        // 0x17: Property
        int propCount = mr.GetTableRowCount(TableIndex.Property);
        for (int r = 1; r <= propCount; r++)
        {
            var pr = mr.GetPropertyDefinition(MetadataTokens.PropertyDefinitionHandle(r));
            mb.AddProperty(pr.Attributes, MapString(pr.Name), MapBlob(pr.Signature));
        }

        // 0x18: MethodSemantics
        int msCount = mr.GetTableRowCount(TableIndex.MethodSemantics);
        for (int r = 1; r <= msCount; r++)
        {
            rawReader.ReadMethodSemantics(r, out var semantics, out var method, out var assoc);
            mb.AddMethodSemantics(assoc, semantics, method);
        }

        // 0x19: MethodImpl
        int miCount = mr.GetTableRowCount(TableIndex.MethodImpl);
        for (int r = 1; r <= miCount; r++)
        {
            rawReader.ReadMethodImpl(r, out var type, out var methodBody, out var methodDecl);
            mb.AddMethodImplementation(type, methodBody, methodDecl);
        }

        // 0x1A: ModuleRef
        int modRefCount = mr.GetTableRowCount(TableIndex.ModuleRef);
        for (int r = 1; r <= modRefCount; r++)
        {
            var mref = mr.GetModuleReference(MetadataTokens.ModuleReferenceHandle(r));
            mb.AddModuleReference(MapString(mref.Name));
        }

        // 0x1B: TypeSpec
        int tsCount = mr.GetTableRowCount(TableIndex.TypeSpec);
        for (int r = 1; r <= tsCount; r++)
        {
            var ts = mr.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(r));
            mb.AddTypeSpecification(MapBlob(ts.Signature));
        }

        // 0x1C: ImplMap
        int imCount = mr.GetTableRowCount(TableIndex.ImplMap);
        for (int r = 1; r <= imCount; r++)
        {
            rawReader.ReadImplMap(r, out var attrs, out var method, out var importName, out var importScope);
            mb.AddMethodImport(method, attrs, MapString(importName), importScope);
        }

        // 0x1D: FieldRva
        int frvaCount = mr.GetTableRowCount(TableIndex.FieldRva);
        uint minFieldRva = uint.MaxValue;
        uint maxFieldEndRva = 0;
        if (frvaCount > 0)
        {
            for (int r = 1; r <= frvaCount; r++)
            {
                rawReader.ReadFieldRva(r, out uint rva, out var field);
                minFieldRva = Math.Min(minFieldRva, rva);
                int size = GetFieldDataSize(mr, field);
                maxFieldEndRva = Math.Max(maxFieldEndRva, rva + (uint)size);
            }

            for (int r = 1; r <= frvaCount; r++)
            {
                rawReader.ReadFieldRva(r, out uint rva, out var field);
                int offsetVal = (int)(rva - minFieldRva);
                mb.AddFieldRelativeVirtualAddress(field, offsetVal);
            }
        }

        // 0x20: Assembly
        if (mr.IsAssembly)
        {
            var asm = mr.GetAssemblyDefinition();
            mb.AddAssembly(MapString(asm.Name), asm.Version, MapString(asm.Culture), MapBlob(asm.PublicKey), asm.Flags, asm.HashAlgorithm);
        }

        // 0x23: AssemblyRef
        int asmRefCount = mr.GetTableRowCount(TableIndex.AssemblyRef);
        for (int r = 1; r <= asmRefCount; r++)
        {
            var ar = mr.GetAssemblyReference(MetadataTokens.AssemblyReferenceHandle(r));
            mb.AddAssemblyReference(MapString(ar.Name), ar.Version, MapString(ar.Culture), MapBlob(ar.PublicKeyOrToken), ar.Flags, MapBlob(ar.HashValue));
        }

        // 0x26: File
        int fileCount = mr.GetTableRowCount(TableIndex.File);
        for (int r = 1; r <= fileCount; r++)
        {
            var file = mr.GetAssemblyFile(MetadataTokens.AssemblyFileHandle(r));
            mb.AddAssemblyFile(MapString(file.Name), MapBlob(file.HashValue), file.ContainsMetadata);
        }

        // 0x27: ExportedType
        int etCount = mr.GetTableRowCount(TableIndex.ExportedType);
        for (int r = 1; r <= etCount; r++)
        {
            var et = mr.GetExportedType(MetadataTokens.ExportedTypeHandle(r));
            mb.AddExportedType(et.Attributes, MapString(et.Namespace), MapString(et.Name), et.Implementation, et.GetTypeDefinitionId());
        }

        // 0x28: ManifestResource
        int resCount = mr.GetTableRowCount(TableIndex.ManifestResource);
        for (int r = 1; r <= resCount; r++)
        {
            var res = mr.GetManifestResource(MetadataTokens.ManifestResourceHandle(r));
            mb.AddManifestResource(res.Attributes, MapString(res.Name), res.Implementation, (uint)res.Offset);
        }

        // 0x29: NestedClass
        int ncCount = mr.GetTableRowCount(TableIndex.NestedClass);
        for (int r = 1; r <= ncCount; r++)
        {
            rawReader.ReadNestedClass(r, out var nested, out var enclosing);
            mb.AddNestedType(nested, enclosing);
        }

        // 0x2A: GenericParam
        int gpCount = mr.GetTableRowCount(TableIndex.GenericParam);
        for (int r = 1; r <= gpCount; r++)
        {
            var gp = mr.GetGenericParameter(MetadataTokens.GenericParameterHandle(r));
            mb.AddGenericParameter(gp.Parent, gp.Attributes, MapString(gp.Name), gp.Index);
        }

        // 0x2B: MethodSpec
        int msSpecCount = mr.GetTableRowCount(TableIndex.MethodSpec);
        for (int r = 1; r <= msSpecCount; r++)
        {
            var ms = mr.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(r));
            mb.AddMethodSpecification(ms.Method, MapBlob(ms.Signature));
        }

        // 0x2C: GenericParamConstraint
        int gpcCount = mr.GetTableRowCount(TableIndex.GenericParamConstraint);
        for (int r = 1; r <= gpcCount; r++)
        {
            var gpc = mr.GetGenericParameterConstraint(MetadataTokens.GenericParameterConstraintHandle(r));
            mb.AddGenericParameterConstraint(gpc.Parameter, gpc.Type);
        }

        // -------------------------------------------------------------
        // Step 4: Precompute PE Section Layout (.text, .reloc, .rsrc)
        // -------------------------------------------------------------
        const uint textRva = 0x2000;
        uint curOffset = 80; // IAT (8) + CorHeader (72)

        // Method bodies
        uint methodBodyStreamRva = 0;
        if (bodyStreamBuilder.Count > 0)
        {
            curOffset = (curOffset + 3u) & ~3u;
            methodBodyStreamRva = textRva + curOffset;
            curOffset += (uint)bodyStreamBuilder.Count;
        }

        // Strong name signature
        uint strongNameRva = 0;
        int strongNameSize = origCor.StrongNameSignatureDirectory.Size;
        if (strongNameSize > 0)
        {
            curOffset = (curOffset + 3u) & ~3u;
            strongNameRva = textRva + curOffset;
            curOffset += (uint)strongNameSize;
        }

        // Debug directory
        var debugEntries = peReader.ReadDebugDirectory();
        bool hasCodeView = false;
        bool hasReproducible = false;
        CodeViewDebugDirectoryData codeViewData = default;
        int debugEntryCount = 0;

        foreach (var entry in debugEntries)
        {
            if (entry.Type == DebugDirectoryEntryType.CodeView)
            {
                hasCodeView = true;
                codeViewData = peReader.ReadCodeViewDebugDirectoryData(entry);
                debugEntryCount++;
            }
            else if (entry.Type == DebugDirectoryEntryType.Reproducible)
            {
                hasReproducible = true;
                debugEntryCount++;
            }
        }

        uint debugDirRva = 0;
        uint debugDataRva = 0;
        int debugDirSize = 0;
        int codeViewDataSize = 0;

        if (debugEntryCount > 0)
        {
            curOffset = (curOffset + 3u) & ~3u;
            debugDirRva = textRva + curOffset;
            debugDirSize = debugEntryCount * 28;
            curOffset += (uint)debugDirSize;

            if (hasCodeView)
            {
                codeViewDataSize = 24 + Encoding.UTF8.GetByteCount(codeViewData.Path) + 1;
                debugDataRva = textRva + curOffset;
                curOffset += (uint)codeViewDataSize;
            }
        }

        // Metadata root serialization
        curOffset = (curOffset + 3u) & ~3u;
        uint metadataRva = textRva + curOffset;

        var rootBuilder = new MetadataRootBuilder(mb, mr.MetadataVersion, suppressValidation: SuppressValidation);
        int metadataLength = s_getMetadataSize(rootBuilder.Sizes);
        curOffset += (uint)metadataLength;

        // Managed manifest resources
        uint resourceRva = 0;
        int resourceLength = origCor.ResourcesDirectory.Size;
        byte* resourcePtr = null;
        if (resourceLength > 0 && origCor.ResourcesDirectory.RelativeVirtualAddress != 0)
        {
            curOffset = (curOffset + 3u) & ~3u;
            resourceRva = textRva + curOffset;
            resourcePtr = (byte*)peReader.GetSectionData(origCor.ResourcesDirectory.RelativeVirtualAddress).Pointer;
            curOffset += (uint)resourceLength;
        }

        // Field data
        uint mappedFieldDataStreamRva = 0;
        int fieldCopyLen = 0;
        byte* fieldDataPtr = null;
        if (frvaCount > 0 && minFieldRva != uint.MaxValue)
        {
            curOffset = (curOffset + 3u) & ~3u;
            mappedFieldDataStreamRva = textRva + curOffset;
            var fieldBlock = peReader.GetSectionData((int)minFieldRva);
            fieldDataPtr = (byte*)fieldBlock.Pointer;
            fieldCopyLen = Math.Min((int)(maxFieldEndRva - minFieldRva), fieldBlock.Length);
            curOffset += (uint)fieldCopyLen;
        }

        // Re-serialize final metadata root with actual mappedFieldDataStreamRva
        var finalMetadataBlob = new BlobBuilder();
        rootBuilder.Serialize(finalMetadataBlob, (int)methodBodyStreamRva, (int)mappedFieldDataStreamRva);

        // Import Table & Startup Stub
        curOffset = (curOffset + 15u) & ~15u;
        uint importTableRva = textRva + curOffset;
        uint iltRva = importTableRva + 40;
        uint hintNameRva = iltRva + 16;
        uint dllNameRva = hintNameRva + 14;
        curOffset += 40 + 16 + 14 + 14;

        curOffset = (curOffset + 3u) & ~3u;
        uint startupStubRva = textRva + curOffset;
        curOffset += 6;

        uint textVirtualSize = curOffset;
        uint textRawSize = AlignUp(textVirtualSize, 512);

        // .reloc Section
        uint relocRva = AlignUp(textRva + textVirtualSize, 8192);
        uint relocVirtualSize = 12;
        uint relocRawSize = AlignUp(relocVirtualSize, 512);

        // .rsrc Section
        var rsrcSec = origHeaders.SectionHeaders.FirstOrDefault(s => s.Name == ".rsrc");
        bool hasRsrc = rsrcSec.SizeOfRawData > 0;
        uint rsrcRva = 0;
        uint rsrcVirtualSize = 0;
        uint rsrcRawSize = 0;
        byte* rsrcPtr = null;

        if (hasRsrc)
        {
            rsrcRva = AlignUp(relocRva + relocVirtualSize, 8192);
            rsrcVirtualSize = (uint)rsrcSec.VirtualSize;
            rsrcRawSize = (uint)rsrcSec.SizeOfRawData;
            rsrcPtr = (byte*)peReader.GetSectionData(rsrcSec.VirtualAddress).Pointer;
        }

        ushort sectionCount = (ushort)(hasRsrc ? 3 : 2);
        uint totalImageSize = (hasRsrc ? rsrcRva + AlignUp(rsrcVirtualSize, 8192) : relocRva + AlignUp(relocVirtualSize, 8192));
        uint entryPointRva = isDll ? 0 : startupStubRva;

        // -------------------------------------------------------------
        // Step 5: Direct Sequential File Emission
        // -------------------------------------------------------------

        // 5.1 DOS Header & Stub
        output.Write(s_dosHeader);

        // 5.2 PE Signature & COFF Header
        Span<byte> coffBuf = stackalloc byte[24];
        coffBuf[0] = 0x50; coffBuf[1] = 0x45; coffBuf[2] = 0x00; coffBuf[3] = 0x00; // PE\0\0
        BinaryPrimitives.WriteUInt16LittleEndian(coffBuf.Slice(4), (ushort)Machine.I386);
        BinaryPrimitives.WriteUInt16LittleEndian(coffBuf.Slice(6), sectionCount);
        BinaryPrimitives.WriteUInt32LittleEndian(coffBuf.Slice(8), 0); // Normalized timestamp
        BinaryPrimitives.WriteUInt32LittleEndian(coffBuf.Slice(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(coffBuf.Slice(16), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(coffBuf.Slice(20), 224); // SizeOfOptionalHeader
        ushort characteristics = (ushort)(Characteristics.ExecutableImage | Characteristics.LargeAddressAware);
        if (isDll) characteristics |= (ushort)Characteristics.Dll;
        BinaryPrimitives.WriteUInt16LittleEndian(coffBuf.Slice(22), characteristics);
        output.Write(coffBuf);

        // 5.3 Optional Header (Standard + Windows-Specific + Data Directories)
        Span<byte> optBuf = stackalloc byte[224];
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(0), 0x010B); // PE32
        optBuf[2] = 48; optBuf[3] = 0; // Linker version
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(4), textRawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(16), entryPointRva);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(20), textRva);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(24), textRva + textVirtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(28), 0x00400000); // ImageBase
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(32), 8192); // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(36), 512);  // FileAlignment
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(40), 4); // MajorOS
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(42), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(44), 0); // MajorImage
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(46), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(48), 4); // MajorSubsystem
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(50), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(52), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(56), totalImageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(60), 512); // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(64), 0);   // CheckSum
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(68), (ushort)Subsystem.WindowsCui);
        BinaryPrimitives.WriteUInt16LittleEndian(optBuf.Slice(70), (ushort)(DllCharacteristics.DynamicBase | DllCharacteristics.NxCompatible | DllCharacteristics.NoSeh | DllCharacteristics.TerminalServerAware));
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(72), 0x00100000); // SizeOfStackReserve
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(76), 0x1000);     // SizeOfStackCommit
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(80), 0x00100000); // SizeOfHeapReserve
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(84), 0x1000);     // SizeOfHeapCommit
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(88), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(optBuf.Slice(92), 16); // NumberOfRvaAndSizes

        // Data Directories
        WriteDataDir(optBuf.Slice(96 + 8 * 1), importTableRva, 40); // Import
        if (hasRsrc) WriteDataDir(optBuf.Slice(96 + 8 * 2), rsrcRva, rsrcVirtualSize); // Resource
        WriteDataDir(optBuf.Slice(96 + 8 * 5), relocRva, relocVirtualSize); // BaseRelocation
        if (debugDirSize > 0) WriteDataDir(optBuf.Slice(96 + 8 * 6), debugDirRva, (uint)debugDirSize); // Debug
        WriteDataDir(optBuf.Slice(96 + 8 * 12), textRva, 8); // IAT
        WriteDataDir(optBuf.Slice(96 + 8 * 14), textRva + 8, 72); // CLR Runtime Header
        output.Write(optBuf);

        // 5.4 Section Headers
        uint currentRawOffset = 512;
        output.Write(BuildSectionHeader(".text", textVirtualSize, textRva, textRawSize, currentRawOffset, SectionCharacteristics.ContainsCode | SectionCharacteristics.MemExecute | SectionCharacteristics.MemRead));
        currentRawOffset += textRawSize;

        output.Write(BuildSectionHeader(".reloc", relocVirtualSize, relocRva, relocRawSize, currentRawOffset, SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemDiscardable | SectionCharacteristics.MemRead));
        currentRawOffset += relocRawSize;

        if (hasRsrc)
        {
            output.Write(BuildSectionHeader(".rsrc", rsrcVirtualSize, rsrcRva, rsrcRawSize, currentRawOffset, SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead));
            currentRawOffset += rsrcRawSize;
        }

        // Pad headers to 512
        PadTo(output, 512);

        // 5.5 Emit .text Section Content
        long textStartPos = output.Position;

        // IAT (8 bytes)
        Span<byte> iatBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(iatBuf, hintNameRva);
        BinaryPrimitives.WriteUInt32LittleEndian(iatBuf.Slice(4), 0);
        output.Write(iatBuf);

        // COR Header (72 bytes)
        Span<byte> corBuf = stackalloc byte[72];
        BinaryPrimitives.WriteUInt32LittleEndian(corBuf, 72); // cb
        BinaryPrimitives.WriteUInt16LittleEndian(corBuf.Slice(4), 2); // MajorRuntimeVersion
        BinaryPrimitives.WriteUInt16LittleEndian(corBuf.Slice(6), 5); // MinorRuntimeVersion
        WriteDataDir(corBuf.Slice(8), metadataRva, (uint)finalMetadataBlob.Count); // Metadata
        BinaryPrimitives.WriteUInt32LittleEndian(corBuf.Slice(16), (uint)origCor.Flags); // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(corBuf.Slice(20), (uint)origCor.EntryPointTokenOrRelativeVirtualAddress); // EntryPoint
        WriteDataDir(corBuf.Slice(24), resourceRva, (uint)resourceLength); // Resources
        WriteDataDir(corBuf.Slice(32), strongNameRva, (uint)strongNameSize); // StrongName
        output.Write(corBuf);

        // Method bodies
        if (bodyStreamBuilder.Count > 0)
        {
            PadPosition(output, textStartPos, methodBodyStreamRva - textRva);
            bodyStreamBuilder.WriteContentTo(output);
        }

        // StrongName zeroes
        if (strongNameSize > 0)
        {
            PadPosition(output, textStartPos, strongNameRva - textRva);
            WriteZeroes(output, strongNameSize);
        }

        // Debug Directory
        if (debugDirSize > 0)
        {
            PadPosition(output, textStartPos, debugDirRva - textRva);
            if (hasCodeView)
            {
                Span<byte> cvEntry = stackalloc byte[28];
                BinaryPrimitives.WriteUInt32LittleEndian(cvEntry, 0); // Characteristics
                BinaryPrimitives.WriteUInt32LittleEndian(cvEntry.Slice(4), 0); // Normalized timestamp
                BinaryPrimitives.WriteUInt16LittleEndian(cvEntry.Slice(8), 0); // MajorVersion
                BinaryPrimitives.WriteUInt16LittleEndian(cvEntry.Slice(10), 0); // MinorVersion
                BinaryPrimitives.WriteUInt32LittleEndian(cvEntry.Slice(12), (uint)DebugDirectoryEntryType.CodeView);
                BinaryPrimitives.WriteUInt32LittleEndian(cvEntry.Slice(16), (uint)codeViewDataSize);
                BinaryPrimitives.WriteUInt32LittleEndian(cvEntry.Slice(20), debugDataRva);
                BinaryPrimitives.WriteUInt32LittleEndian(cvEntry.Slice(24), 512 + (debugDataRva - textRva));
                output.Write(cvEntry);
            }
            if (hasReproducible)
            {
                Span<byte> repEntry = stackalloc byte[28];
                BinaryPrimitives.WriteUInt32LittleEndian(repEntry.Slice(12), (uint)DebugDirectoryEntryType.Reproducible);
                output.Write(repEntry);
            }

            if (hasCodeView)
            {
                PadPosition(output, textStartPos, debugDataRva - textRva);
                Span<byte> cvHead = stackalloc byte[24];
                cvHead[0] = (byte)'R'; cvHead[1] = (byte)'S'; cvHead[2] = (byte)'D'; cvHead[3] = (byte)'S';
                codeViewData.Guid.TryWriteBytes(cvHead.Slice(4, 16));
                BinaryPrimitives.WriteInt32LittleEndian(cvHead.Slice(20), codeViewData.Age);
                output.Write(cvHead);
                byte[] pathBytes = Encoding.UTF8.GetBytes(codeViewData.Path);
                output.Write(pathBytes);
                output.WriteByte(0); // Null terminator
            }
        }

        // Metadata root
        PadPosition(output, textStartPos, metadataRva - textRva);
        finalMetadataBlob.WriteContentTo(output);

        // Managed manifest resources
        if (resourceLength > 0 && resourcePtr != null)
        {
            PadPosition(output, textStartPos, resourceRva - textRva);
            output.Write(new ReadOnlySpan<byte>(resourcePtr, resourceLength));
        }

        // Field data
        if (fieldCopyLen > 0 && fieldDataPtr != null)
        {
            PadPosition(output, textStartPos, mappedFieldDataStreamRva - textRva);
            output.Write(new ReadOnlySpan<byte>(fieldDataPtr, fieldCopyLen));
        }

        // Import Table & Startup Stub
        PadPosition(output, textStartPos, importTableRva - textRva);
        Span<byte> impBuf = stackalloc byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(impBuf, iltRva);
        BinaryPrimitives.WriteUInt32LittleEndian(impBuf.Slice(12), dllNameRva);
        BinaryPrimitives.WriteUInt32LittleEndian(impBuf.Slice(16), textRva); // FirstThunk -> IAT
        output.Write(impBuf);

        // ILT
        Span<byte> iltBuf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(iltBuf, hintNameRva);
        output.Write(iltBuf);

        // Hint/Name
        Span<byte> hnBuf = stackalloc byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(hnBuf, 0); // Hint
        Encoding.ASCII.GetBytes(runtimeMain).CopyTo(hnBuf.Slice(2));
        output.Write(hnBuf);

        // DLL Name (mscoree.dll\0)
        Span<byte> dllBuf = stackalloc byte[14];
        Encoding.ASCII.GetBytes("mscoree.dll\0").CopyTo(dllBuf);
        output.Write(dllBuf);

        // Startup Stub (x86: jmp dword ptr [0x402000])
        PadPosition(output, textStartPos, startupStubRva - textRva);
        Span<byte> stubBuf = stackalloc byte[6];
        stubBuf[0] = 0xFF; stubBuf[1] = 0x25;
        BinaryPrimitives.WriteUInt32LittleEndian(stubBuf.Slice(2), 0x00400000 + textRva);
        output.Write(stubBuf);

        // Pad .text to textRawSize
        PadTo(output, 512 + textRawSize);

        // 5.6 Emit .reloc Section
        Span<byte> relocBuf = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(relocBuf, textRva + (startupStubRva - textRva) + 2); // Target RVA
        BinaryPrimitives.WriteUInt32LittleEndian(relocBuf.Slice(4), 12); // Block size
        BinaryPrimitives.WriteUInt16LittleEndian(relocBuf.Slice(8), 0x3002); // HIGHLOW at offset 2
        BinaryPrimitives.WriteUInt16LittleEndian(relocBuf.Slice(10), 0); // Absolute padding
        output.Write(relocBuf);
        PadTo(output, 512 + textRawSize + relocRawSize);

        // 5.7 Emit .rsrc Section
        if (hasRsrc && rsrcPtr != null)
        {
            output.Write(new ReadOnlySpan<byte>(rsrcPtr, (int)rsrcRawSize));
        }

        output.Flush();
    }

    private static int GetFieldDataSize(MetadataReader mr, FieldDefinitionHandle fieldHandle)
    {
        try
        {
            var field = mr.GetFieldDefinition(fieldHandle);
            var sigReader = mr.GetBlobReader(field.Signature);
            byte prolog = sigReader.ReadByte(); // 0x06 FieldSig
            while (sigReader.RemainingBytes > 0)
            {
                byte b = sigReader.ReadByte();
                if (b == 0x1F || b == 0x20) // Custom modifier
                {
                    sigReader.ReadCompressedInteger();
                    continue;
                }
                switch (b)
                {
                    case 0x02: // Boolean
                    case 0x04: // SByte
                    case 0x05: // Byte
                        return 1;
                    case 0x03: // Char
                    case 0x06: // Int16
                    case 0x07: // UInt16
                        return 2;
                    case 0x08: // Int32
                    case 0x09: // UInt32
                    case 0x0C: // Single
                        return 4;
                    case 0x0A: // Int64
                    case 0x0B: // UInt64
                    case 0x0D: // Double
                        return 8;
                    case 0x18: // IntPtr
                    case 0x19: // UIntPtr
                        return 8;
                    case 0x11: // ValueType
                        var typeHandle = sigReader.ReadTypeHandle();
                        if (typeHandle.Kind == HandleKind.TypeDefinition)
                        {
                            var typeDef = mr.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                            int layoutSize = typeDef.GetLayout().Size;
                            if (layoutSize > 0) return layoutSize;
                        }
                        return 16;
                    default:
                        return 8;
                }
            }
        }
        catch { }
        return 16;
    }

    private static byte[] BuildSectionHeader(string name, uint virtualSize, uint virtualAddress, uint rawSize, uint rawAddress, SectionCharacteristics characteristics)
    {
        byte[] buf = new byte[40];
        Encoding.UTF8.GetBytes(name).CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), virtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), virtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), rawAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36), (uint)characteristics);
        return buf;
    }

    private static void WriteDataDir(Span<byte> target, uint rva, uint size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, rva);
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(4), size);
    }

    private static uint AlignUp(uint val, uint alignment) => (val + alignment - 1) & ~(alignment - 1);

    private static void PadTo(Stream s, long targetPos)
    {
        long diff = targetPos - s.Position;
        if (diff > 0) WriteZeroes(s, (int)diff);
    }

    private static void PadPosition(Stream s, long startPos, long targetOffset)
    {
        long targetPos = startPos + targetOffset;
        PadTo(s, targetPos);
    }

    private static void WriteZeroes(Stream s, int count)
    {
        while (count > 0)
        {
            int chunk = Math.Min(count, s_zeroBuffer.Length);
            s.Write(s_zeroBuffer, 0, chunk);
            count -= chunk;
        }
    }
}
