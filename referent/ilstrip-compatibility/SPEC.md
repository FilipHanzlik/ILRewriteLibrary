# ILStrip: standalone input/output specification and compatibility corpus

## 1. Status and scope

This document describes two operations: **whole-assembly stripping** and
**selective-method stripping**. Its executable evidence is a frozen corpus
captured from a real `dotnet new maui --sample-content` iOS build, together with
a standalone replay and verification harness.

This is a source-derived, experimentally checked specification, not an existing
official CLI standard. There are two distinct goals:

| Goal | Acceptance criterion |
|---|---|
| Legacy compatibility | Match the pinned shipped implementation, including explicitly identified defects, using the recorded golden outputs. |
| Corrected replacement | Preserve the intended metadata and debug identities without reproducing those defects. Legacy whole-assembly goldens alone cannot certify this profile. |

The replay harness invokes the shipped implementation; it is **not** a replacement
stripper. Its independent checks use `System.Reflection.Metadata` and raw bytes.
MSBuild is used only to establish provenance and capture the original task
arguments. A stripping request itself does not require a project, workload,
build graph, linker descriptor, or app bundle.

The corpus covers transformations and output-file side effects. It does not
certify that an arbitrary selected method can safely have its IL removed.

## 2. Which implementation performs which operation?

`AssemblyStripper` is used by the runtime `ILStrip` task when
`TrimIndividualMethods=false`, the default. It supplies whole-assembly stripping.

When `TrimIndividualMethods=true`, `ILStrip` uses its own byte-patching
implementation; it does **not** call `AssemblyStripper` for that operation.
The Apple SDK's `MobileILStrip` derives from the runtime task.

For this MAUI/iOS build, the recorded operation was whole-assembly stripping.
Selective stripping has a WebAssembly consumer in runtime. Android had an
experimental selective consumer in .NET 8/9 which was removed in .NET 10.
Those integrations inform provenance, not the standalone protocol.

The modified Cecil implementation used by `AssemblyStripper` is supplied from
`dotnet/runtime-assets`; it is not the current upstream Mono.Cecil library.
That repository's historical rewrite intention is not a completed specification
or proof that the rewrite happened.

## 3. Pinned oracle and captured application

### 3.1 Actual execution environment

The oracle is the implementation **shipped in the installed .NET 10 iOS workload**,
not a build of the runtime checkout used for source research.

| Component | Captured value |
|---|---|
| Local SDK | `10.0.401` |
| Workload set | `10.0.401.1` |
| MSBuild | `18.9.11+e34a38d2a` |
| SDK runtime/reference packs | `10.0.12` |
| iOS SDK pack | `Microsoft.iOS.Sdk.net10.0_27.0/27.0.10722` |
| MAUI template/package version | `10.0.110` |
| Host | macOS `26.7`, arm64 |
| Xcode | `27.0`, build `27A266a` |
| Target | Release, `net10.0-ios`, `iossimulator-arm64`, Mono, no interpreter |
| Recorded task | `MobileILStrip`, `TrimIndividualMethods=false`, 67 assembly items |

Both oracle assemblies report informational version
`10.0.3+c2435c3e0f46de784341ac3ed62863ce77e117b4`. This is a binary-reported
version, not an assertion that its hash resolves to a public runtime commit.

| Oracle file | SHA-256 |
|---|---|
| `MonoTargetsTasks.dll` | `ce66e6d68253fd11501fa5be14739221a3cd552ff7f9ded9d94f75ebb3731d94` |
| `AssemblyStripper.dll` | `ddd6e8da7b7ef60db65fba3e2555ba50c4badfe01a7e3b63a4039c3721415432` |

Their source installation directory is
`.dotnet/packs/Microsoft.iOS.Sdk.net10.0_27.0/27.0.10722/tools/msbuild/net10.0`.
`corpus/recorded-task.json` records loaded identities and hashes, original task
paths and item metadata, and the successful binlog's hash.

The isolated SDK/workload installation did not alter the system .NET installation.
Earlier attempts with the system .NET 11 SDK failed: Mono/iOS is unsupported for
.NET 11+, and the cross-version attempt requested unavailable compatibility
packs. Those failed binlogs are not the provenance of this corpus.

