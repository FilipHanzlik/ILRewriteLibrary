# SRM: Reading, Writing, and Performance

Updated: 2026-09-15. This expands the reading cheat sheet previously supplied in chat.

**Purpose:** a learning reference and a starting point for thesis research, not evidence that every operation has been implemented or mastered. Numbered references distinguish API documentation, format specifications, source observations, and hypotheses.

## 1. Mental model

System.Reflection.Metadata (SRM) provides low-level metadata reading and output-building infrastructure. Unlike Cecil, it does not provide a general-purpose mutable assembly object graph. [1, 2, 12]

```text
Reading:
PE image -> PEReader -> MetadataReader -> handles and metadata views

Writing:
Your output plan -> metadata rows/heaps + encoded bodies -> PE image
```

| Namespace | Examples |
|---|---|
| `System.Reflection.Metadata` | `MetadataReader`, definition/handle types, `BlobReader`, `BlobBuilder` |
| `System.Reflection.Metadata.Ecma335` | `MetadataTokens`, `MetadataBuilder`, signature and IL encoders |
| `System.Reflection.PortableExecutable` | `PEReader`, `PEHeaderBuilder`, `ManagedPEBuilder` |

Importing a namespace does not import its subnamespaces.

## 2. Opening an assembly and managing lifetime

The following snippets assume an ordinary trusted managed sample, not an arbitrary native, mixed-mode, or malicious image.

```csharp
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using var stream = new FileStream(
    inputPath,
    FileMode.Open,
    FileAccess.Read,
    FileShare.Read);

using var peReader = new PEReader(stream);
MetadataReader reader = peReader.GetMetadataReader();
```

- `FileAccess.Read` controls this handle's access.
- `FileShare.Read` permits other readers and restricts ordinary concurrent write/delete opens on Windows.
- `FileShare.ReadWrite` does not guarantee a consistent snapshot if another process modifies the file.
- `using` disposes at scope exit, including through `return` or an exception, not immediately after last use.
- Multiple using declarations are disposed in reverse declaration order.
- By default, `PEReader` owns/disposes the stream. `LeaveOpen` changes ownership, not the need to coordinate access.
- Keep the underlying memory and owning reader alive while using metadata/body views. [2, 3, 4, 5]

## 3. Input loading is not object-graph construction

| `PEStreamOptions` | Behavior |
|---|---|
| `Default` | Read image data lazily |
| `PrefetchMetadata` alone | Read metadata upfront; method bodies and other image content are unavailable |
| `PrefetchEntireImage` | Read the entire image upfront |

```csharp
using var peReader = new PEReader(
    stream, PEStreamOptions.PrefetchEntireImage);
```

Prefetch options normally allow the reader to close the input stream during construction unless `LeaveOpen` is specified. Protect the input against concurrent changes during the initial read too. [3, 4]

**Loading all DLL bytes does not create objects for every type, method, or instruction.** Conversely, lazy loading is block-oriented, not necessarily one tiny file read for each requested value.

For a full rewrite, prefetching is a reasonable prototype choice, not a proven fastest default. Multiple assemblies, edit state, builders, and output buffers all contribute to peak memory.

## 4. Handles, metadata views, and values

```csharp
foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
{
    TypeDefinition type = reader.GetTypeDefinition(handle);
    string name = reader.GetString(type.Name);
}
```

```text
Enumerate handle   -> generate a TypeDef row identifier
GetTypeDefinition  -> obtain a lightweight reader/row view
type.Name          -> read the name's heap reference
GetString          -> decode the referenced text
```

For ordinary metadata, enumeration does not need a prebuilt array of type objects or handles. Type properties access underlying table data; `GetTypeDefinition` does not eagerly construct a whole type and its members. Specialized projection paths can perform additional work. [2, 6]

A handle is a small value type interpreted in a reader's context. It is not a global identity, object pointer, or file offset.

### IsNil and EntityHandle

