---
name: ilstrip-validation
description: Run automated validation tests against any candidate implementation of ILStrip (whole-assembly stripping). Use when developing, iterating on, or verifying a new IL/metadata rewriting library against the referent's 67-assembly production corpus and StripReplay oracle.
compatibility: Requires .NET 10 SDK (dotnet CLI) and access to the repository workspace
---

# ILStrip Validation & Testing Skill

This skill guides an agent developing a new System.Reflection.Metadata (SRM) based **ILStrip** (whole-assembly stripping) engine on how to run tests, validate candidate assemblies against the 67-assembly production corpus, and interpret the referent validator's feedback.

---

## 1. Quick Start: How to Run the Tests Against Your Stripper

The test suite in `Tests/ValidationTests/ValidationTests.sln` is built with **xUnit** and connects directly to the referent's official validator (`StripReplay.dll`).

### Setting the Stripper Path

You can point the validation suite to your new ILStrip implementation using either of the following methods:

#### Method A: Environment Variable (Recommended for CLI / CI)
```bash
export NEW_ILSTRIP_PATH="/path/to/YourNewILStrip.dll"
# Optional: if your tool expects specific CLI flags (default is "{input} {output}"):
# export ILSTRIP_ARGS="-i {input} -o {output}"
```

#### Method B: Local Config File (Recommended for IDE / Persistent Sessions)
Create or edit `Tests/ValidationTests/ValidationTests/ilstrip.config.txt` and place the absolute or relative path to your executable or `.dll` on the first line:
```text
/home/filip/Documents/MFF/bakalářka/src/MyILStrip/bin/Release/net10.0/MyILStrip.dll
```