### 3.2 Frozen files

All corpus-relative paths are relative to `corpus/`.

```text
corpus/
  manifest.json
  recorded-task.json
  inputs/<index>/<assembly>.dll
  inputs/<index>/<assembly>.dll.json
  expected/whole/<index>/<assembly>.dll
  expected/whole-deterministic/<index>/<assembly>.dll
  expected/selective/<case>/{methods.txt,request.json,...}
  expected/selective-errors/<case>/{methods.txt,request.json,...}
```

The 67 pairs total **11,674,272 input DLL bytes** and **7,436,288 whole-stripped
DLL bytes**. The index prevents collisions between equal filenames; it is not
part of assembly identity. Optional sibling PDBs are retained as provenance,
not as task-produced PDB outputs.

The principal application fixture is:

| Property | Value |
|---|---|
| Input | `inputs/025/MauiStripSample.dll` |
| Raw whole output | `expected/whole/025/MauiStripSample.dll` |
| Deterministic compatibility output | `expected/whole-deterministic/025/MauiStripSample.dll` |
| Input/output lengths | `744448` / `512000` bytes |
| MVID | `a5f5959c-869a-4599-8da3-6a4d2bba83b1` |
| MethodDefs | `1166` |
| Methods with bodies / without bodies | `1163` / `3` |
| Original body headers | `765` tiny, `398` fat |
| Bodies with exception regions | `74` |
| Input SHA-256 | `dc52b5caf03de951728471ba64a33b791f4b943013360a98b64c3cb007b49180` |
| Raw captured whole SHA-256 | `54e553e34ce4f157f42b0b97065c53751d6a400ec6b973786fdcec742d62ba5d` |
| Normalized whole SHA-256 | `f0e3685d6a24e6d9a7d9d729862d4ab34daaee0d765f90fb85a2dbbaa3335ec8` |

An MVID alone is not an integrity check: both stripping operations retain it.
Use the input hash to identify the exact fixture bytes.

## 4. Whole-assembly operation

### 4.1 Input and result

Input is one managed PE assembly and an output path. No method list is supplied.
For the successful fixture domain, the operation writes a rewritten PE assembly.
The task's item/output mapping is not part of the binary transformation.

The reference supports identical input/output paths; this was checked on a copied
fixture. Normal separate-output requests must leave the input unchanged.
The replay CLI intentionally requires new output files to protect the corpus.

### 4.2 Transformation invariants

For the legacy profile represented by these fixtures:

1. Every MethodDef originally having a nonzero RVA points to one shared tiny body
   whose IL is the single instruction `ret` (`0x2a`). Its tiny body bytes are
   `06 2a`. The app's shared RVA is `8272`; this is a fixture value, not a general
   required RVA.
2. Bodyless MethodDefs remain bodyless, with RVA zero.
3. Every MethodDef's implementation flags become
   `originalFlags | MethodImplAttributes.NoInlining` (`0x0008`), including
   bodyless methods. Other method attributes, names, signatures and parameter
   associations stay unchanged.
4. Module MVID and managed entry-point identity stay unchanged. Metadata table
   row counts and the four metadata heaps (`#Strings`, `#GUID`, `#US`, `#Blob`)
   stay unchanged.
5. Table bytes are unchanged except `MethodDef`, `FieldRVA`, `ManifestResource`,
   and `CustomAttribute`. RVAs/resource offsets may move as the PE is rewritten.
6. Managed resource names, attributes, implementations and embedded payloads
   stay unchanged. Field-backed data must retain its meaning after relocation;
   exact golden comparison covers its bytes/layout in this corpus, but the
   independent inspector does not decode every field-data size.
7. Custom attributes are sorted by their encoded `HasCustomAttribute` parent,
   not by the numeric metadata token. Attribute order/row identity is not a
   general preservation promise. Constructor/value/owner associations are
   preserved **except for the explicit legacy defect in section 7**.
8. Assembly public-key identity remains. The legacy writer reserves/zero-fills
   strong-name signature storage rather than signing the transformed contents.
   Do not mistake that output for a newly valid strong-name signature.
