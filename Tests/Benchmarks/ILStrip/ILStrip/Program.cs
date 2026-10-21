using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace ILStrip;

class Program
{
    static int Main(string[] args)
    {
        string? candidatePath = null;
        string? baselinePath = null;
        string? targetAssembly = null;
        bool runAll = true;
        bool quickMode = false;
        string outputDir = BenchmarkConfig.FindDefaultResultsDir();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--candidate":
                case "-c":
                    if (i + 1 < args.Length) candidatePath = args[++i];
                    break;
                case "--baseline":
                case "-b":
                    if (i + 1 < args.Length) baselinePath = args[++i];
                    break;
                case "--assembly":
                case "-a":
                    if (i + 1 < args.Length) targetAssembly = args[++i];
                    break;
                case "--sample":
                case "--target":
                case "-s":
                    runAll = false;
                    break;
                case "--quick":
                case "-q":
                    quickMode = true;
                    break;
                case "--out":
                case "-o":
                    if (i + 1 < args.Length) outputDir = Path.GetFullPath(args[++i]);
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        Console.WriteLine("==================================================================");
        Console.WriteLine("               ILStrip Performance Benchmark Runner               ");
        Console.WriteLine("==================================================================");

        try
        {
            // 1. Resolve baseline
            if (!string.IsNullOrEmpty(baselinePath))
                BenchmarkConfig.BaselineDll = Path.GetFullPath(baselinePath);

            Console.WriteLine($"Baseline DLL  : {BenchmarkConfig.BaselineDll}");

            // 2. Resolve candidate (if specified)
            if (!string.IsNullOrEmpty(candidatePath))
            {
                BenchmarkConfig.CandidateDll = Path.GetFullPath(candidatePath);
                Console.WriteLine($"Candidate DLL : {BenchmarkConfig.CandidateDll}");
            }
            else
            {
                Console.WriteLine("Candidate DLL : [None] (Profiling Cecil baseline only. Pass --candidate <path> to compare)");
            }

            // 3. Resolve assemblies
            Console.WriteLine($"Corpus Data   : {BenchmarkConfig.DataDir}");
            if (!string.IsNullOrEmpty(targetAssembly))
            {
                Environment.SetEnvironmentVariable("ILSTRIP_TARGET_ASSEMBLIES", targetAssembly);
                Console.WriteLine($"Filter        : Single assembly '{targetAssembly}'");
            }
            else if (runAll)
            {
                var allDlls = Directory.GetFiles(BenchmarkConfig.DataDir, "*.dll")
                    .Select(Path.GetFileName)
                    .Where(f => !string.IsNullOrEmpty(f))
                    .ToArray();
                Environment.SetEnvironmentVariable("ILSTRIP_TARGET_ASSEMBLIES", string.Join(",", allDlls));
                Console.WriteLine($"Filter        : Entire corpus ({allDlls.Length} assemblies)");
            }
            else
            {
                Console.WriteLine("Filter        : Representative tiers (Small, Medium, App, Large, CoreLib)");
            }

            // 4. Pass environment variables for BDN child processes
            Environment.SetEnvironmentVariable("ILSTRIP_BASELINE_DLL", BenchmarkConfig.BaselineDll);
            if (BenchmarkConfig.CandidateDll != null)
                Environment.SetEnvironmentVariable("ILSTRIP_CANDIDATE_DLL", BenchmarkConfig.CandidateDll);
            Environment.SetEnvironmentVariable("ILSTRIP_DATA_DIR", BenchmarkConfig.DataDir);

            // 5. Configure BenchmarkDotNet
            var config = ManualConfig.Create(DefaultConfig.Instance)
                .AddDiagnoser(MemoryDiagnoser.Default);

            if (quickMode)
            {
                Console.WriteLine("Execution Mode: QUICK (Dry run / reduced iterations for rapid testing)");
                config = config.AddJob(Job.Dry.WithWarmupCount(1).WithIterationCount(3));
            }
            else
            {
                Console.WriteLine("Execution Mode: STANDARD (Full statistical accuracy)");
            }

            // If no candidate was provided, filter out NewSRM to avoid errors
            if (string.IsNullOrEmpty(BenchmarkConfig.CandidateDll))
            {
                config = config.AddFilter(new NameFilter(name => name.Contains("LegacyCecil")));
            }

            // 6. Execute BenchmarkDotNet
            Console.WriteLine("\nStarting BenchmarkDotNet execution...\n");
            Summary results = BenchmarkRunner.Run<Benchmarks>(config);

            // 7. Extract and format results
            string baselineName = Path.GetFileName(BenchmarkConfig.BaselineDll);
            string? candidateName = BenchmarkConfig.CandidateDll != null ? Path.GetFileName(BenchmarkConfig.CandidateDll) : null;

            var comparisonReport = ResultFormatter.ExtractComparison(results, baselineName, candidateName);

            // Print formatted terminal table
            string terminalTable = ResultFormatter.FormatTerminalTable(comparisonReport);
            Console.WriteLine(terminalTable);

            // Save Markdown and CSV summaries
            ResultFormatter.SaveOutputs(comparisonReport, outputDir);
            Console.WriteLine($"\n[Artifacts Saved]");
            Console.WriteLine($" - Markdown Summary : {Path.Combine(outputDir, "summary.md")}");
            Console.WriteLine($" - CSV Raw Data     : {Path.Combine(outputDir, "summary.csv")}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            return 1;
        }
        finally
        {
            BenchmarkConfig.CleanScratch();
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
Usage: dotnet run -c Release -- [options]

Options:
  --candidate, -c <path>   Path to the new candidate ILStrip .dll (dynamically loaded).
  --baseline,  -b <path>   Path to the baseline AssemblyStripper.dll (defaults to rebuilt-tools).
  --assembly,  -a <name>   Benchmark a specific assembly (e.g., MauiStripSample.dll).
  --sample,    -s          Benchmark representative 5-assembly sample tiers instead of all 67.
  --quick,     -q          Fast dry-run mode (fewer iterations) for rapid iterative testing.
  --out,       -o <dir>    Output directory for summary.md and summary.csv (default: BenchmarkResults).
  --help,      -h          Show this help message.

Examples:
  dotnet run -c Release -- --quick
  dotnet run -c Release -- --candidate /path/to/MyILStrip.dll --quick
  dotnet run -c Release -- --candidate /path/to/MyILStrip.dll --assembly MauiStripSample.dll
");
    }
}