*(Note: If neither is set, the tests automatically fall back to the referent's legacy oracle as a green baseline).*

---

## 2. Running Test Suites with `dotnet test`

### Run All 67 Assembly Tests Individually
```bash
dotnet test Tests/ValidationTests/ValidationTests.sln --filter "FullyQualifiedName~StripAndValidate_IndividualAssembly"
```
* **What it does:** Runs each of the 67 production assemblies from `Tests/data/dlls/ILStrip/input/` as a separate xUnit `[Theory]` test case.
* **Benefit:** If assembly #14 fails, the other 66 continue to run. You immediately see exactly which assemblies passed and failed.

### Run on a Single Assembly (Fast Iteration)
When debugging a specific assembly, filter by name:
```bash
# Test on the main MAUI application assembly
dotnet test Tests/ValidationTests/ValidationTests.sln --filter "DisplayName~MauiStripSample.dll"

# Test on a small library first
dotnet test Tests/ValidationTests/ValidationTests.sln --filter "DisplayName~Microsoft.Data.Sqlite.dll"

# Test on a complex UI assembly with many methods and generic constraints
dotnet test Tests/ValidationTests/ValidationTests.sln --filter "DisplayName~Microsoft.Maui.Controls.dll"
```

### Run the Official Referent Certification Test
```bash
dotnet test Tests/ValidationTests/ValidationTests.sln --filter "FullyQualifiedName~ValidateEntireCorpus_UsingReferentVerifyWholeCli"
```
* **What it does:** Batch-strips all 67 assemblies and directly executes the referent's official CLI command:
  ```bash
  dotnet referent/ilstrip-compatibility/replay/bin/Release/net10.0/StripReplay.dll \
    verify-whole referent/ilstrip-compatibility/corpus/manifest.json <candidate-dir>
  ```
* **Significance:** Passing this test is the official criterion for full referent compliance.

---

## 3. The Validation Invariants (What Your ILStrip Must Satisfy)

The referent validator checks two layers:

### Layer 1: Normalized SHA-256 Match
The validator zeroes out the 8 wall-clock timestamp bytes:
1. 4 bytes at COFF header start + 4 (`TimeDateStamp`)
2. 4 bytes in the PE Debug directory entry (`TimeDateStamp`)
The resulting normalized bytes are hashed with SHA-256 and compared to `expectedSha256` recorded in [Tests/data/dlls/ILStrip/mapping.json](file:///home/filip/Documents/MFF/bakal%C3%A1%C5%99ka/Tests/data/dlls/ILStrip/mapping.json).

### Layer 2: Deep Semantic & Structural Rules (via SRM `PEReader`)
Your implementation must satisfy these exact invariants:

| Metadata Component | Expected Behavior in Stripped DLL |
| :--- | :--- |
| **MVID** | Module GUID must be 100% identical to the input assembly. |
| **EntryPoint** | Entry point token / RVA must be preserved. |
| **Heaps** | `#Strings`, `#GUID`, `#US`, and `#Blob` must match original input byte-for-byte (copy-through without re-interning strings). |
| **Tables (non-method)** | Row counts and row contents of all non-method tables (`TypeRef`, `TypeDef`, `Field`, `MemberRef`, etc.) must match input. |
| **Method Defs (Bodyless)** | Abstract, runtime, or P/Invoke methods (`RVA == 0`) must remain `RVA == 0`. |
| **Method Defs (With Body)** | Every method with IL must point to a **single shared RVA** across the entire assembly containing a 1-byte opcode `0x2a` (`ret`). |
| **Method ImplAttributes** | `MethodImplAttributes.NoInlining` (`0x0008`) must be bitwise-ORed into `ImplAttributes`. |
| **Resources** | Managed manifest resources must match input byte-for-byte. |
| **Custom Attributes** | The `CustomAttribute` table must be strictly sorted by encoded `HasCustomAttribute` parent token. |
| **PDBs / Debug** | **No `.pdb` file on disk is modified or created by ILStrip.** Sibling PDBs are purely compile-time provenance. The PE container's CodeView debug directory entry (GUID, age, path) pointing to the PDB must be preserved. |

---

## 4. Troubleshooting Common Failures

### `Normalized SHA-256 mismatch`
* **Cause:** The PE file structure or table ordering differs from the golden output.
* **Action:** Check if the structural invariants passed. If structural verification passed but the hash differed, inspect PE section alignment, headers, or metadata stream padding.

### `Whole-stripped methods do not share one body`
* **Cause:** Your code allocated a separate `ret` stub per method instead of reusing a single shared offset in the method body stream.
* **Fix:** Emit `0x06, 0x2a` (tiny header + `ret`) once into `MethodBodyStreamEncoder`, note the offset, and assign that exact RVA offset to every stripped method.

### `Method ImplAttributes missing NoInlining`
* **Cause:** Forgot to set the `NoInlining` flag.
* **Fix:** When copying `MethodDefinition`, apply `method.ImplAttributes | MethodImplAttributes.NoInlining`.

### `Metadata heap changed: #Strings (or #Blob / #US)`
* **Cause:** Re-serializing or re-interning strings causes new heap offsets.
* **Fix:** Use zero-copy / copy-through pass-through of the raw heap byte arrays directly into the output PE builder.

### `Custom attributes are not sorted by their encoded parent`
* **Cause:** In ECMA-335, the `CustomAttribute` table must be sorted by the coded index `HasCustomAttribute`.
* **Fix:** Sort custom attribute rows by `((token & 0x00ffffff) << 5) | tag` before writing the table.

---

## 5. Test Corpus Inventory

* **Total Fixtures:** 67 real production assemblies (ranging from 5 KB to 2.1 MB).
* **Corpus Mapping File:** [Tests/data/dlls/ILStrip/mapping.json](file:///home/filip/Documents/MFF/bakal%C3%A1%C5%99ka/Tests/data/dlls/ILStrip/mapping.json).
* **Inputs Directory:** `Tests/data/dlls/ILStrip/input/`
* **Golden Expected Directory:** `Tests/data/dlls/ILStrip/expected/`
* **Referent Oracle DLL:** `referent/ilstrip-compatibility/replay/bin/Release/net10.0/StripReplay.dll`