9. PE sections, offsets, header timestamps, debug layout and overall file size
   can change. This is not an in-place range-zeroing operation.

The shared `ret` stub is not a valid substitute implementation for ordinary JIT
execution of every return type. These outputs are intended for an AOT environment
which does not need the removed IL; retaining metadata does not make them general
purpose runnable assemblies.

### 4.3 Comparison rules

`verify-whole` applies independent metadata/body/resource checks and compares
candidate bytes against captured bytes after the precisely defined timestamp
normalization in section 6.

This is intentionally an exact **legacy** layout test, not a claim that every
semantically correct PE writer must emit the same layout. A corrected writer
needs separate semantic acceptance for the known owner/debug differences.

## 5. Selective-method operation

### 5.1 Input protocol

Conceptually a request contains an assembly, its expected MVID, and a list of
MethodDef tokens. The legacy task serializes that request in a text file:

```text
inputs/025/MauiStripSample.dll
a5f5959c-869a-4599-8da3-6a4d2bba83b1
0600000e
06000001
06000030
```

The first line determines the assembly the task actually reads. Relative paths
are resolved against the process working directory, not the token-file directory.
The second line is compared with the assembly MVID case-insensitively.
The remaining lines are hexadecimal tokens; the examples use the producer-style
eight-digit form without a `0x` prefix. They are not decimal method numbers,
names, or RVAs.

Corpus token files use paths relative to the corpus root. Run them from
`corpus/`. `exercise` creates equivalent absolute-path request files in its new
run directory. A future CLI should avoid a conflicting second assembly path or
explicitly validate that it matches the request's first line.

The supported successful fixture subset uses unique, valid, body-bearing
MethodDef tokens. Bodyless selections and other token kinds are not certified
by this corpus.

### 5.2 File and byte behavior

An empty token list returns the original item and creates **no stripped DLL**.
It is successful, not an error.

A nonempty valid list causes the task to copy the input beneath
`<intermediate>/stripped/<input-filename>` and then patch that copy. This includes
a valid request which changes no bytes because shared-body owners remain.
File length, metadata, method RVAs/flags, resources and bytes outside the patched
method-body ranges remain unchanged.

For each original RVA, determine every MethodDef which uses that RVA. Strip its
body **only if all owners are selected**. A partially selected alias group keeps
the entire body. Selecting one MethodDef does not necessarily remove its body.

For a fully selected body, using its original raw file offset and body-block size:

| Original format | Exact patch |
|---|---|
| Tiny | Set the first header byte to `0x02`; zero the rest of the original body block. |
| Fat | Preserve the first **four bytes**; zero the remainder of the original body block. |

The fat-body patch zeros code size, local-signature token, IL and exception data
while retaining the initial flags/header-size and max-stack fields. It does not
replace the whole body with a `ret`, change the RVA, add `NoInlining`, or compact
the PE.

Do not use successful post-strip method-body decoding as the sole oracle:
retained fat-header flags and erased exception data are not guaranteed to be
accepted by every IL decoder. The independent verifier compares raw bytes
computed from the **original** body-block extents.

### 5.3 Successful golden cases

| Case | Input | Selected methods | Output file | Changed bytes |
|---|---|---:|---|---:|
| `empty` | Original app | 0 | Absent | 0 |
| `mixed` | Original app | 3: tiny, fat and exception-bearing coverage | Present, 744448 bytes | 368 |
| `all` | Original app | All 1163 body-bearing methods | Present, 744448 bytes | 178142 |
| `shared-partial` | Captured whole-stripped app | 1 of 1163 shared-stub owners | Present, 512000 bytes | 0 |
| `shared-all` | Captured whole-stripped app | All 1163 shared-stub owners | Present, 512000 bytes | 2 |

The last two requests use the real whole-stripped application as a synthetic
shared-RVA input. They are not another sample build mode.

All five cases were repeated using fresh output directories and matched exactly
without normalization. The mixed/all selections are transformation fixtures,
**not lists produced or certified by the iOS AOT compiler**.

### 5.4 Failure characterization