`IsNil` is a Boolean property meaning "no value." For table handles, row zero is nil and actual rows start at one. It is not a general validity check.

```csharp
var parentHandle = type.GetDeclaringType();
if (!parentHandle.IsNil)
{
    var parent = reader.GetTypeDefinition(parentHandle);
}
```

`EntityHandle` accommodates different metadata-table kinds. Inspect `Kind`, then cast to the corresponding typed handle. A cast does not resolve a dependency or execute code. Each relationship permits specific kinds. [7]

## 5. Reading heaps

| Heap | Handle | Reader API |
|---|---|---|
| `#Strings` | `StringHandle` | `GetString(handle)` |
| `#US` | `UserStringHandle` | `GetUserString(handle)` |
| `#Blob` | `BlobHandle` | `GetBlobBytes(handle)` or `GetBlobReader(handle)` |
| `#GUID` | `GuidHandle` | `GetGuid(handle)` |

`GetBlobBytes` copies into an array. `GetBlobReader` provides access without requiring that intermediate array copy. Interpretation still depends on the blob's purpose: a signature and custom-attribute arguments use different encodings. [2, 7]

```csharp
reader.StringComparer.Equals(method.Name, "Add")
```

Compares the identified metadata text with a C# string without first constructing a decoded string for the metadata name. It still inspects text; it is not a numeric handle comparison. [8]

## 6. Assembly identity

```csharp
var assembly = reader.GetAssemblyDefinition();
string name = reader.GetString(assembly.Name);
Version version = assembly.Version;
var identity = assembly.GetAssemblyNameInfo();
```

| Member | Meaning |
|---|---|
| `Name` | Handle to the simple name in `#Strings` |
| `Version` | Four numbers represented as a `Version` |
| `GetAssemblyNameInfo()` | Lightweight immutable identity object |
| `GetAssemblyName()` | Conventional mutable `System.Reflection.AssemblyName` object |

The assembly row stores major/minor/build/revision as four unsigned 16-bit numbers, not a version string. `AssemblyNameInfo` gathers identity components; use `GetString` if only the simple name is needed. Modifying a returned `AssemblyName` does not modify source metadata. Check `reader.IsAssembly` for general inputs. [7, 9]

## 7. Types and members

| Task | API sequence |
|---|---|
| Enumerate types | `reader.TypeDefinitions` -> `GetTypeDefinition(handle)` |
| Enumerate a type's methods | `type.GetMethods()` -> `GetMethodDefinition(handle)` |
| Enumerate its fields | `type.GetFields()` -> `GetFieldDefinition(handle)` |
| Find a nested type's enclosing type | `type.GetDeclaringType()` |
| Find a method's owning type | `method.GetDeclaringType()` |
| Print flags | `method.Attributes` or `field.Attributes` |
| Find the method-body location | `method.RelativeVirtualAddress` |

SRM's type collection includes nested types. Their namespace can be empty; follow the enclosing-type relationship instead of assuming they are top-level. `<Module>` is a special metadata type. [6, 7]

Flags enums format as readable names:

```text
Public, HideBySig
Private, InitOnly
Public, Static, Literal, HasDefault
```

These flags are not custom attributes such as `[Exploration(...)]`.

### Ownership lookup

`MethodDef` has no direct declaring-type column. In ordinary optimized metadata, `TypeDef.MethodList` establishes consecutive method ownership ranges. Finding the owner uses metadata relationships, not the method's name or IL. Uncommon pointer tables add indirection. [7]

If already enumerating a type's methods, retain its handle. Do not repeatedly rediscover the same owner. Additional caching should be justified by measured repetition, not assumed necessary.

## 8. Method bodies and IL

For the sample's ordinary managed methods:

```csharp
if (method.RelativeVirtualAddress != 0)
{
    var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
    Console.WriteLine(body.MaxStack);
    Console.WriteLine(body.LocalSignature.IsNil);
    Console.WriteLine(body.ExceptionRegions.Length);
    Console.WriteLine(Convert.ToHexString(body.GetILContent().AsSpan()));
}
```

