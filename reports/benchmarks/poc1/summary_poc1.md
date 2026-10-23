# ILStrip Performance Benchmark Results

- **Baseline (Legacy):** `AssemblyStripper.dll`
- **Candidate (New):** `SrmIlStrip.dll`
- **Timestamp:** 2026-10-23 08:58:41 UTC

## Aggregate Key Indicators

- 🚀 **Geometric Mean Speedup:** **49,25x faster**
- 💾 **Total Memory Allocation Reduction:** **99,6% fewer allocations**

## Detailed Comparison by Assembly

| Assembly | Size | Baseline Time (Mean ± σ) | Candidate Time (Mean ± σ) | Speedup | Baseline Alloc | Candidate Alloc | Memory Saved |
|:---|---:|---:|---:|---:|---:|---:|---:|
| `System.Threading.Thread.dll` | 4,5 KB | 634,30 ± 21,64 µs | 460,25 ± 23,36 µs | **1,38x** (27,4%) | 68,6 KB | 69,0 KB | **-0,7%** |
| `System.Text.Json.dll` | 351,5 KB | 58,04 ± 3,02 ms | 743,59 ± 28,13 µs | **78,06x** (98,7%) | 17,76 MB | 111,2 KB | **99,4%** |
| `MauiStripSample.dll` | 727,0 KB | 171,03 ± 3,94 ms | 914,71 ± 48,55 µs | **186,98x** (99,5%) | 26,94 MB | 87,4 KB | **99,7%** |
| `System.Private.CoreLib.dll` | 1,85 MB | 319,82 ± 12,37 ms | 2,42 ± 0,10 ms | **132,00x** (99,2%) | 87,96 MB | 293,0 KB | **99,7%** |
| `Microsoft.Maui.Controls.dll` | 2,04 MB | 343,96 ± 17,74 ms | 3,15 ± 0,10 ms | **109,10x** (99,1%) | 86,36 MB | 301,7 KB | **99,7%** |
