# Workload 3: Control-Flow Scan, KeepAlive Injection & PDB Synchronization Analysis

This document records the empirical profiling results for **Workload 3 (KeepAlive Injection & Portable PDB Re-writing)** executed on [`Microsoft.Maui.Controls.dll`](../input/Microsoft.Maui.Controls.dll) (2.04 MB input DLL + 891 KB matching Portable PDB) using JetBrains **dotTrace** and **dotMemory**.

---

## 1. Workload Overview

* **Target Assembly:** `Microsoft.Maui.Controls.dll` (1,827 Types, 14,860 Methods, 5,899 Fields) alongside matching Portable PDB.
* **Workload Description:** 
  1. Full assembly and Portable PDB symbol loading.
  2. Traversal of all types and methods with bodies.
  3. Control-flow inspection: identifying eligible reference-type parameters and tracking all return instructions (`OpCodes.Ret`).
  4. Instruction injection: inserting 29,384 instructions (parameter load + `GC.KeepAlive(object)` call before each return point).
  5. Branch retargeting: scanning instruction targets and redirecting 1,089 branch operands pointing to `ret` to target the head of the new injection sequence instead.
  6. Re-serialization of both rewritten PE assembly (2.24 MB) and synchronized Portable PDB (891 KB) via `AssemblyDefinition.Write`.
* **Real-World Modeling:** Models the Android SDK `AddKeepAlivesHelper` IL post-processing step used to prevent premature garbage collection of native Java peer objects during interop.
* **Environment:** .NET 10.0 x64, Windows 11, Release mode, Mono.Cecil 0.11.6.

---

## 2. Performance & Timing Profile (JetBrains dotTrace)

### Call Tree Breakdown
![dotTrace Timeline Trace]((hard)_KeepAlives_trace.png)

### Key Timing Observations:

1. **Massive Garbage Collection Freeze (14.83% of Total Runtime):**
   * **GC Wait:** **`208 ms` out of `1,405 ms` (`14.83%`)**.
   * In a compiler or packaging toolchain, losing nearly 15% of execution time to stop-the-world GC pauses represents severe memory pressure.
   * Progression of GC pause times across tiers:
     * **Tier 1 (Scan):** 18 ms (6.8%)
     * **Tier 2 (Strip):** 44 ms (8.13%)
     * **Tier 3 (KeepAlive + PDB):** **208 ms (14.83%)** — an almost $12\times$ increase in GC pause time over Tier 1.

2. **The "On-Demand" Reading Overhead (`ModuleDefinition.Read` = 371 ms):**
   * Deserialization is split into two phases:
     * `AssemblyDefinition.ReadAssembly`: **`314 ms` (22.3%)** — PE headers, type/member tables, and PDB symbol provider setup.
     * `ModuleDefinition.Read`: **`371 ms` (26.4%)** — On-demand parsing of method bodies and sequence points as `method.Body` is accessed.
   * **Total Ingestion Time:** $314\text{ ms} + 371\text{ ms} = \mathbf{685\text{ ms}}$ (**48.7%** of runtime).

3. **The PDB Re-Serialization Penalty (`Write` = 618 ms):**
   * Writing doubled from 257 ms (in Workload 2) to **`618 ms` (44.0%)**.
   * Re-encoding the modified method bodies alongside the **Portable PDB** requires recalculating all sequence point offset deltas, document scopes, and variable scopes, dropping serialization throughput to **~5.0 MB/s**.

4. **Domain Logic vs. Infrastructure Cost:**
   * Actual mutation logic (`get_Body` 20 ms, `ImportReference` 11 ms, `OnInsert` 4.3 ms, `InsertBefore` 2.3 ms, `GetAllTypes` 1.0 ms) took **$< 40\text{ ms}$ ($< 3.0\%$ of total runtime)**.
   * More than **97% of CPU time** was spent purely on Cecil's object-model instantiation, serialization, and GC pauses.

---

## 3. Memory & Allocation Profile (JetBrains dotMemory)

### A. Heap Lifecycle & Generation Promotion (Timeline)
![dotMemory Heap Timeline]((hard)_KeepAlives_memory_timeline.png)

### Timeline Observations:
* **Peak Managed Heap:** Climbs from 0.5 MB to **~80 MB** ($39.2\times$ the size of the 2.04 MB input file).
* **Generation 2 Domination ("Sea of Green"):**
  * Generation 2 (green) occupies **~55–60 MB** of the ~80 MB heap.
  * Because Cecil instantiates individual heap objects for every instruction, operand, and sequence point across 14,860 methods, these objects survive multiple GC cycles during the method inspection loop and are permanently promoted into Gen 2.
* **16+ Stop-the-World GC Collections:**
  * At least 16 separate GC pause events are recorded on the timeline, causing constant collection pauses and Gen 2 full GC sweeps.
* **Late-Stage LOH Surge (Purple):**
  * During the `Write` phase (far right), the Large Object Heap steps up to allocate large contiguous output buffers for the rewritten PE file and Portable PDB streams.

---

### B. Allocation Call Tree & Memory Traffic
![dotMemory Allocation Call Tree]((hard)_KeepAlives_memory_calltree.png)

