# Workload 2: Macro Assembly Stripper Analysis

This document records the empirical profiling results for **Workload 2 (Macro Assembly Stripping)** executed on [`Microsoft.Maui.Controls.dll`](../input/Microsoft.Maui.Controls.dll) (2.04 MB input / 2,141,480 bytes $\rightarrow$ 1.09 MB output / 1,093,632 bytes, $-48.9\%$) using JetBrains **dotTrace** and **dotMemory**.

---

## 1. Workload Overview

* **Target Assembly:** `Microsoft.Maui.Controls.dll` (1,827 Types, 14,860 Methods, 5,899 Fields).
* **Workload Description:** Full assembly load with immediate reading, iterating through all methods, stripping bodies for all 14,182 non-abstract/non-runtime methods (clearing instructions, emitting a single `OpCodes.Ret`, marking `MethodImplAttributes.NoInlining`), and re-serializing the stripped assembly back to disk via `AssemblyDefinition.Write`.
* **Real-World Modeling:** Models the post-AOT IL stripping stage in `dotnet/runtime` (`AssemblyStripper.cs`), where executable IL is removed from pre-compiled NativeAOT assemblies to reduce deployment payload size.
* **Environment:** .NET 10.0 x64, Windows 11, Release mode, Mono.Cecil 0.11.6.

---

## 2. Performance & Timing Profile (JetBrains dotTrace)

### Call Tree Breakdown
![dotTrace Timeline Trace]((medium)_AssemblyStripper_trace.png)

### Key Timing Observations:

1. **The Symmetrical 50/50 Read vs. Write Split:**
   * Total `Main` execution time: **538 ms (100%)**.
   * `AssemblyDefinition.ReadAssembly`: **246 ms (45.8%)**.
   * In-memory IL stripping loop (14,182 methods): **~7 ms (~1.4%)**.
   * `AssemblyDefinition.Write`: **257 ms (47.8%)**.
   * **Takeaway:** The actual domain logic (stripping instructions from over 14,000 methods) takes only **7 ms (1.4% of total runtime)**. The remaining **98.6% of execution time** is purely spent on Cecil's object-model materialization and re-serialization overhead.

2. **The "Write Bottleneck" & Metadata Re-Synthesis:**
   * Re-serializing the 1.09 MB stripped assembly requires **257 ms**, yielding an effective write throughput of only **~4.25 MB/s**.
   * Internal breakdown of `AssemblyDefinition.Write` (257 ms):
     * `MetadataBuilder.BuildMetadata`: **199 ms (~78% of write time)**:
       * `MetadataBuilder.BuildModule`: **140 ms (54.5% of write time)** — Cecil walks the entire managed object graph to re-synthesize CLI metadata table rows from scratch.
       * `MetadataBuilder.WriteStrings`: **31 ms (12.1% of write time)** — Cecil re-hashes and re-interns untouched strings into the `#Strings` heap.
       * `MetadataBuilder.WriteTableHeap`: **25 ms (9.7% of write time)** — Encoding rows into the compressed `#~` metadata stream.
     * Physical PE writing / `ImageWriter` disk I/O: **~52 ms (~20% of write time)**.

3. **Severe Garbage Collection Pauses:**
   * **GC Wait:** **44 ms (8.13% of total runtime)**.
   * Stop-the-world GC pauses doubled compared to Workload 1 (18 ms $\rightarrow$ 44 ms), claiming more than **8% of the application's entire lifespan**.

---

## 3. Memory & Allocation Profile (JetBrains dotMemory)

### A. Heap Lifecycle & Generation Promotion (Timeline)
![dotMemory Heap Timeline]((medium)_AssemblyStripper_memory_timeline.png)

### Timeline Observations & Heap Evolution:
* **Managed Heap Peak:** **~34 MB** (climbing from 0.5 MB).
* **The Two Allocation Waves:**
  * **Wave 1 (Read Phase, 0 ms – 250 ms):** Heap climbs sharply to ~18 MB as `ModuleReader.CreateModule` allocates Cecil's type and member definitions.
  * **Wave 2 (Write Phase, 250 ms – 540 ms):** Heap surges from ~18 MB to **~34 MB** as `MetadataBuilder` constructs builders, table rows, and string lookup dictionaries.
* **Persistent Promotion to Generation 2 (Green):**
  * During the 5 garbage collection cycles visible on the timeline, Cecil objects survive across collections.
  * Gen 0 (blue) repeatedly collapses, driving long-lived metadata objects into Gen 1 (pink) and ultimately **Gen 2 (green)**.
  * By the end of execution, Gen 2 dominates the heap (~18 MB), accompanied by Gen 1 (~8 MB) and LOH buffers (~7.5 MB). Almost no memory is retained in Gen 0.

---

### B. Allocation Call Tree & Memory Traffic
![dotMemory Allocation Call Tree]((medium)_AssemblyStripper_memory_calltree.png)

### Memory Traffic Breakdown:
* **Total Objects Allocated:** **33.38 MB total traffic** (32.74 MB inside `AssemblyStripWorkload.Execute`).
* **Inside `ReadAssembly`:** **18.18 MB (54.5%)** — Object graph materialization.
* **Inside `Write`:** **10.77 MB (32.3%)** — Metadata re-building, row generation, and string tables.
* **Inside In-Memory Stripping Loop:** **~3.79 MB (11.3%)**:
  * `Emit` (`new Instruction`): **1.15 MB (3.44%)**
  * `new MethodBody()`: **997.2 KB**
  * `GetILProcessor` (`new ILProcessor`): **755.5 KB (2.21%)**
  * `new Instruction[]` arrays: **376.0 KB**
  * `new InstructionCollection()`: **134.3 KB**
  * Linq enumerable iterators: **303.9 KB (0.89%)**

* **Allocation Amplification Factor:**
  * Transforming a **2.04 MB** input assembly into a **1.09 MB** output assembly generated **33.38 MB of heap allocations**—a **$16.4\times$ memory amplification factor** relative to the original binary size.

---

## 4. Bachelor Thesis Conclusions (Chapter 2 Evidence)

1. **The Cost of Decoupled Object Models on Write:**
   Mono.Cecil completely decouples in-memory objects (`TypeDefinition`, `MethodDefinition`) from the underlying physical binary layout. While this provides a convenient high-level API, saving requires a full re-synthesis of all metadata tables (`BuildModule`, 140 ms) and string deduplication heaps (`WriteStrings`, 31 ms).

2. **Disproportionate Churn for Micro-Mutations:**
   The developer's actual mutation (clearing instructions and emitting `ret`) took only **7 ms** and generated **~3.8 MB** of temporary allocations. Cecil's overhead to support this mutation was **503 ms** (246 ms Read + 257 ms Write) and **28.95 MB** of allocations (18.18 MB Read + 10.77 MB Write). Over **98% of runtime** and **88% of memory allocations** were infrastructure overhead.

3. **Contrast with Zero-Copy / Streaming Models (`System.Reflection.Metadata`):**
   Modern toolchains like `dotnet/runtime`'s current `AssemblyStripper` rely on `System.Reflection.Metadata` (SRM) and `System.Reflection.Metadata.Ecma335`. Because SRM operates on raw byte spans, unaffected metadata heaps (`#Strings`, `#US`, unchanged method metadata rows) can be **stream-copied directly or pass-through cloned** without parsing or re-interning. This eliminates both the 199 ms `BuildMetadata` re-synthesis and the 10.8 MB of write allocations.