General tools must also consider implementation flags and supported image forms; nonzero RVA alone is not a universal managed-body guarantee.

| Member | Meaning |
|---|---|
| `MaxStack` | Declared stack limit, not measured actual usage |
| `LocalSignature` | Handle to a StandAloneSig row describing local slots |
| `ExceptionRegions` | Exception-handling regions |
| `GetILContent()` | IL bytes as an immutable array |

`AsSpan()` adds no byte-array copy; it supplies the read-only view accepted by `Convert.ToHexString`. This does not claim that obtaining IL content itself is universally zero-copy. [10]

Parameters, local slots, and the evaluation stack are distinct. No local signature means no declared IL local slots; source locals can be optimized away. Tiny method headers imply `MaxStack = 8`, even when fewer slots are used. [7]

### Observed Add example

```text
IL bytes: 02 28 0D 00 00 06 03 04 58 2A

ldarg.0
call 0x0600000D
ldarg.1
ldarg.2
add
ret
```

The call operand is little-endian. `0x06` identifies MethodDef; `0x0D` means row 13. The user followed this to `CountCall` in the sample.

```csharp
using System.Reflection.Metadata.Ecma335;

var handle = MetadataTokens.MethodDefinitionHandle(13);
var target = reader.GetMethodDefinition(handle);
```

This hard-coded row is sample-specific. General decoders inspect operand/token kinds, including MethodDef, MemberRef, and MethodSpec for calls. SRM supplies primitives, not Cecil's editable instruction list. [7]

## 9. References and custom attributes

A `MethodDefinition` declares a method in this module. A `MemberReference` describes a method or field through `Parent`, `Name`, and `Signature`; it does not contain the method's implementation.

```text
Typical external call:
IL -> MemberRef -> TypeRef -> AssemblyRef
```

References are not necessarily external. Locating another assembly and its actual definition is a separate resolution task. [7]

```text
method.GetCustomAttributes()
  -> handles -> reader.GetCustomAttribute(handle)
  -> Constructor: EntityHandle
       MethodDefinition -> GetDeclaringType() -> type
       MemberReference  -> Parent -> owning type
```

The attribute application has no independent name. The constructor is named `.ctor`; the declaring type supplies a name such as `ExplorationAttribute`. Arguments are in the application's `Value` blob. [7]

SRM does not instantiate the attribute. Reflection requesting actual attribute instances can execute constructors and named-argument setters; `CustomAttributeData` permits inspection without instantiation. [11]

## 10. Signature decoding: conceptual only so far

```text
Signature blob -> SRM decoder -> provider callbacks -> MethodSignature<TType>
```

```csharp
method.DecodeSignature(provider, genericContext);
```

- `provider`: an `ISignatureTypeProvider<TType, TGenericContext>` implementation deciding how decoded types are represented.
- `genericContext`: information the provider needs for generic-parameter positions.
- A string-producing provider represents an encoded integer type as `"Int32"`, not an integer value.
- For non-generic `Add`, a suitable provider can use null context.
- There is no built-in general signature-to-string provider. Implement or reuse one; it is not needed separately per method. [13]

The user chose not to implement the full provider interface during this introduction.

## 11. Writing: assembling output rather than editing reader views

| Component | Responsibility |
|---|---|
| `MetadataBuilder` | Add output metadata rows and heap values |
| `BlobBuilder` | Accumulate bytes |
| `BlobEncoder` and signature encoders | Encode signatures |
| `InstructionEncoder` | Encode IL |
| `MethodBodyStreamEncoder` | Encode bodies/headers into a body stream |
| `MetadataRootBuilder` | Prepare metadata-root serialization |
| `PEHeaderBuilder` / `ManagedPEBuilder` | Describe and serialize a managed PE image |

```text
Rows + heaps ---------------------+
Signatures -> blob heap ----------+-> metadata root + body stream -> PE
IL -> method-body stream ---------+
```

