# ILStrip Performance Benchmark Results

- **Baseline (Legacy):** `AssemblyStripper.dll`
- **Candidate (New):** `SrmMetadataBuilderStrip.dll`
- **Timestamp:** 2026-10-23 08:44:15 UTC

## Aggregate Key Indicators

- 🚀 **Geometric Mean Speedup:** **8,49x faster**
- 💾 **Total Memory Allocation Reduction:** **84,8% fewer allocations**

## Detailed Comparison by Assembly

| Assembly | Size | Baseline Time (Mean ± σ) | Candidate Time (Mean ± σ) | Speedup | Baseline Alloc | Candidate Alloc | Memory Saved |
|:---|---:|---:|---:|---:|---:|---:|---:|
| `System.Threading.Thread.dll` | 4,5 KB | 634,23 ± 28,25 µs | 531,38 ± 44,79 µs | **1,19x** (16,2%) | 68,6 KB | 144,6 KB | **-110,9%** |
| `System.Text.Json.dll` | 351,5 KB | 56,80 ± 2,17 ms | 4,16 ± 0,14 ms | **13,65x** (92,7%) | 17,76 MB | 2,61 MB | **85,3%** |
| `MauiStripSample.dll` | 727,0 KB | 170,40 ± 4,94 ms | 12,82 ± 1,46 ms | **13,29x** (92,5%) | 26,94 MB | 5,23 MB | **80,6%** |
| `System.Private.CoreLib.dll` | 1,85 MB | 307,75 ± 5,44 ms | 19,16 ± 0,90 ms | **16,06x** (93,8%) | 87,96 MB | 11,94 MB | **86,4%** |
| `Microsoft.Maui.Controls.dll` | 2,04 MB | 339,29 ± 16,50 ms | 26,78 ± 2,10 ms | **12,67x** (92,1%) | 86,36 MB | 13,42 MB | **84,5%** |
