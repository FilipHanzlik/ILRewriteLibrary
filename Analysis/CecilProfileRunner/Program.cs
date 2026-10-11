using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CecilProfileRunner;

public static class Program
{
    public static int Main(string[] args)
    {

        // Parse CLI arguments
        string inputPath = FindDefaultInputAssembly();
        string outputDir = Directory.Exists("output") ? Path.GetFullPath("output") : Path.Combine(AppContext.BaseDirectory, "output");
        string selectedWorkload = "all";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--input":
                case "-i":
                    if (i + 1 < args.Length) inputPath = Path.GetFullPath(args[++i]);
                    break;
                case "--workload":
                case "-w":
                    if (i + 1 < args.Length) selectedWorkload = args[++i].ToLowerInvariant();
                    break;
                case "--output-dir":
                case "-o":
                    if (i + 1 < args.Length) outputDir = Path.GetFullPath(args[++i]);
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        if (!File.Exists(inputPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: Target input assembly not found at: {inputPath}");
            Console.ResetColor();
            return 1;
        }

        Directory.CreateDirectory(outputDir);


        bool runAll = selectedWorkload == "all";

        // Workload 1: Metadata Scan
        if (runAll || selectedWorkload == "metadata-scan" || selectedWorkload == "1")
        {
            MetadataScanWorkload.Execute(inputPath);
        }

        // Workload 2: Assembly Strip
        if (runAll || selectedWorkload == "assembly-strip" || selectedWorkload == "2")
        {
            string strippedOutput = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(inputPath)}_stripped.dll");
            AssemblyStripWorkload.Execute(inputPath, strippedOutput);
        }

        // Workload 3: KeepAlives Injection
        if (runAll || selectedWorkload == "keepalive-inject" || selectedWorkload == "3")
        {
            string keepAliveOutput = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(inputPath)}_keepalives.dll");
            KeepAlivesWorkload.Execute(inputPath, keepAliveOutput);

        }

        return 0;
    }

    private static string FindDefaultInputAssembly()
    {
        // Try known relative paths from various execution roots
        string[] candidates =
        {
            Path.Combine("input", "Microsoft.Maui.Controls.dll"),
            Path.Combine(AppContext.BaseDirectory, "input", "Microsoft.Maui.Controls.dll"),
            Path.Combine("referent", "ilstrip-compatibility", "corpus", "inputs", "015", "Microsoft.Maui.Controls.dll"),
            Path.Combine("..", "..", "..", "..", "referent", "ilstrip-compatibility", "corpus", "inputs", "015", "Microsoft.Maui.Controls.dll"),
            Path.Combine("..", "..", "..", "referent", "ilstrip-compatibility", "corpus", "inputs", "015", "Microsoft.Maui.Controls.dll"),
            Path.Combine("..", "referent", "ilstrip-compatibility", "corpus", "inputs", "015", "Microsoft.Maui.Controls.dll")
        };

        foreach (var candidate in candidates)
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(full))
                return full;
        }

        return Path.GetFullPath(candidates[0]);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run -c Release -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -i, --input <path>         Path to input assembly (default: inputs/015/Microsoft.Maui.Controls.dll)");
        Console.WriteLine("  -w, --workload <name>      Workload to run: all, metadata-scan (1), assembly-strip (2), keepalive-inject (3)");
        Console.WriteLine("  -o, --output-dir <path>    Directory for written assemblies (default: output/)");
        Console.WriteLine("  -h, --help                 Display this help message");
    }
}