Returned handles connect output entities. Input handles do not automatically identify the correct output entities.

`AddMethodDefinition` takes a signature blob handle and a method-body STREAM OFFSET, not an input RVA. Its body-offset sentinel for no body is `-1`; zero can be a valid first body offset. [12, 14]

**The caller must establish consistency:** ownership ranges, signatures, references, bodies, and required content. Builders have argument, encoding, and ordering checks, not complete program-semantic validation.

For rewriting, remapping may affect table references, signature-embedded handles, IL token operands, local signatures, catch types, user strings, entry points, and debug information. Unchanged code can still need relocation when output row numbers change. See ECMA-335 for the encoding obligations. [7]

The official Hello World example is useful for orientation but uses .NET Framework-style `mscorlib` references. Do not treat it as an unchanged .NET 10 starter. [12]

## 12. What efficient emission does and does not mean

**Source observations, not benchmark results:**

| Mechanism | Observation | Qualification |
|---|---|---|
| Compact rows | SRM stores tables in lists of row structs | Cecil's final writer also uses arrays of row structs |
| Byte buffering | BlobBuilder uses chunks optimized for append/sequential output | Allocations and copies still exist |
| Direct IL encoding | InstructionEncoder can avoid a mandatory instruction-object list | Complex edits may still need an intermediate representation |
| Explicit output construction | Caller supplies already-planned relationships | Planning cost has moved to the caller, not disappeared |

Sources: SRM tables [14], BlobBuilder [15], Cecil writer [16].

**Correction to an earlier oversimplification:** compact row storage alone is not a differentiating SRM advantage over Cecil's final emitter.

Cecil normally rebuilds the output image. Its writer performs broad metadata work, but unresolved method bodies can use raw-body patching instead of instruction-graph materialization. [16, 17]

Replacing only the backend while retaining a Cecil-like graph might be faster, equivalent, or slower. Translation, stack calculation, deduplication, symbol processing, and preservation obligations must be equivalent before interpreting measurements.

No representative SRM-versus-Cecil stripping benchmark has been run in this project. No minimum speedup has been established.

## 13. Thread safety: distinguish readers and builders

**There is no valid blanket claim that every SRM operation is "absolutely thread-safe."**

| Operation | Boundary |
|---|---|
| Parallel reads through PEReader | Explicitly supported; disposal must not overlap reads |
| Shared metadata inspection | Keep source memory stable/alive; respect any custom decoder/provider's contract |
| Advancing a mutable BlobReader/enumerator | Keep cursor state worker-local |
| Mutating one MetadataBuilder from several threads | Requires external coordination |
| Mutating one BlobBuilder from several threads | Requires external coordination |
| Using independently owned output builders | Independent work can run concurrently |

PEReader's source explicitly documents concurrent reading and excludes concurrent disposal. This is a scoped guarantee, not a guarantee about all SRM types. [4]

**Builder thread-safety evidence is an implementation-based conclusion:** `AddMethodDefinition` modifies a normal `List<MethodRow>` and then derives the returned handle from `Count`, without synchronization. Concurrent calls risk both collection corruption and incorrect returned row identities. This is not a quoted thread-safety statement from the MetadataBuilder API page. [14, 18]

Cecil can also write independent assemblies concurrently when object graphs, resolvers/caches, streams, and symbol services are independently owned or appropriately synchronized. Its writer uses per-operation state, not a global lock serializing all assemblies. Do not generalize this to concurrent writes/mutation of one module. [16, 17]

### Possible intra-assembly architecture, not an implemented guarantee

```text
Stable input
  -> parallel worker-owned analysis/transformation
  -> coordinated references, conflicts, and token planning
  -> parallel body encoding into separate buffers where feasible
  -> coordinated metadata population and final output
```

Locking a shared builder serializes access. It does not by itself solve semantic conflicts, deterministic ordering, or owner-list contiguity.

