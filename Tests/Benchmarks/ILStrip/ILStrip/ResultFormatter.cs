using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Reports;

namespace ILStrip;

public record AssemblyComparison(
    string AssemblyName,
    long InputFileBytes,
    double BaselineMeanNs,
    double BaselineStdDevNs,
    double BaselineStdErrNs,
    double? CandidateMeanNs,
    double? CandidateStdDevNs,
    double? CandidateStdErrNs,
    long BaselineAllocatedBytes,
    long? CandidateAllocatedBytes,
    double BaselineGen0Per1000,
    double? CandidateGen0Per1000,
    double BaselineGen1Per1000,
    double? CandidateGen1Per1000,
    double BaselineGen2Per1000,
    double? CandidateGen2Per1000
)
{
    public double? Speedup => (CandidateMeanNs.HasValue && CandidateMeanNs.Value > 0)
        ? BaselineMeanNs / CandidateMeanNs.Value
        : null;

    public double? TimeReductionPercent => (CandidateMeanNs.HasValue && BaselineMeanNs > 0)
        ? ((BaselineMeanNs - CandidateMeanNs.Value) / BaselineMeanNs) * 100.0
        : null;

    public double? MemoryReductionPercent => (CandidateAllocatedBytes.HasValue && BaselineAllocatedBytes > 0)
        ? ((double)(BaselineAllocatedBytes - CandidateAllocatedBytes.Value) / BaselineAllocatedBytes) * 100.0
        : null;
}

public record BenchmarkAggregateReport(
    List<AssemblyComparison> Items,
    string BaselineName,
    string? CandidateName
)
{
    public bool HasCandidateComparison => Items.Any(i => i.CandidateMeanNs.HasValue);

    public double? GeometricMeanSpeedup
    {
        get
        {
            var valid = Items.Where(i => i.Speedup.HasValue).Select(i => i.Speedup!.Value).ToList();
            if (valid.Count == 0) return null;
            double logSum = valid.Sum(s => Math.Log(s));
            return Math.Exp(logSum / valid.Count);
        }
    }

    public double? TotalMemoryReductionPercent
    {
        get
        {
            long baseTotal = Items.Where(i => i.CandidateAllocatedBytes.HasValue).Sum(i => i.BaselineAllocatedBytes);
            long candTotal = Items.Where(i => i.CandidateAllocatedBytes.HasValue).Sum(i => i.CandidateAllocatedBytes!.Value);
            if (baseTotal <= 0) return null;
            return ((double)(baseTotal - candTotal) / baseTotal) * 100.0;
        }
    }
}

public static class ResultFormatter
{
    public static BenchmarkAggregateReport ExtractComparison(Summary summary, string baselineName, string? candidateName)
    {
        var comparisons = new List<AssemblyComparison>();
        string dataDir = BenchmarkConfig.DataDir;

        // Group benchmark cases by TargetAssembly parameter
        var groups = summary.Reports
            .GroupBy(r => r.BenchmarkCase.Parameters["TargetAssembly"]?.ToString() ?? "Unknown");

        foreach (var group in groups)
        {
            string assemblyName = group.Key;
            string inputPath = Path.Combine(dataDir, assemblyName);
            long fileBytes = File.Exists(inputPath) ? new FileInfo(inputPath).Length : 0;

            var baselineReport = group.FirstOrDefault(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name == nameof(Benchmarks.LegacyCecil));
            var candidateReport = group.FirstOrDefault(r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name == nameof(Benchmarks.NewSRM));

            double baseMeanNs = baselineReport?.ResultStatistics?.Mean ?? 0;
            double baseStdDevNs = baselineReport?.ResultStatistics?.StandardDeviation ?? 0;
            double baseStdErrNs = baselineReport?.ResultStatistics?.StandardError ?? 0;
            long baseAlloc = (baselineReport != null ? baselineReport.GcStats.GetBytesAllocatedPerOperation(baselineReport.BenchmarkCase) : null) ?? 0;
            double baseGen0 = baselineReport?.GcStats.Gen0Collections ?? 0;
            double baseGen1 = baselineReport?.GcStats.Gen1Collections ?? 0;
            double baseGen2 = baselineReport?.GcStats.Gen2Collections ?? 0;

            double? candMeanNs = candidateReport?.ResultStatistics?.Mean;
            double? candStdDevNs = candidateReport?.ResultStatistics?.StandardDeviation;
            double? candStdErrNs = candidateReport?.ResultStatistics?.StandardError;
            long? candAlloc = candidateReport != null ? candidateReport.GcStats.GetBytesAllocatedPerOperation(candidateReport.BenchmarkCase) : null;
            double? candGen0 = candidateReport?.GcStats.Gen0Collections;
            double? candGen1 = candidateReport?.GcStats.Gen1Collections;
            double? candGen2 = candidateReport?.GcStats.Gen2Collections;

            comparisons.Add(new AssemblyComparison(
                assemblyName,
                fileBytes,
                baseMeanNs,
                baseStdDevNs,
                baseStdErrNs,
                candMeanNs,
                candStdDevNs,
                candStdErrNs,
                baseAlloc,
                candAlloc,
                baseGen0,
                candGen0,
                baseGen1,
                candGen1,
                baseGen2,
                candGen2
            ));
        }

        return new BenchmarkAggregateReport(comparisons.OrderBy(c => c.InputFileBytes).ToList(), baselineName, candidateName);
    }

