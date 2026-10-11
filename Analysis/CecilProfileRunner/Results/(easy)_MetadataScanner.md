# Workload 1: Metadata & Attribute Scanner Analysis

This document records the empirical profiling results for **Workload 1 (Metadata & Custom Attribute Scan)** executed on [`Microsoft.Maui.Controls.dll`](../input/Microsoft.Maui.Controls.dll) (2.04 MB / 2,141,480 bytes) using JetBrains **dotTrace** and **dotMemory**.

---

## 1. Workload Overview

* **Target Assembly:** `Microsoft.Maui.Controls.dll` (1,827 Types, 14,860 Methods, 5,899 Fields, 7,901 Custom Attributes).
* **Workload Description:** Read-only recursive traversal of all types, members, and custom attributes, unpacking constructor arguments and named properties (simulating `RemoveRegisterAttribute` / `LinkAttributesParser`).
* **Environment:** .NET 10.0 x64, Windows 11, Release mode, Mono.Cecil 0.11.6.

---

## 2. Performance & Timing Profile (JetBrains dotTrace)

### Call Tree Breakdown
![dotTrace Timeline Trace]((easy)_MetadataScanner_trace.png)

### Key Timing Observations:
1. **Front-Loaded Execution in `ReadAssembly`:**
   * Total `MetadataScanWorkload.Execute` time: **270 ms (100%)**.
   * `AssemblyDefinition.ReadAssembly`: **239 ms (88.5%)**.
   * The actual C# `foreach` inspection loop and attribute argument unpacking: **~5 ms (~1.8%)**.
   * **Takeaway:** Mono.Cecil with `ReadingMode.Immediate` front-loads virtually all execution time into reading and materializing the in-memory object graph. Once in memory, traversal is fast, but the initial materialization is the primary time bottleneck.

2. **Stop-the-World Garbage Collection Overhead:**
   * **GC Wait:** **18 ms (6.8% of runtime)**.
   * In performance-sensitive compiler/linking infrastructure, time spent in GC above 5% indicates severe memory pressure.
   * For a simple read-only metadata inspection task on a 2 MB file, losing nearly 7% of total runtime to GC pauses demonstrates Cecil's high allocation churn.

---

## 3. Memory & Allocation Profile (JetBrains dotMemory)

### A. Heap Lifecycle & Generation Promotion (Timeline)
![dotMemory Heap Timeline]((easy)_MetadataScanner_memory_timeline.png)

### Timeline Observations & Exact Heap Breakdown:
* **Total .NET Managed Heap:** **21.34 MB**
  * **Heap Generation 0:** **2.44 MB (11.4%)**
  * **Heap Generation 1:** **10.08 MB (47.2%)** *(Nearly half of all memory consists of Cecil metadata objects promoted from Gen 0 to Gen 1)*
  * **Heap Generation 2:** **2.47 MB (11.6%)** *(Long-lived module representations promoted to Gen 2)*
  * **LOH, POH and FOH:** **6.35 MB (29.8%)** *(Large Object Heap buffers from reading the 2.04 MB binary file)*

1. **Aggressive Heap Growth:**
   * The managed heap climbs from **0.5 MB to 21.34 MB** in less than 700 ms.
   * This confirms our measured allocation of **18.6 MB** on a 2.04 MB input assembly (**~9.1x allocation amplification**).
2. **Heavy Promotion to Generation 1 (47.2%):**
   * Rather than dying quickly in Gen 0, **10.08 MB** of Cecil's objects survived long enough during eager reading to be promoted into **Gen 1 (Pink)**.
   * Major collections involving Gen 1 and Gen 2 are significantly more expensive than minor Gen 0 collections, causing the wider yellow GC pause blocks visible on the timeline.
3. **Large Object Heap (LOH) Pressure (29.8%):**
   * **6.35 MB** landed directly in the Large Object Heap (Purple). Because `Microsoft.Maui.Controls.dll` is 2.04 MB, internal file reading and heap buffers exceed the 85,000-byte threshold, requiring expensive Gen 2 collections to reclaim.

---

### B. Allocation Call Tree & Object Churn (Memory Traffic)
![dotMemory Allocation Call Tree]((easy)_MetadataScanner_memory_calltree.png)

### Allocation Traffic Breakdown:
* **Total Objects Allocated:** **209,500 objects** (`209,5 K objects`).
* **Total Allocated Size:** **20.17 MB** (`19.54 MB` inside `Program.Main`).
* **Inside `ReadAssembly` (19.53 MB total):**
  * `ModuleReader.CreateModule`: **18.24 MB (90.4%)** (Allocating the metadata object graph: `TypeDefinition`, `MethodDefinition`, `CustomAttribute`, `Collection<T>`).
  * `ImageReader.ReadImage`: **1.29 MB (6.41%)** (Reading PE headers and section streams).
  * Underlying file streams: **< 0.01%**.

---

## 4. Bachelor Thesis Conclusions (Chapter 2 Evidence)

1. **Object-Graph Materialization vs. Raw Binary Parsing:**
   PE stream reading (`ReadImage`) accounts for only 1.29 MB of allocations and a small fraction of CPU time. The overwhelming bottleneck is `CreateModule` (18.24 MB, 90.4% of allocations and 88.5% of execution time), where Cecil instantiates over 200,000 heap objects for metadata rows.
2. **Memory Amplification Factor:**
   Parsing a **2.04 MB** assembly generates **20.17 MB** of heap traffic and **209,500 objects**—a **~9.1x memory amplification** without modifying or saving a single byte.
3. **Contrast with `System.Reflection.Metadata` (SRM):**
   Modern zero-allocation architectures using SRM navigate metadata via lightweight value-type handles (`struct TypeDefinitionHandle`, `struct CustomAttributeHandle`, `struct BlobReader`) directly over memory-mapped spans, eliminating both the 200,000 object allocations and the 6.8% GC freeze.
