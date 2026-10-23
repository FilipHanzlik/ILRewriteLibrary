---
name: ecma335-srm-internals
description: Reference and subagent lookup protocol for the ECMA-335 specification PDF, PE section layout, and System.Reflection.Metadata.Ecma335 low-level APIs. Load this skill whenever you need binary layout rules for metadata tables, heaps, signatures, CIL method bodies, or PE headers.
---

# ECMA-335 Specification Lookup & SRM Internals Skill

Use this skill to get exact binary layouts for `.NET` PE files, ECMA-335 metadata tables, and `System.Reflection.Metadata` (SRM) low-level offsets without polluting your main context window.

---

## 1. SUBAGENT PROTOCOL FOR QUERIES TO THE ECMA-335 PDF

**CRITICAL RULE FOR MAIN AGENT:** 
NEVER read or grep the entire ECMA-335 PDF directly in your main context window! The specification is over 500 pages long and will destroy your context budget.

Whenever you need binary layout details not covered in the Quick Reference below (e.g., specific table column definitions, coded index tag bits, blob signature encodings, or fat method headers):

1. **Spawn a Subagent** (using your subagent/task tool) with a narrowly scoped question.
2. **Pass the Subagent these exact instructions:**
   - Locate the ECMA-335 PDF in the repository (e.g., `docs/ECMA-335.pdf` or search with `find . -iname "*ecma*335*.pdf"`).
   - Never dump the whole PDF. Use `pdfgrep`, `pdftotext` with page bounds (`pdftotext -f <start_page> -l <end_page> <path_to_pdf> -`), or the Python snippet below to extract ONLY the relevant 2–3 pages.
   - Return ONLY a concise summary containing:
     1. Exact byte offsets and field sizes.
     2. Coded index bit-width rules or flag enum hex values.
     3. Any sorting or alignment invariants specified by ECMA-335.

### ECMA-335 Partition II (Metadata & Physical Layout) Navigation Index
Give this exact section number to your subagent so it can jump straight to the right page via regex search (`II\.22\.26` or `22\.26 MethodDef`):