    public static string FormatMarkdownTable(BenchmarkAggregateReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# ILStrip Performance Benchmark Results");
        sb.AppendLine();
        sb.AppendLine($"- **Baseline (Legacy):** `{report.BaselineName}`");
        sb.AppendLine($"- **Candidate (New):** `{(report.CandidateName ?? "None (Baseline Profile Only)")}`");
        sb.AppendLine($"- **Timestamp:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        if (report.HasCandidateComparison)
        {
            sb.AppendLine("## Aggregate Key Indicators");
            sb.AppendLine();
            if (report.GeometricMeanSpeedup.HasValue)
                sb.AppendLine($"- 🚀 **Geometric Mean Speedup:** **{report.GeometricMeanSpeedup.Value:F2}x faster**");
            if (report.TotalMemoryReductionPercent.HasValue)
                sb.AppendLine($"- 💾 **Total Memory Allocation Reduction:** **{report.TotalMemoryReductionPercent.Value:F1}% fewer allocations**");
            sb.AppendLine();

            sb.AppendLine("## Detailed Comparison by Assembly");
            sb.AppendLine();
            sb.AppendLine("| Assembly | Size | Baseline Time (Mean ± σ) | Candidate Time (Mean ± σ) | Speedup | Baseline Alloc | Candidate Alloc | Memory Saved |");
            sb.AppendLine("|:---|---:|---:|---:|---:|---:|---:|---:|");

            foreach (var item in report.Items)
            {
                string sizeStr = FormatBytes(item.InputFileBytes);
                string baseTime = FormatDurationWithStdDev(item.BaselineMeanNs, item.BaselineStdDevNs);
                string candTime = item.CandidateMeanNs.HasValue
                    ? FormatDurationWithStdDev(item.CandidateMeanNs.Value, item.CandidateStdDevNs ?? 0)
                    : "N/A";
                string speedupStr = item.Speedup.HasValue ? $"**{item.Speedup.Value:F2}x** ({item.TimeReductionPercent:F1}%)" : "N/A";
                string baseAlloc = FormatBytes(item.BaselineAllocatedBytes);
                string candAlloc = item.CandidateAllocatedBytes.HasValue ? FormatBytes(item.CandidateAllocatedBytes.Value) : "N/A";
                string memSaved = item.MemoryReductionPercent.HasValue ? $"**{item.MemoryReductionPercent.Value:F1}%**" : "N/A";

                sb.AppendLine($"| `{item.AssemblyName}` | {sizeStr} | {baseTime} | {candTime} | {speedupStr} | {baseAlloc} | {candAlloc} | {memSaved} |");
            }
        }
        else
        {
            sb.AppendLine("## Baseline Profile (Cecil Legacy)");
            sb.AppendLine();
            sb.AppendLine("| Assembly | Size | Baseline Time (Mean ± σ) | Baseline Allocations | Gen0 Collections |");
            sb.AppendLine("|:---|---:|---:|---:|---:|");

            foreach (var item in report.Items)
            {
                string baseTime = FormatDurationWithStdDev(item.BaselineMeanNs, item.BaselineStdDevNs);
                sb.AppendLine($"| `{item.AssemblyName}` | {FormatBytes(item.InputFileBytes)} | {baseTime} | {FormatBytes(item.BaselineAllocatedBytes)} | {item.BaselineGen0Per1000:F1} |");
            }
        }

        return sb.ToString();
    }

    public static string FormatTerminalTable(BenchmarkAggregateReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("========================================================================================================================");
        sb.AppendLine("                                         ILStrip Benchmark Comparison Results                                           ");
        sb.AppendLine("========================================================================================================================");
        sb.AppendLine($"Baseline : {report.BaselineName}");
        sb.AppendLine($"Candidate: {(report.CandidateName ?? "None (Baseline Profile Only)")}");
        sb.AppendLine("------------------------------------------------------------------------------------------------------------------------");

        if (report.HasCandidateComparison)
        {
            if (report.GeometricMeanSpeedup.HasValue)
                sb.AppendLine($" >> Geometric Mean Speedup     : {report.GeometricMeanSpeedup.Value:F2}x FASTER");
            if (report.TotalMemoryReductionPercent.HasValue)
                sb.AppendLine($" >> Total Memory Saved         : {report.TotalMemoryReductionPercent.Value:F1}% REDUCTION");
            sb.AppendLine("------------------------------------------------------------------------------------------------------------------------");

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-28} | {1,8} | {2,20} | {3,20} | {4,9} | {5,11} | {6,11} | {7,10}",
                "Assembly", "Size", "Base Time (Mean ± σ)", "Cand Time (Mean ± σ)", "Speedup", "Base Alloc", "Cand Alloc", "Mem Saved"));
            sb.AppendLine(new string('-', 130));