## 14. Performance status and next evidence milestone

**Conclusion:** SRM-based rewriting is technically feasible; meaningful speedup for the intended workload remains unproven.

The reported 20-30 MB upper DLL size does not determine graph cost. Method counts, IL density, resources, edit density, repeated resolution, and batching matter.

Remaining hypotheses:
- Avoid unnecessary decoding/materialization.
- Reduce repeated analysis/resolution.
- Parallelize substantial independent work within one assembly.
- Improve proven emission bottlenecks without changing output obligations.

Before committing to a performance-driven redesign:
1. Obtain one representative stripping job: inputs, dependencies, exact transformation/configuration, and expected behavior.
2. Profile the unchanged Cecil tool: elapsed/CPU time, allocations, GC, and peak/retained memory separately.
3. Distinguish known-removal stripping from full reachability/reflection-aware trimming.
4. Add targeted instrumentation only where the baseline cannot answer the question.
5. Prototype one measured bottleneck and compare end-to-end equivalent work.

Compare fairly with Cecil's lazy/raw-body paths and existing assembly-level parallelism. Avoid per-getter instrumentation that dominates the measured work.

These are experimental recommendations, not published performance findings. Platform alignment and maintenance may have separate value but do not prove speed.

## 15. Learning boundary

| Status | Topics |
|---|---|
| Demonstrated through user-supplied output | Identity, type/member enumeration, flags/RVAs, Add's body bytes, known call token to CountCall |
| Discussed; no implementation demonstrated | Attribute traversal, signature providers, writer pipeline, concurrency designs |
| Not yet done | Assembly emission, general IL decoding, dependency resolution, token-remapping implementation, profiling corpus or benchmarks |

Enough foundation exists to investigate a real Cecil stripping workload. More broad API study is not a prerequisite. Learn remaining SRM mechanisms in response to the measured use case.

## References and thesis-use notes

References gathered during the learning session, last reviewed 2026-09-15. API pages select .NET 10 but are living documentation. Source permalinks identify exact snapshots, not necessarily the binaries installed locally.

For a thesis, cite the specification for format rules, documentation for API contracts, and pinned source for implementation observations. Record the actual runtime/package/tool versions and workload configuration for experiments. This cheat sheet is a secondary explanation, not benchmark evidence.

### Official documentation and specification

