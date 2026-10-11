# Cecil Profile Runner

A standalone, high-precision benchmarking and profiling harness designed to measure the memory allocation churn, GC overhead, and execution time of **Mono.Cecil** across 3 representative real-world workloads on a production assembly ([`Microsoft.Maui.Controls.dll`](../../referent/ilstrip-compatibility/corpus/inputs/015/Microsoft.Maui.Controls.dll), 2.04 MB).

This tool directly supports **Chapter 2 (Motivation: Why Mono.Cecil is Memory-Heavy and Slow)** of the bachelor thesis.

---

## 1. The 3 Representative Workloads

The workloads directly model production rewriting patterns identified across `dotnet/runtime` and `dotnet/android`:

| Workload | Real-World Analog | Description | Cecil Cost Profile |
| :--- | :--- | :--- | :--- |
| **1. Metadata & Attribute Scan** | [`RemoveRegisterAttribute`](https://github.com/dotnet/android/blob/main/src/Xamarin.Android.Build.Tasks/Tasks/RemoveRegisterAttribute.cs) / `LinkAttributesParser` | Read-only recursive traversal of all `TypeDefinition`, `MethodDefinition`, `FieldDefinition`, and `CustomAttribute` instances with full argument unpacking. | **Object-Graph Materialization:** Pure reading forces creation of thousands of heap objects and boxed values. |
| **2. Whole-Assembly Stripping** | [`AssemblyStripper`](https://github.com/dotnet/runtime/blob/main/src/tasks/AssemblyStripper/AssemblyStripper.cs) (Post-AOT IL stripping) | Replaces all method bodies with minimal `ret` stubs, marks `NoInlining`, and serializes the rewritten PE to disk. | **Full Table Re-Emission:** Even with identical metadata, Cecil reconstructs `#~`, `#Strings`, `#Blob`, and PE sections. |
| **3. Control-Flow KeepAlives Injection** | [`AddKeepAlivesHelper`](https://github.com/dotnet/android/blob/main/src/Xamarin.Android.Build.Tasks/Linker/MonoDroid.Tuner/AddKeepAlivesHelper.cs) | Identifies methods with reference parameters, reads matching Portable PDB symbols, inserts `ldarg` + `call System.GC::KeepAlive(object)` before every `ret`, retargets branch operands, synchronizes sequence point offsets, and re-emits both DLL and updated PDB. | **Instruction Model, Offsets & PDB Trees:** Churn of linked `Instruction` objects, branch recalculations, `MemberRef` resolution, and full Portable PDB tree synchronization. |

---

## 2. Quick Start

### Build
```powershell
dotnet build Analysis/CecilProfileRunner -c Release
```

### Run All Workloads
```powershell
dotnet run --project Analysis/CecilProfileRunner -c Release -- --iterations 2
```

### Run a Specific Workload
```powershell
# Workload 1: Metadata scan
dotnet run --project Analysis/CecilProfileRunner -c Release -- -w metadata-scan

# Workload 2: Stripping
dotnet run --project Analysis/CecilProfileRunner -c Release -- -w assembly-strip

# Workload 3: KeepAlive injection
dotnet run --project Analysis/CecilProfileRunner -c Release -- -w keepalive-inject
```

### CLI Options
- `-i, --input <path>`: Input assembly path (defaults to corpus `inputs/015/Microsoft.Maui.Controls.dll`).
- `-w, --workload <all|metadata-scan|assembly-strip|keepalive-inject>`: Workload selection.
- `-o, --output-dir <path>`: Directory for emitted assemblies (default: `output/`).
- `-n, --iterations <count>`: Number of iterations (warmup + steady state).
- `--interactive`: Pauses with `Console.ReadLine()` before and after workloads to easily attach or capture snapshots in JetBrains dotMemory / dotTrace.

---

## 3. Profiling with JetBrains Tools

### A. Memory Profiling with dotMemory
1. Launch `dotMemory` (or Rider's built-in memory profiler).
2. Choose **Profile .NET Process / Application**.
3. Select the compiled executable: `Analysis\CecilProfileRunner\bin\Release\net10.0\CecilProfileRunner.exe` with arguments `--interactive -w all`.
4. Run the process:
   - When prompted *"Attach dotTrace / dotMemory... then press [Enter]"*, take **Baseline Snapshot**.
   - After each workload runs and prompts you, take **Workload Snapshot**.
5. Analyze snapshot comparisons:
   - Group by **Type** or **Namespace**.
   - Inspect top allocations: `Mono.Cecil.Cil.Instruction`, `Mono.Cecil.TypeDefinition`, `Mono.Cecil.CustomAttributeArgument`, etc.
   - Note the memory amplification factor (heap allocation vs. physical DLL size).

### B. Performance Profiling with dotTrace
1. Launch `dotTrace` with **Timeline Profiling** mode enabled (Timeline mode is critical to visualize Garbage Collection pauses and thread stalls).
2. Profile the command:
   ```powershell
   dotnet run --project Analysis/CecilProfileRunner -c Release -- -n 5 -w all
   ```
3. In the timeline view:
   - Filter by **Garbage Collection**: Observe time spent in GC pauses (Gen 0 / Gen 1 churn).
   - In **Top Methods**: Identify bottlenecks like `MetadataBuilder.BuildMetadata`, `Instruction.Create`, and `TypeReference.Resolve`.

---

## 4. Empirical Baseline Numbers (`Microsoft.Maui.Controls.dll`, 2.04 MB)

Measured in steady-state (Iteration 2, .NET 10 x64, Release mode):

| Workload | Time (ms) | Heap Allocated | Allocation Factor | Peak Working Set | GC Collections (0/1/2) | Key Metric |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| **1. Metadata Scan** | ~100 ms | **18.62 MB** | **9.1x** | 52.5 MB | 2 / 2 / 1 | 1,827 Types, 7,901 Attributes |
| **2. Assembly Strip** | ~306 ms | **37.22 MB** | **18.2x** | 73.2 MB | 4 / 3 / 1 | 14,182 Methods stripped (-48.9% file size) |
| **3. KeepAlives Inject** | ~492 ms | **78.81 MB** | **38.6x** | 108.0 MB | 11 / 7 / 2 | 29,384 Instructions injected, 1,089 branches repaired |

### Key Thesis Takeaways:
1. **Zero-mutation reads still allocate ~9x the file size**: Merely inspecting attributes and signatures creates massive heap pressure due to Cecil's object-oriented type system.
2. **Instruction mutation multiplies memory by ~39x**: Adding 29K instructions causes 78 MB of allocations and triggered 20 GC events in less than half a second.
3. **Mandatory External Resolution**: When re-emitting an assembly with default parameter values, Cecil forces resolution of external assemblies (e.g. `Microsoft.Maui.dll`), demonstrating that Cecil cannot operate as an isolated binary rewriter without a full assembly resolver.