| Case | Request defect | Legacy result | Stripped DLL side effect |
|---|---|---|---|
| `wrong-mvid` | MVID differs from input | Task returns false; diagnostic; wrapper exits 1 | Absent |
| `unknown-token` | `06000000`, MethodDef RID zero | Task returns false; diagnostic; wrapper exits 1 | Unchanged input copy exists |
| `duplicate-token` | A uniquely owned method appears twice | Negative RVA usage count; task returns false; wrapper exits 1 | Unchanged input copy exists |
| `malformed-token` | `not-a-hex-token` | Task throws `AggregateException` containing `FormatException`; wrapper exits 1 | Absent |

Failure copies are retained under `expected/selective-errors/`. Diagnostics are
observable, but this corpus does not require identical stack traces or absolute
paths in messages.

These non-atomic side effects are **legacy observations**, not recommended
replacement behavior. A corrected CLI should validate the complete request
before committing an output, report failures with a nonzero exit status, and
not leave a success-shaped output on error. Reject duplicates rather than
silently deduplicating them unless a different contract is deliberately chosen.

## 6. Determinism

`Deterministic=true`, `ContinuousIntegrationBuild=true`, and `PathMap` did not
make the shipped whole-assembly stripper byte-deterministic.

For 67 assemblies, two whole runs separated by more than a second produced:

| Comparison | Matching pairs |
|---|---:|
| Raw bytes | 0 / 67 |
| Bytes after the normalization below | 67 / 67 |

Only these four-byte fields are set to zero:

1. COFF `TimeDateStamp`, at `PEHeaders.CoffHeaderStartOffset + 4`.
2. Debug-directory `TimeDateStamp` for **non-portable CodeView** entries and
   **Reproducible** entries, representing this legacy writer's wall-clock stamps.

Ranges are discovered by parsing each PE. For the application, the observed
offsets are `136` and `600`; never assume those offsets for other assemblies.
`Microsoft.Data.Sqlite.dll` has a Reproducible-only debug entry, so normalizing
CodeView timestamps alone is insufficient for this corpus.

Never normalize portable CodeView's content-derived stamp, MVID, PDB GUID,
metadata, tokens, body bytes, resource data, or arbitrary differences.

`expected/whole-deterministic/` contains materialized normalized binaries.
This is a **timestamp-normalized legacy compatibility profile**, not a supported
deterministic switch on the shipped task. A new tool can use fixed timestamps
and should preserve content-derived portable-PDB identity.

Selective stripping modifies no timestamp fields and was exactly deterministic
for all successful fixtures.

Frozen-input repeatability is established. Complete clean-build reproducibility
of the MAUI project, including all upstream toolchain steps, is **not** established.

## 7. Legacy defects and corrected semantics

### 7.1 Lost custom-attribute owners

The modified old Cecil `HasCustomAttribute` decoder handles tags 0-19 but omits
`GenericParamConstraint` (20) and `MethodSpec` (21). On actual captured assemblies,
constraint-owned attributes are emitted with the nil MethodDef owner
`06000000`, losing their association while retaining constructor and value.

| Assembly | Affected attributes |
|---|---:|
| `CommunityToolkit.Maui.dll` | 3 |
| `Microsoft.Maui.Controls.dll` | 7 |
| `Microsoft.Maui.dll` | 9 |
| `System.Linq.Expressions.dll` | 1 |
| `System.Private.CoreLib.dll` | 79 |
| `Microsoft.iOS.dll` | 8 |
| **Total** | **107** |

The manifest records per-fixture `compatibilityObservations`. The legacy verifier
models this specific loss, preserves constructor/value multiplicity, and rejects
other association changes. It does not silently waive attribute checking.

A corrected replacement should retain **all legal owner associations**, including
tags 20 and 21. The 107 observations cover tag 20; the matching decoder omission
for tag 21 is source evidence, not a fixture demonstrating MethodSpec-owned
attributes.

### 7.2 Portable-PDB/debug identity damage

For the application, the input CodeView entry has:

```text
MajorVersion = 256
MinorVersion = 20557 (0x504d)
IsPortableCodeView = true
Stamp = 3667545124
Guid = 81e20562-dd1b-4a8a-a0fd-6fb1527d6789
Age = 1
Path = /_/MauiStripSample/obj/Release/net10.0-ios/iossimulator-arm64/MauiStripSample.pdb
```

The shipped whole output retains the GUID, age and path, but sets major/minor
versions to zero, loses the portable marker, uses a wall-clock stamp, and drops
the application's PdbChecksum and Reproducible entries.

The newer pinned runtime-assets source contains portable CodeView preservation
work. That does not change what this older shipped oracle emitted.

A corrected replacement should preserve the portable CodeView version/stamp and
PDB identity, and make an explicit policy for other debug records rather than
silently discarding them. Zeroing the legacy timestamps does **not** fix this
identity damage.

Neither defect has been fixed in product source or published as a GitHub issue
as part of this investigation.

## 8. Standalone replay and candidate verification

Run commands below from the directory containing this document unless shown
otherwise. Each output file/run directory must be new. Reuse of the existing
corpus is supported; its golden files are not regenerated during subsequent
exercises.

Build the harness:

```bash
.dotnet/dotnet build replay/StripReplay.csproj -c Release
```

The project references the pinned shipped DLLs directly, without adding NuGet
packages. `OracleDirectory` can be supplied as an MSBuild global property when
relocating the oracle; changing its binaries means changing the oracle profile.

Replay both modes and all error cases:

```bash
.dotnet/dotnet replay/bin/Release/net10.0/StripReplay.dll \
  exercise corpus/manifest.json runs-new
```

Check a replacement's legacy whole outputs:

```bash
.dotnet/dotnet replay/bin/Release/net10.0/StripReplay.dll \
  verify-whole corpus/manifest.json candidate-whole
```

`candidate-whole` must contain the manifest's input suffixes, for example
`candidate-whole/025/MauiStripSample.dll`. This command checks all 67 assemblies.

Invoke the oracle directly and materialize a normalized output:

```bash
.dotnet/dotnet replay/bin/Release/net10.0/StripReplay.dll \
  whole corpus/inputs/025/MauiStripSample.dll candidate-app.dll
.dotnet/dotnet replay/bin/Release/net10.0/StripReplay.dll \
  normalize candidate-app.dll candidate-app-normalized.dll
cmp candidate-app-normalized.dll \
  corpus/expected/whole-deterministic/025/MauiStripSample.dll
```

Invoke selective stripping and independently check a candidate:

```bash
cd corpus
../.dotnet/dotnet ../replay/bin/Release/net10.0/StripReplay.dll \
  methods inputs/025/MauiStripSample.dll \
  expected/selective/mixed/methods.txt ../candidate-methods
../.dotnet/dotnet ../replay/bin/Release/net10.0/StripReplay.dll \
  verify-methods inputs/025/MauiStripSample.dll \
  expected/selective/mixed/methods.txt \
  ../candidate-methods/stripped/MauiStripSample.dll
cd ..
```

The selective verifier computes expected bytes independently; it does not call
the shipped task. Output absence for `empty` and failure-file side effects are
checked by `exercise`, not by `verify-methods`, which takes an existing candidate
file.

Additional commands:

```text
inspect <input.dll> <new-output.json>
capture <successful.binlog> <new-corpus-directory> <sample.csproj>
```

`capture` requires one successful stripping task in a successful build and copies
the files referenced by its recorded items. Capture immediately after the build:
a binlog is not a snapshot of the referenced DLLs. Do not recapture over the
frozen corpus.

## 9. Recreating the MAUI/iOS provenance

The original template sources were not manually modified. From this directory:

```bash
.dotnet/dotnet new maui --sample-content --framework net10.0 \
  --name MauiStripSample --output sample --no-restore --no-update-check

.dotnet/dotnet build sample/MauiStripSample.csproj \
  -c Release -f net10.0-ios -r iossimulator-arm64 \
  -p:TargetFrameworks=net10.0-ios \
  -p:UseMonoRuntime=true \
  -p:UseInterpreter=false \
  -p:EnableAssemblyILStripping=true \
  -p:Deterministic=true \
  -p:ContinuousIntegrationBuild=true \
  -p:RestorePackagesWithLockFile=true \
  -p:PathMap="$PWD/sample=/_/MauiStripSample" \
  '-bl:logs/ios-release-03.binlog;ProjectImports=None'
```