1. Microsoft, [System.Reflection.Metadata package README: About / How to Use](https://www.nuget.org/packages/System.Reflection.Metadata#readme-body-tab). Purpose and high-level positioning.
2. Microsoft, [MetadataReader](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadatareader?view=net-10.0). Table/heap reading APIs, examples, and lifetime/input cautions.
3. Microsoft, [PEStreamOptions](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pestreamoptions?view=net-10.0). Lazy/prefetched reading and stream ownership.
4. .NET runtime source, [PEReader.cs](https://github.com/dotnet/runtime/blob/6af607b8858d946ddbf4101a18b98de61c2eb5e1/src/libraries/System.Reflection.Metadata/src/System/Reflection/PortableExecutable/PEReader.cs). Class remarks explicitly document parallel reads and the disposal exception; constructors document source lifetime.
5. Microsoft, [using statement](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/using) and [FileShare](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=net-10.0). Language/resource and file-sharing semantics.
6. .NET runtime source, [TypeDefinition.cs](https://github.com/dotnet/runtime/blob/6af607b8858d946ddbf4101a18b98de61c2eb5e1/src/libraries/System.Reflection.Metadata/src/System/Reflection/Metadata/TypeSystem/TypeDefinition.cs) and [HandleCollections.TypeSystem.cs](https://github.com/dotnet/runtime/blob/6af607b8858d946ddbf4101a18b98de61c2eb5e1/src/libraries/System.Reflection.Metadata/src/System/Reflection/Metadata/TypeSystem/HandleCollections.TypeSystem.cs). Lightweight row views, property access, and handle enumeration.
7. Ecma International, [ECMA-335, Common Language Infrastructure, sixth edition (June 2012)](https://ecma-international.org/publications-and-standards/standards/ecma-335/). See Partition II: Assembly/TypeDef/MethodDef/MemberRef/CustomAttribute tables, signatures, metadata physical layout and method bodies; Partition III: IL instructions. Also consult [.NET's ECMA-335 augmentations](https://github.com/dotnet/runtime/blob/6af607b8858d946ddbf4101a18b98de61c2eb5e1/docs/design/specs/Ecma-335-Augments.md) for later runtime-specific additions.
8. Microsoft, [MetadataStringComparer](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadatastringcomparer?view=net-10.0). Comparing metadata names without decoding them into intermediate strings.
9. Microsoft, [AssemblyDefinition](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.assemblydefinition?view=net-10.0) and [AssemblyNameInfo](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.assemblynameinfo?view=net-10.0). Identity access and the immutable identity representation.
10. Microsoft, [MethodBodyBlock](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.methodbodyblock?view=net-10.0) and [ImmutableArray.AsSpan](https://learn.microsoft.com/en-us/dotnet/api/system.collections.immutable.immutablearray-1.asspan?view=net-10.0). Body access and read-only array views.
11. Microsoft, [CustomAttributeData](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.customattributedata?view=net-10.0) and [Accessing attributes using reflection](https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/reflection-and-attributes/accessing-attributes-by-using-reflection). Attribute descriptions versus instantiation.
12. Microsoft, [MetadataBuilder and its Hello World example](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.ecma335.metadatabuilder?view=net-10.0#examples) and [ManagedPEBuilder](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.managedpebuilder?view=net-10.0). Complete emission path; sample uses legacy framework references.
13. Microsoft, [MethodDefinition.DecodeSignature](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.methoddefinition.decodesignature?view=net-10.0) and [ISignatureTypeProvider](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.isignaturetypeprovider-2?view=net-10.0). Provider and context contracts.

### Implementation evidence

.NET runtime snapshot: `6af607b8858d946ddbf4101a18b98de61c2eb5e1`.
Cecil snapshot: `882ca5eedda1e62eb41bd5869aeb15d8f1538e51`.

14. .NET runtime, [MetadataBuilder.Tables.cs](https://github.com/dotnet/runtime/blob/6af607b8858d946ddbf4101a18b98de61c2eb5e1/src/libraries/System.Reflection.Metadata/src/System/Reflection/Metadata/Ecma335/MetadataBuilder.Tables.cs). Row structs/lists; inspect `_methodDefTable` and `AddMethodDefinition` for the unsynchronized add/count sequence and body-offset contract.
15. .NET runtime, [BlobBuilder.cs](https://github.com/dotnet/runtime/blob/6af607b8858d946ddbf4101a18b98de61c2eb5e1/src/libraries/System.Reflection.Metadata/src/System/Reflection/Metadata/BlobBuilder.cs). Chunked byte-buffer implementation and mutable append state.
16. Mono.Cecil, [AssemblyWriter.cs](https://github.com/jbevain/cecil/blob/882ca5eedda1e62eb41bd5869aeb15d8f1538e51/Mono.Cecil/AssemblyWriter.cs). Inspect `ModuleWriter.Write`, `BuildMetadata`, and `MetadataTable<TRow>`: output workflow, per-module state, and arrays of row structs.
17. Mono.Cecil, [CodeWriter.cs](https://github.com/jbevain/cecil/blob/882ca5eedda1e62eb41bd5869aeb15d8f1538e51/Mono.Cecil.Cil/CodeWriter.cs). Inspect `WriteMethodBody`, `WriteUnresolvedMethodBody`, and `WriteResolvedMethodBody`: raw-body patching versus instruction-based emission.
18. Microsoft, [List<T>: Thread Safety](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1?view=net-10.0#thread-safety). Shared read/write access requires synchronization; used together with source [14], not as a standalone SRM contract.
