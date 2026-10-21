---
name: ilstrip-benchmarking
description: Run BenchmarkDotNet performance and memory benchmarks comparing a new candidate ILStrip implementation against the legacy Mono.Cecil baseline. Use for iterative performance optimization, profiling managed memory allocations, and measuring speedups across the 67-assembly corpus.
compatibility: Requires .NET 10 SDK (dotnet CLI) and access to the repository workspace
---

# ILStrip Performance Benchmarking Skill

This skill guides an agent developing a new `System.Reflection.Metadata` (SRM) based **ILStrip** (whole-assembly stripping) engine on how to execute, configure, and interpret performance benchmarks against the legacy `Mono.Cecil` baseline (`AssemblyStripper.dll`).

---

## 1. Quick Start: Running Benchmarks Against Your Candidate DLL

The benchmark project is located at `Tests/Benchmarks/ILStrip/ILStrip/ILStrip.csproj` and uses **BenchmarkDotNet** with `[MemoryDiagnoser]` to accurately track execution time, standard deviation, and byte-level managed heap allocations in-process.

### The Fast Iteration Command (~15–20 seconds)

When iterating on code changes, benchmark against the **5 representative sample tiers** in **quick mode**:

```bash
dotnet run --project Tests/Benchmarks/ILStrip/ILStrip/ILStrip.csproj -c Release -- \
  --candidate /path/to/MyNewILStrip.dll \
  --sample \
  --quick
```

*(Alternatively, you can use the helper script:)*
```bash
./.agents/skills/ilstrip-benchmarking/scripts/run-benchmark.sh /path/to/MyNewILStrip.dll --sample --quick
```

---

## 2. ⚠️ Critical Caution: Statistical Reliability of Quick Runs (`--quick`)

> [!WARNING]
> **Execution times from `--quick` runs are NOT statistically reliable for final comparisons!**
> 
> * **Why:** `--quick` (`-q`) executes a minimal dry run (`Job.Dry`) with only **3 cold iterations** and no extensive warmup phases. In managed runtimes (.NET), the first iteration includes JIT compilation and OS file caching. Background OS interrupts, CPU frequency shifts (e.g., Intel Turbo Boost on laptop CPUs), or a single Garbage Collection pause can drastically distort a 3-sample average.
> * **Real-World Evidence:** In empirical verification, running the exact same baseline stripper against itself in `--quick` mode produced an artificial **1.6x speedup** on one run purely due to timing jitter and cold-start scheduling!
> * **Rule of Thumb for Iterative Development:**
>   * ✅ **Allocated Memory (`Base Alloc`, `Cand Alloc`, `Mem Saved`):** 100% deterministic and reliable even in `--quick` mode. Use `--quick` confidently to verify that your memory optimizations and zero-allocation streaming are working.
>   * ⚠️ **Execution Time (`Speedup`):** Treat speedup numbers in `--quick` only as rough directional feedback.
>   * 🏆 **Definitive Claims & Thesis Data:** Always run without `--quick` (standard mode with warmups and statistical iterations) before concluding that an optimization improved speed.

---

## 3. Benchmark CLI Options & Workload Scopes

The benchmark runner supports the following options:

| Flag | Shorthand | Description | Default |
|:---|:---|:---|:---|
| `--candidate <path>` | `-c` | Path to the new stripper `.dll` (dynamically loaded in-process). | *None (profiles baseline only)* |
| `--baseline <path>` | `-b` | Path to baseline `AssemblyStripper.dll`. | `rebuilt-tools/AssemblyStripper.dll` |
| `--sample` | `-s` | Run only the **5 representative size tiers** instead of all 67 assemblies. | *Off (defaults to all 67 assemblies)* |
| `--assembly <name>` | `-a` | Run against a single specific assembly (e.g. `MauiStripSample.dll`). | *None* |
| `--quick` | `-q` | Fast dry-run mode (3 iterations, no warmups) for rapid iteration. | *Standard statistical run* |
| `--out <dir>` | `-o` | Output directory for markdown and CSV summary reports. | `Tests/Benchmarks/ILStrip/BenchmarkResults/` |

---

## 4. Recommended Development Workflow

### Step 1: Rapid Sanity & Memory Check (Single Assembly, ~5s)
When testing a new metadata writing pass, verify that it works and check its allocations on the real-world application assembly:
```bash
dotnet run --project Tests/Benchmarks/ILStrip/ILStrip/ILStrip.csproj -c Release -- \
  --candidate /path/to/MyNewILStrip.dll \
  --assembly MauiStripSample.dll \
  --quick
```

### Step 2: Tiered Sample Benchmark (~15–20s)
Once single-assembly stripping works, check all 5 representative complexity tiers:
```bash
dotnet run --project Tests/Benchmarks/ILStrip/ILStrip/ILStrip.csproj -c Release -- \
  --candidate /path/to/MyNewILStrip.dll \
  --sample \
  --quick
```
*The 5 representative tiers tested with `--sample` are:*
1. **Small (~4.5 KB):** `System.Threading.Thread.dll` (minimal tables, tiny method count)
2. **Medium (~352 KB):** `System.Text.Json.dll` (high method count, heavy reflection/generics)
3. **Application (~727 KB):** `MauiStripSample.dll` (real-world MAUI app with fat method headers & exception handlers)
4. **Large Framework (~2.1 MB):** `Microsoft.Maui.Controls.dll` (massive UI framework)
5. **CoreLib (~1.9 MB):** `System.Private.CoreLib.dll` (foundational BCL runtime assembly)

