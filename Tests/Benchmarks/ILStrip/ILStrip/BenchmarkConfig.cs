using System;
using System.IO;

namespace ILStrip;

public static class BenchmarkConfig
{
    private static string? _baselineDll;
    private static string? _candidateDll;
    private static string? _dataDir;
    private static string? _scratchDir;

    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;
            string env = Environment.GetEnvironmentVariable("ILSTRIP_DATA_DIR") ?? "";
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
                return _dataDir = Path.GetFullPath(env);

            // Auto-locate repo root
            string current = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                string candidate = Path.Combine(current, "Tests", "data", "dlls", "ILStrip", "input");
                if (Directory.Exists(candidate))
                    return _dataDir = Path.GetFullPath(candidate);

                current = Path.GetDirectoryName(current)!;
            }

            throw new DirectoryNotFoundException("Could not locate Tests/data/dlls/ILStrip/input directory.");
        }
        set => _dataDir = value;
    }

    public static string BaselineDll
    {
        get
        {
            if (_baselineDll != null) return _baselineDll;
            string env = Environment.GetEnvironmentVariable("ILSTRIP_BASELINE_DLL") ?? "";
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                return _baselineDll = Path.GetFullPath(env);

            string current = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                string rebuilt = Path.Combine(current, "rebuilt-tools", "AssemblyStripper.dll");
                if (File.Exists(rebuilt))
                    return _baselineDll = Path.GetFullPath(rebuilt);

                string replay = Path.Combine(current, "referent", "ilstrip-compatibility", "replay", "bin", "Release", "net10.0", "AssemblyStripper.dll");
                if (File.Exists(replay))
                    return _baselineDll = Path.GetFullPath(replay);

                current = Path.GetDirectoryName(current)!;
            }

            throw new FileNotFoundException("Could not locate baseline AssemblyStripper.dll.");
        }
        set => _baselineDll = value;
    }

    public static string? CandidateDll
    {
        get
        {
            if (_candidateDll != null) return _candidateDll;
            string env = Environment.GetEnvironmentVariable("ILSTRIP_CANDIDATE_DLL") ?? "";
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                return _candidateDll = Path.GetFullPath(env);

            return null;
        }
        set => _candidateDll = value;
    }

    public static string ScratchDir
    {
        get
        {
            if (_scratchDir != null) return _scratchDir;
            _scratchDir = Path.Combine(Path.GetTempPath(), "ILStripBenchmarks", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_scratchDir);
            return _scratchDir;
        }
    }

    public static string FindDefaultResultsDir()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, "Tests", "Benchmarks", "ILStrip");
            if (Directory.Exists(candidate))
                return Path.Combine(candidate, "BenchmarkResults");

            current = Path.GetDirectoryName(current)!;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkResults");
    }

    public static void CleanScratch()
    {
        if (_scratchDir != null && Directory.Exists(_scratchDir))
        {
            try { Directory.Delete(_scratchDir, recursive: true); } catch { }
            _scratchDir = null;
        }
    }
}
