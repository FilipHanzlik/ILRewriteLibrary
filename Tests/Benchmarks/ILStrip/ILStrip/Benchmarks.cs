using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;

namespace ILStrip;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class Benchmarks
{
    private static StripperInvoker? _baselineInvoker;
    private static StripperInvoker? _candidateInvoker;

    private string _inputPath = "";
    private string _outputPathCecil = "";
    private string _outputPathCandidate = "";

    [ParamsSource(nameof(GetTargetAssemblies))]
    public string TargetAssembly { get; set; } = "";

    public static IEnumerable<string> GetTargetAssemblies()
    {
        string? customList = Environment.GetEnvironmentVariable("ILSTRIP_TARGET_ASSEMBLIES");
        if (!string.IsNullOrWhiteSpace(customList))
        {
            foreach (var item in customList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return item;
            }
            yield break;
        }

        // Representative tiers by size and complexity:
        // 1. Small: ~4.5 KB
        yield return "System.Threading.Thread.dll";
        // 2. Medium: ~352 KB
        yield return "System.Text.Json.dll";
        // 3. Application assembly (MAUI iOS/MacCatalyst sample): ~727 KB
        yield return "MauiStripSample.dll";
        // 4. Large UI framework assembly: ~2.1 MB
        yield return "Microsoft.Maui.Controls.dll";
        // 5. CoreLib BCL assembly: ~1.9 MB
        yield return "System.Private.CoreLib.dll";
    }

    [GlobalSetup]
    public void Setup()
    {
        string baselinePath = BenchmarkConfig.BaselineDll;
        _baselineInvoker = StripperInvoker.Load(baselinePath, "Cecil Baseline");

        string? candidatePath = BenchmarkConfig.CandidateDll;
        if (!string.IsNullOrEmpty(candidatePath) && File.Exists(candidatePath))
        {
            _candidateInvoker = StripperInvoker.Load(candidatePath, "SRM Candidate");
        }

        string dataDir = BenchmarkConfig.DataDir;
        _inputPath = Path.Combine(dataDir, TargetAssembly);
        if (!File.Exists(_inputPath))
        {
            throw new FileNotFoundException($"Corpus target assembly not found: '{_inputPath}'", _inputPath);
        }

        string scratch = BenchmarkConfig.ScratchDir;
        _outputPathCecil = Path.Combine(scratch, $"{TargetAssembly}.cecil.dll");
        _outputPathCandidate = Path.Combine(scratch, $"{TargetAssembly}.candidate.dll");
    }

    [Benchmark(Baseline = true, Description = "Legacy Cecil")]
    public void LegacyCecil()
    {
        _baselineInvoker!.Invoke(_inputPath, _outputPathCecil);
    }

    [Benchmark(Description = "New SRM")]
    public void NewSRM()
    {
        if (_candidateInvoker == null)
            throw new InvalidOperationException("No candidate stripper loaded. Pass --candidate <path> to enable.");

        _candidateInvoker.Invoke(_inputPath, _outputPathCandidate);
    }
}