            foreach (var item in report.Items)
            {
                string name = item.AssemblyName.Length > 28 ? item.AssemblyName[..25] + "..." : item.AssemblyName;
                string baseTime = FormatDurationWithStdDev(item.BaselineMeanNs, item.BaselineStdDevNs);
                string candTime = item.CandidateMeanNs.HasValue
                    ? FormatDurationWithStdDev(item.CandidateMeanNs.Value, item.CandidateStdDevNs ?? 0)
                    : "N/A";
                string speedup = item.Speedup.HasValue ? $"{item.Speedup.Value:F2}x" : "N/A";
                string memSaved = item.MemoryReductionPercent.HasValue ? $"{item.MemoryReductionPercent.Value:F1}%" : "N/A";

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-28} | {1,8} | {2,20} | {3,20} | {4,9} | {5,11} | {6,11} | {7,10}",
                    name,
                    FormatBytes(item.InputFileBytes),
                    baseTime,
                    candTime,
                    speedup,
                    FormatBytes(item.BaselineAllocatedBytes),
                    item.CandidateAllocatedBytes.HasValue ? FormatBytes(item.CandidateAllocatedBytes.Value) : "N/A",
                    memSaved));
            }
        }
        else
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-35} | {1,8} | {2,24} | {3,15}",
                "Assembly", "Size", "Base Time (Mean ± σ)", "Base Allocations"));
            sb.AppendLine(new string('-', 90));

            foreach (var item in report.Items)
            {
                string name = item.AssemblyName.Length > 35 ? item.AssemblyName[..32] + "..." : item.AssemblyName;
                string baseTime = FormatDurationWithStdDev(item.BaselineMeanNs, item.BaselineStdDevNs);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-35} | {1,8} | {2,24} | {3,15}",
                    name,
                    FormatBytes(item.InputFileBytes),
                    baseTime,
                    FormatBytes(item.BaselineAllocatedBytes)));
            }
        }

        sb.AppendLine("========================================================================================================================");
        return sb.ToString();
    }

    public static string FormatCsv(BenchmarkAggregateReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Assembly,SizeBytes,BaselineMeanNs,BaselineStdDevNs,BaselineStdErrNs,CandidateMeanNs,CandidateStdDevNs,CandidateStdErrNs,SpeedupRatio,TimeReductionPercent,BaselineAllocBytes,CandidateAllocBytes,MemoryReductionPercent");
        foreach (var item in report.Items)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2:F2},{3:F2},{4:F2},{5},{6},{7},{8},{9},{10},{11},{12}",
                item.AssemblyName,
                item.InputFileBytes,
                item.BaselineMeanNs,
                item.BaselineStdDevNs,
                item.BaselineStdErrNs,
                item.CandidateMeanNs?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                item.CandidateStdDevNs?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                item.CandidateStdErrNs?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                item.Speedup?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                item.TimeReductionPercent?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                item.BaselineAllocatedBytes,
                item.CandidateAllocatedBytes?.ToString() ?? "",
                item.MemoryReductionPercent?.ToString("F2", CultureInfo.InvariantCulture) ?? ""));
        }
        return sb.ToString();
    }

    public static void SaveOutputs(BenchmarkAggregateReport report, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string mdPath = Path.Combine(outputDir, "summary.md");
        string csvPath = Path.Combine(outputDir, "summary.csv");

        File.WriteAllText(mdPath, FormatMarkdownTable(report));
        File.WriteAllText(csvPath, FormatCsv(report));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
    }

    private static string FormatDurationWithStdDev(double meanNs, double stdDevNs)
    {
        if (meanNs < 1000)
            return $"{meanNs:F0} ± {stdDevNs:F0} ns";
        if (meanNs < 1_000_000)
            return $"{(meanNs / 1000.0):F2} ± {(stdDevNs / 1000.0):F2} µs";
        if (meanNs < 1_000_000_000)
            return $"{(meanNs / 1_000_000.0):F2} ± {(stdDevNs / 1_000_000.0):F2} ms";
        return $"{(meanNs / 1_000_000_000.0):F2} ± {(stdDevNs / 1_000_000_000.0):F2} s";
    }
}