### Step 3: Full Corpus Benchmark (All 67 Assemblies, ~25–35 min)
For official thesis data, run the full 67-assembly corpus with standard statistical confidence:
```bash
dotnet run --project Tests/Benchmarks/ILStrip/ILStrip/ILStrip.csproj -c Release -- \
  --candidate /path/to/MyNewILStrip.dll
```

---

## 5. Candidate Stripper Entry Point Conventions

The benchmark dynamically loads the candidate `.dll` using `AssemblyLoadContext` and searches for any of the following entry points:

1. **Standard Static Methods:**
   * `public static void StripAssembly(string inputPath, string outputPath)`
   * `public static void Strip(string inputPath, string outputPath)`
   * `public static bool TryStripAssembly(string inputPath, string outputPath)`
2. **Any Public Static 2-Parameter Method:**
   * Any method with signature `(string, string)` returning `void` or `bool`.
3. **CLI Entry Point:**
   * `public static void Main(string[] args)` (invoked with `[inputPath, outputPath]`).

The entry point is bound to an `Action<string, string>` delegate during benchmark setup, ensuring **zero reflection overhead** during the timed benchmark loops.

---

## 6. Interpreting Benchmark Results

### Terminal Output Structure
```text
========================================================================================================================
                                         ILStrip Benchmark Comparison Results                                           
========================================================================================================================
Baseline : AssemblyStripper.dll
Candidate: MyNewILStrip.dll
------------------------------------------------------------------------------------------------------------------------
 >> Geometric Mean Speedup     : 3.82x FASTER
 >> Total Memory Saved         : 84.6% REDUCTION
------------------------------------------------------------------------------------------------------------------------
Assembly                     |     Size | Base Time (Mean ± σ) | Cand Time (Mean ± σ) |   Speedup |  Base Alloc |  Cand Alloc |  Mem Saved
----------------------------------------------------------------------------------------------------------------------------------
System.Threading.Thread.dll  |   4.5 KB |     23.90 ± 1.12 ms  |      6.10 ± 0.32 ms  |     3.92x |     69.6 KB |     12.1 KB |      82.6%
System.Text.Json.dll         | 351.5 KB |    152.71 ± 4.20 ms  |     39.20 ± 1.05 ms  |     3.90x |    17.76 MB |      2.10 MB|      88.2%
MauiStripSample.dll          | 727.0 KB |    284.56 ± 8.15 ms  |     72.40 ± 2.10 ms  |     3.93x |    26.94 MB |      3.85 MB|      85.7%
System.Private.CoreLib.dll   |  1.85 MB |    510.72 ± 12.3 ms  |    135.10 ± 3.45 ms  |     3.78x |    87.96 MB |     14.20 MB|      83.8%
Microsoft.Maui.Controls.dll  |  2.04 MB |    561.06 ± 14.1 ms  |    148.20 ± 4.10 ms  |     3.79x |    86.36 MB |     13.90 MB|      83.9%
========================================================================================================================
```

### Key Metrics to Track:
* **`Base Time (Mean ± σ)` & `Cand Time (Mean ± σ)`:** Execution time arithmetic mean and standard deviation ($\sigma$). Lower standard deviation indicates higher statistical stability.
* **`Speedup`:** Speedup factor ($T_{\text{base}} / T_{\text{cand}}$). Values $> 1.0\text{x}$ represent a speedup.
* **`Base Alloc` vs `Cand Alloc`:** Total managed heap memory allocated per single assembly stripping pass.
  * *Note:* Legacy Cecil allocates **~37x to 50x** the file size in managed objects (e.g., 88 MB for CoreLib).
  * *Target:* SRM-based implementation should reduce this by **80% to 90%**.
* **`Mem Saved`:** Percentage reduction in allocated bytes ($\frac{\text{Alloc}_{\text{base}} - \text{Alloc}_{\text{cand}}}{\text{Alloc}_{\text{base}}} \times 100\%$).
* **`Geometric Mean Speedup`:** The canonical multi-workload academic summary metric representing overall speedup across the benchmark suite.

---

## 7. Artifacts and Exports

After each run, results are automatically saved to `Tests/Benchmarks/ILStrip/BenchmarkResults/`:

1. **[`summary.md`](file:///home/filip/Documents/MFF/bakal%C3%A1%C5%99ka/Tests/Benchmarks/ILStrip/BenchmarkResults/summary.md):**
   Clean GitHub Flavored Markdown table with KPI cards, ready for copying into thesis reports, pull requests, or project notes.
2. **[`summary.csv`](file:///home/filip/Documents/MFF/bakal%C3%A1%C5%99ka/Tests/Benchmarks/ILStrip/BenchmarkResults/summary.csv):**
   Raw CSV file containing `BaselineMeanNs`, `BaselineStdDevNs`, `BaselineStdErrNs`, `CandidateMeanNs`, `CandidateStdDevNs`, `CandidateStdErrNs`, `SpeedupRatio`, `BaselineAllocBytes`, `CandidateAllocBytes`, and `MemoryReductionPercent`.
   Directly compatible with Python (`matplotlib`/`seaborn`) and LaTeX (`pgfplots`) for thesis charts.
