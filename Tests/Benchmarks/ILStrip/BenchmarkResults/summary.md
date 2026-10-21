# ILStrip Performance Benchmark Results

- **Baseline (Legacy):** `AssemblyStripper.dll`
- **Candidate (New):** `AssemblyStripper.dll`
- **Timestamp:** 2026-10-21 07:26:03 UTC

## Aggregate Key Indicators

- 🚀 **Geometric Mean Speedup:** **0.96x faster**
- 💾 **Total Memory Allocation Reduction:** **0.0% fewer allocations**

## Detailed Comparison by Assembly

| Assembly | Size | Baseline Time (Mean ± σ) | Candidate Time (Mean ± σ) | Speedup | Baseline Alloc | Candidate Alloc | Memory Saved |
|:---|---:|---:|---:|---:|---:|---:|---:|
| `System.Threading.Thread.dll` | 4.5 KB | 37.69 ± 63.24 ms | 39.18 ± 65.91 ms | **0.96x** (-4.0%) | 70.1 KB | 70.1 KB | **0.0%** |
