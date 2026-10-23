# ILStrip Performance Benchmark Results

- **Baseline (Legacy):** `AssemblyStripper.dll`
- **Candidate (New):** `SrmIlStrip.dll`
- **Timestamp:** 2026-10-22 08:35:52 UTC

## Aggregate Key Indicators

- 🚀 **Geometric Mean Speedup:** **3,70x faster**
- 💾 **Total Memory Allocation Reduction:** **99,6% fewer allocations**

## Detailed Comparison by Assembly

| Assembly | Size | Baseline Time (Mean ± σ) | Candidate Time (Mean ± σ) | Speedup | Baseline Alloc | Candidate Alloc | Memory Saved |
|:---|---:|---:|---:|---:|---:|---:|---:|
| `System.Threading.Thread.dll` | 4,5 KB | 1,23 ± 0,05 ms | 911,78 ± 49,85 µs | **1,35x** (26,1%) | 68,6 KB | 69,0 KB | **-0,7%** |
| `System.Text.Json.dll` | 351,5 KB | 85,28 ± 1,66 ms | 33,46 ± 1,22 ms | **2,55x** (60,8%) | 17,76 MB | 111,2 KB | **99,4%** |
| `MauiStripSample.dll` | 727,0 KB | 229,05 ± 10,83 ms | 43,13 ± 3,04 ms | **5,31x** (81,2%) | 26,94 MB | 87,4 KB | **99,7%** |
| `System.Private.CoreLib.dll` | 1,85 MB | 391,68 ± 6,80 ms | 65,03 ± 1,99 ms | **6,02x** (83,4%) | 87,96 MB | 297,0 KB | **99,7%** |
| `Microsoft.Maui.Controls.dll` | 2,04 MB | 408,08 ± 5,55 ms | 65,14 ± 0,61 ms | **6,26x** (84,0%) | 86,36 MB | 309,6 KB | **99,6%** |