* **II.22 — Metadata Logical Format Tables (Alphabetical & by Hex ID):**
  * `II.22.1`  (`0x20`) `Assembly`
  * `II.22.2`  (`0x23`) `AssemblyRef`
  * `II.22.9`  (`0x0B`) `Constant` (sorted by `Parent` coded index)
  * `II.22.10` (`0x0C`) `CustomAttribute` (sorted by `Parent` coded index)
  * `II.22.11` (`0x0E`) `DeclSecurity`
  * `II.22.13` (`0x14`) `Event`
  * `II.22.15` (`0x04`) `Field`
  * `II.22.16` (`0x10`) `FieldLayout`
  * `II.22.17` (`0x0D`) `FieldMarshal`
  * `II.22.18` (`0x1D`) `FieldRVA` (4-byte `RVA` + `Field` table index)
  * `II.22.20` (`0x2A`) `GenericParam`
  * `II.22.21` (`0x2C`) `GenericParamConstraint`
  * `II.22.22` (`0x1C`) `ImplMap` (P/Invoke mapping)
  * `II.22.23` (`0x09`) `InterfaceImpl`
  * `II.22.24` (`0x28`) `ManifestResource` (4-byte `Offset`, 4-byte `Flags`, `Name`, `Implementation`)
  * `II.22.25` (`0x0A`) `MemberRef`
  * `II.22.26` (`0x06`) `MethodDef` (4-byte `RVA`, 2-byte `ImplFlags`, 2-byte `Flags`, `Name`, `Signature`, `ParamList`)
  * `II.22.27` (`0x19`) `MethodImpl`
  * `II.22.28` (`0x18`) `MethodSemantics`
  * `II.22.29` (`0x2B`) `MethodSpec`
  * `II.22.30` (`0x00`) `Module` (`Generation`, `Name`, `Mvid` #GUID index, `EncId`, `EncBaseId`)
  * `II.22.31` (`0x1A`) `ModuleRef`
  * `II.22.32` (`0x29`) `NestedClass`
  * `II.22.33` (`0x08`) `Param`
  * `II.22.34` (`0x17`) `Property`
  * `II.22.35` (`0x15`) `PropertyMap`
  * `II.22.36` (`0x11`) `StandAloneSig`
  * `II.22.37` (`0x02`) `TypeDef`
  * `II.22.38` (`0x01`) `TypeRef`
  * `II.22.39` (`0x1B`) `TypeSpec`
* **II.23 — Metadata Physical Layout:**
  * `II.23.1` Flags for attributes (`MethodAttributes`, `MethodImplAttributes`, `TypeAttributes`, `FieldAttributes`).
  * `II.23.2` Blobs and Signatures (compressed integers, calling conventions, `MethodDefSig`, `FieldSig`, `LocalVarSig`).
* **II.24 — Metadata Physical Header & Streams:**
  * `II.24.2.1` Metadata root (`BSJB` signature, version string, stream headers).
  * `II.24.2.2` `#Strings`, `#US`, `#Blob`, `#GUID` heaps.
  * `II.24.2.6` `#~` stream header (`Valid` 64-bit bitvector, `Sorted` 64-bit bitvector, `Rows` counts, Coded Index tag encodings).
* **II.25 — File Format Extensions to PE:**
  * `II.25.2` PE Headers & Section headers.
  * `II.25.3` CLI Header (`IMAGE_COR20_HEADER`, 72 bytes, `COMIMAGE_FLAGS_*`).
  * `II.25.4` Common Intermediate Language (CIL) physical layout:
    * `II.25.4.2` Tiny format method header (`CorILMethod_TinyFormat` = `0x02`).
    * `II.25.4.3` Fat format method header (`CorILMethod_FatFormat` = `0x03`).
    * `II.25.4.5` Exception handling clauses (`CorILMethod_Sect_EHTable`).

### Subagent Extraction Commands
Tell your subagent to use either `pdfgrep`/`pdftotext` (standard on Linux) or `pypdf` to extract only the matching pages:

Option A (CLI `pdfgrep` + `pdftotext`):
```bash
# 1. Find the page number where the section starts:
pdfgrep -n "II\.22\.26" <path/to/ECMA-335.pdf>
# 2. Extract only that page and the next page:
pdftotext -f <page> -l <page+1> <path/to/ECMA-335.pdf> -
```

Option B (Python `pypdf` fallback):
```bash
python3 -c "
import pypdf, sys, re
reader = pypdf.PdfReader(sys.argv[1])
pattern = re.compile(sys.argv[2])
for i, page in enumerate(reader.pages):
    text = page.extract_text()
    if pattern.search(text):
        print(f'=== PAGE {i+1} ===\n{text}')
" <path/to/ECMA-335.pdf> "II\.22\.26\s+MethodDef"
```

---

## 2. FAST-PATH CHEAT SHEET (NO SUBAGENT NEEDED FOR THESE)

For the most common `System.Reflection.Metadata` (SRM) and `ILStrip` operations, use these verified constants and APIs directly:

### 2.1 Zero-Math Table & Heap Offset Lookup (`MetadataReaderExtensions`)
Do not manually compute coded index widths if `MetadataReader` already has the image open. Use `System.Reflection.Metadata.Ecma335.MetadataReaderExtensions`:

```csharp
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

// Entire BSJB Metadata Root block:
unsafe
{
    ReadOnlySpan<byte> rawMetadata = new ReadOnlySpan<byte>(
        metadataReader.MetadataPointer, 
        metadataReader.MetadataLength);
}

// O(1) offset (from MetadataPointer), row stride, and count for any TableIndex:
int tableOffset = metadataReader.GetTableMetadataOffset(TableIndex.MethodDef);
int rowSize     = metadataReader.GetTableRowSize(TableIndex.MethodDef);
int rowCount    = metadataReader.GetTableRowCount(TableIndex.MethodDef);

// 1-based rowId byte offset inside rawMetadata:
int rowOffset = tableOffset + (rowId - 1) * rowSize;

// O(1) offset and byte size for any HeapIndex (#Strings, #US, #GUID, #Blob):
int heapOffset = metadataReader.GetHeapMetadataOffset(HeapIndex.String);
int heapSize   = metadataReader.GetHeapSize(HeapIndex.String);
```

### 2.2 `MethodDef` (`0x06`) & `FieldRVA` (`0x1D`) Fixed Column Offsets
All integer columns are Little-Endian:
- **`MethodDef` (`0x06`):**
  - `rowOffset + 0`: `uint32 RVA` (4 bytes)
  - `rowOffset + 4`: `uint16 ImplFlags` (2 bytes — `MethodImplAttributes.NoInlining` = `0x0008`)
  - `rowOffset + 6`: `uint16 Flags` (2 bytes — `MethodAttributes`)
  - `rowOffset + 8`: `Name` (`#Strings` index, 2 or 4 bytes), followed by `Signature` (`#Blob` index) and `ParamList` (`Param` index).
- **`FieldRVA` (`0x1D`):**
  - `rowOffset + 0`: `uint32 RVA` (4 bytes — MUST be updated if mapped field data shifts in `.text`!)
  - `rowOffset + 4`: `Field` (`Field` table index, 2 or 4 bytes).

### 2.3 CIL Tiny Method Header (`II.25.4.2`)
- A method body with < 64 bytes of IL, maxstack <= 8, no locals, and no EH uses a 1-byte Tiny header: `(byte)((codeSize << 2) | 0x02)`.
- For a 1-byte `ret` (`0x2A`) body:
  - Header byte: `(1 << 2) | 0x02` = `0x06`.
  - Opcode byte: `0x2A`.
  - Exact 2-byte sequence at the shared RVA: **`[ 0x06, 0x2A ]`**.