These are the recorded generation/build commands, not instructions to overwrite
the existing successful sample or binlog. Use new locations for a new capture.
The successful build produced **0 warnings and 0 errors**.
`sample/packages.lock.json` preserves the resolved NuGet graph; use locked restore
when rebuilding it. The lockfile does not pin Xcode, workloads, or every native
build input.

The build binlog is `logs/ios-release-03.binlog`. Earlier `01`/`02` attempts failed
and are retained separately. Binlogs can contain local paths and build properties;
they have not been uploaded.

## 10. What was verified, and what remains outside this corpus

The final harness build succeeded with zero warnings/errors. Both the initial
exercise and a later exercise against the existing frozen goldens passed:

- 67 whole-assembly pairs matched the captured legacy output after narrow
  timestamp normalization, with independent table/heap/body/resource checks.
- Repeated whole runs differed only in the allowed timestamp fields.
- Five selective success cases and four failure cases matched their expected
  bytes/file side effects; successful selective repeats were byte-identical.
- In-place whole stripping matched separate-output stripping.
- Frozen input hashes remained unchanged.
- Direct `whole`, `methods`, `normalize`, `inspect`, `verify-whole` and
  `verify-methods` commands were exercised. An unstripped selective candidate was
  rejected for incorrect bytes; the CLI MVID-error path returned exit code 1.

Reports are `runs-final/report.json` and `runs-verified/report.json`.
The latest build/exercise logs are `logs/replay-build-verified.log` and
`logs/exercise-verified.log`.

**Not run or established:** app launch on a simulator, execution of selectively
stripped assemblies, compiler-certified eligibility of synthetic selections,
full clean-build reproducibility, or validation of a new stripper implementation.
This corpus also does not cover every PE variant or malformed input: missing
files, native-only images, bodyless selections, multi-module assemblies,
MethodSpec-owned attributes, signing policies and unusual body layouts need
additional deliberately constructed fixtures.

## 11. Source references

Research revisions are separate from the shipped oracle's binary identity.

| Repository | Pinned revision |
|---|---|
| `dotnet/runtime` | `9fc5e2dd35febb732f16020969b43da7933d326b` |
| `dotnet/macios` | `354c2a645855dfdf774cc5e098cd214764f475b5` |
| `dotnet/android` | `244f6b727642d3da589f5dc5d62884fbe84f8685` |
| `dotnet/maui` | `45bab995c8498ee77144404a4361d3cf86a4a054` |
| `dotnet/runtime-assets` | `13b701d371826c168b26d581351422c031089faa` |
| Android historical `release/9.0.1xx` | `1361e50584b56e690e2b8b5f6db6a04a1d2b7b38` |

Key implementation references:

- [Runtime AssemblyStripper](https://github.com/dotnet/runtime/blob/9fc5e2dd35febb732f16020969b43da7933d326b/src/tasks/AssemblyStripper/AssemblyStripper.cs)
- [Runtime ILStrip task, including selective patching](https://github.com/dotnet/runtime/blob/9fc5e2dd35febb732f16020969b43da7933d326b/src/tasks/MonoTargetsTasks/ILStrip/ILStrip.cs)
- [Apple ILStrip wrapper](https://github.com/dotnet/macios/blob/354c2a645855dfdf774cc5e098cd214764f475b5/msbuild/Xamarin.MacDev.Tasks/Tasks/ILStrip.cs)
- [Modified Cecil sources](https://github.com/dotnet/runtime-assets/tree/13b701d371826c168b26d581351422c031089faa/src/Microsoft.DotNet.CilStrip.Sources)

Within the last reference, `Mono.Cecil.Metadata/Utilities.cs` contains the
`HasCustomAttribute` decoder and `Mono.Cecil.Binary/CopyImageVisitor.cs` contains
debug-directory copying behavior. The binary fixtures, hashes and reports take
precedence over assumptions that a newer source revision is identical to the
shipped oracle.