### Understanding "Total" vs. "Own" Allocations in dotMemory:
* **Total (Inclusive) Memory:** All allocations made within the method **plus** all sub-methods called down its entire execution tree.
  * *Example:* `KeepAlivesWorkload.Execute` has a Total allocation of **`112.30 MB`** because it encompasses `ReadAssembly`, `Write`, and all callee methods.
* **Own (Exclusive) Memory:** Allocations made **directly** by the statements inside that specific method body itself, excluding any callees.
  * *Example:* `KeepAlivesWorkload.Execute` has an Own allocation of **`2.84 MB`**, representing the temporary objects created directly in the injection loop:
    * `new Instruction[]`: 1.51 MB
    * `new List<Instruction>()`: 498.7 KB
    * `new List<ParameterDefinition>()`: 443.2 KB
    * `new ParameterDefinitionCollection()`: 220.7 KB
    * `new ILProcessor()`: 199.7 KB
    * $\sum \approx 2.84\text{ MB}$.

### Memory Traffic Breakdown:
* **Total Objects Allocated:** **`112.94 MB` total traffic** (`112.30 MB` inside `Execute`).
* **Inside `Write` (`ModuleWriter.Write`):** **`45.62 MB` (40.4%)**
  * `ModuleWriter.BuildMetadata`: `45.57 MB` (Metadata re-synthesis, string tables, and PDB blob tables).
* **Inside `ReadAssembly` (`AssemblyDefinition.ReadAssembly`):** **`32.97 MB` (29.2%)**
  * Type definitions, module headers, and PDB reader setup.
* **Inside `Read` (`ModuleDefinition.Read`):** **`27.29 MB` (24.2%)**
  * `<get_Body>b__41_0`: `25.24 MB` (On-demand instantiation of instruction objects and sequence points).
* **Inside In-Memory Injection Loop (Own of `Execute`):** **`2.84 MB` (2.5%)**

* **Allocation Amplification Factor:**
  * Modifying a **2.04 MB** DLL and an **891 KB** PDB generated **`112.94 MB` of heap allocations**—a **$55.4\times$ allocation amplification factor**.
  * Over **`105.88 MB` (93.7%)** of all allocated memory was pure Cecil infrastructure overhead.

---

## 4. Cross-Workload Empirical Synthesis (Thesis Chapter 2 Summary)

The following empirical data across all three profiling tiers forms the quantitative foundation for Chapter 2 ("Motivation: Why Mono.Cecil is Memory-Heavy and Slow"):

| Profiling Metric | Tier 1: `metadata-scan` (Easy) | Tier 2: `assembly-strip` (Medium) | Tier 3: `keepalive-inject` (Hard) |
| :--- | :--- | :--- | :--- |
| **Input Assembly Size** | 2.04 MB | 2.04 MB | 2.04 MB (+ 891 KB PDB) |
| **Output Assembly Size** | N/A (Read-only) | 1.09 MB (-48.9%) | 2.24 MB (+ 891 KB PDB) |
| **Total Wall-Clock Time** | 270 ms | 538 ms | **1,405 ms** |
| **Read / Ingestion Phase** | 239 ms (88.5%) | 246 ms (45.8%) | **685 ms** (314 ms + 371 ms, 48.7%) |
| **Write / Serialization Phase** | N/A | 257 ms (47.8%) | **618 ms** (44.0%) |
| **Domain Logic Time** | ~5 ms (~1.8%) | ~7 ms (~1.4%) | **< 40 ms (< 3.0%)** |
| **Stop-the-World GC Pause** | 18 ms (6.8%) | 44 ms (8.13%) | **208 ms (14.83%)** |
| **Peak Managed Heap** | 21.34 MB | ~34 MB | **~80 MB** |
| **Total Allocated Traffic** | 20.17 MB | 33.38 MB | **112.94 MB** |
| **Allocation Amplification** | $9.9\times$ | $16.4\times$ | **$55.4\times$** |
| **Framework Overhead %** | 98.2% | 98.6% | **97.0%** |

---

## 5. Bachelor Thesis Conclusions (Chapter 2 Evidence)

1. **Object Allocation Explosion for Fine-Grained IL:**
   While metadata scanning creates ~200,000 objects, manipulating instruction streams and PDB sequence points pushes allocations past **112 MB** ($55.4\times$ binary size). Every single bytecode instruction becomes a heap-allocated class (`Mono.Cecil.Cil.Instruction`), generating unsustainable pressure on the CLR garbage collector.
2. **GC Pause Escalation (The 15% Ceiling):**
   Because instructions and method bodies survive across method iteration, they get promoted from Gen 0 into Gen 1 and ultimately Gen 2 ("Sea of Green"). As a result, the runtime spent **208 ms (14.83%)** purely in GC freeze, severely degrading build/linking toolchain performance.
3. **The Necessity of a Zero-Allocation Span-Based Alternative (SRM):**
   In modern toolchains, `System.Reflection.Metadata` completely avoids allocating `Instruction` class instances. Instead, IL bytes are inspected and decoded as raw `ReadOnlySpan<byte>` buffers, and Portable PDB sequence points are read via lightweight value-type structs (`SequencePointCollection`). This architectural shift eliminates the 112 MB allocation traffic and the 208 ms GC pause entirely.
