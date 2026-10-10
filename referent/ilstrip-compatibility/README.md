# ILStrip implementation handoff

Start with [SPEC.md](SPEC.md). This bundle contains real input assemblies and
expected outputs for reimplementing both whole-assembly and selective-method
stripping. The bundled replay tool is the **legacy oracle and verifier**, not a
new stripping implementation.

## Contents

| Path | Purpose |
|---|---|
| `SPEC.md` | Transformation contracts, determinism, legacy defects and coverage limits |
| `corpus/manifest.json` | 67 frozen whole-assembly input/output pairs and hashes |
| `corpus/inputs/` | Real inputs, metadata snapshots and available sibling PDBs |
| `corpus/expected/whole/` | Exact outputs captured from the shipped iOS task |
| `corpus/expected/whole-deterministic/` | Golden copies with only allowed timestamp fields zeroed |
| `corpus/expected/selective/` | Five successful method-selection fixtures |
| `corpus/expected/selective-errors/` | Four failure fixtures, including output-file side effects |
| `replay/` | C# source plus prebuilt cross-platform DLL and its dependencies |
| `sample/` | Template-generated MAUI sample sources and dependency lockfile |
| `reports/verified.json` | Verification results from the original capture |
| `provenance/` | Original SDK pin and bundle metadata |
| `licenses/` | Component licenses, notices, package metadata and attribution inventory |
| `SHA256SUMS` | Integrity hashes for every other bundled file |

## Run without MAUI, Xcode, or a workload installation

Install a **.NET 10 runtime** (a .NET 10 SDK also supplies it), unzip the archive,
and run from this directory:

```bash
dotnet replay/bin/Release/net10.0/StripReplay.dll \
  exercise corpus/manifest.json runs-local
```

Use a new run-directory name each time. Expected error diagnostics are printed
for the four negative cases; a successful exercise ends with the verification
summary and exit code zero.

Check your implementation's legacy-compatible whole outputs:

```bash
dotnet replay/bin/Release/net10.0/StripReplay.dll \
  verify-whole corpus/manifest.json candidate-whole
```

Candidate paths mirror the input suffixes, for example
`candidate-whole/025/MauiStripSample.dll`.

Check a selective candidate independently of the oracle:

```bash
cd corpus
dotnet ../replay/bin/Release/net10.0/StripReplay.dll \
  verify-methods inputs/025/MauiStripSample.dll \
  expected/selective/mixed/methods.txt ../candidate-methods.dll
```

The token files' first lines are relative to the **corpus root**, not their own
directories. See SPEC.md for direct stripping and other CLI commands.

## Rebuild the harness

Use a .NET 10 SDK. The original SDK was 10.0.401. No mobile workload or NuGet
package dependency is needed; override the project's original workload-pack
location with the included oracle binaries:

```bash
dotnet build replay/StripReplay.csproj -c Release \
  -p:OracleDirectory="$PWD/replay/bin/Release/net10.0" \
  -o rebuilt-tools -p:UseAppHost=false
dotnet rebuilt-tools/StripReplay.dll \
  exercise corpus/manifest.json runs-rebuilt
```

If multiple SDKs are installed, select a .NET 10 SDK for rebuilding. The original
SDK pin is preserved under `provenance/global.capture.json`, not at the bundle
root, so running the prebuilt DLL does not demand that exact SDK.

## Important compatibility boundaries

Whole-stripping goldens include **known legacy defects**: loss of some
generic-constraint custom-attribute owners and portable-PDB/debug identity
damage. The legacy verifier intentionally characterizes them. A corrected
implementation should preserve the intended identities and cannot be judged
solely by exact legacy whole-output equality.

Whole outputs match after narrow timestamp normalization; selective outputs
match exactly. Synthetic selective lists are not AOT-compiler-certified lists
of safely removable methods. No stripped application was launched.

## Packaging and provenance

The original corpus DLLs/PDBs and golden bytes are unchanged. Capture-machine
path prefixes in textual provenance and diagnostics have been replaced with
`<capture-root>` and `<capture-user-home>`. The tool was rebuilt with mapped
source paths, without a platform-specific executable host.

The multi-gigabyte SDK/workloads, build caches, generated app bundles, raw
logs/binlogs, failed attempts, and redundant replay outputs are omitted.
SPEC.md's `logs/` and `runs-*/` paths refer to the original capture, not files
included here. `reports/verified.json` preserves the final verification report.
The original binlog hash is retained as provenance, but the binlog is not bundled.

Binary font assets are omitted from the sample source copy; regenerate the
sample with the pinned MAUI template for a complete application rebuild.
Rebuilding the app is unnecessary for exercising the frozen corpus.

License notices apply to their respective third-party components. Fixture
assemblies derived by linking/stripping are identified in the package inventory;
they are not unmodified copies of the corresponding NuGet assemblies.

On macOS, verify the extracted bundle with `shasum -a 256 -c SHA256SUMS`;
on Linux, use `sha256sum -c SHA256SUMS`.
