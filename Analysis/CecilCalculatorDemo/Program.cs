using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CecilCalculatorDemo;

internal static class Program
{
    private const int AdditionBonus = 10;
    private const string RewrittenGreeting = "Hello from the Cecil-rewritten DLL";

    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine("CecilCalculatorDemo [<calculator.dll> <new-output.dll>]");
            Console.WriteLine("Without arguments, uses the copied original sample and a new output directory.");
            Console.WriteLine("The input must have matching portable or embedded portable debug symbols.");
            return;
        }

        if (args.Length != 0 && args.Length != 2)
            throw new ArgumentException("Expected no arguments, or an input DLL and a new output DLL. Use --help for usage.");

        var inputPath = Path.GetFullPath(args.Length == 2
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "input", "DllExplorerSample.dll"));
        var outputPath = Path.GetFullPath(args.Length == 2
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "rewritten",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture),
                "DllExplorerSample.dll"));

        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output must be different files; the original calculator must remain unchanged.");
        if (File.Exists(outputPath))
            throw new IOException($"Output already exists: {outputPath}. Choose a new output path.");

        var originalHash = HashFile(inputPath);
        var cecil = typeof(AssemblyDefinition).Assembly;
        Console.WriteLine($"Cecil: {cecil.FullName}");
        Console.WriteLine($"Cecil path: {cecil.Location}");
        Console.WriteLine($"Cecil SHA-256: {HashFile(cecil.Location)}");
        Console.WriteLine($"Input: {inputPath}");
        Console.WriteLine($"Output: {outputPath}");

        RewriteCalculator(inputPath, outputPath);
        DemoVerification.Verify(inputPath, outputPath, AdditionBonus, RewrittenGreeting);

        if (HashFile(inputPath) != originalHash)
            throw new InvalidOperationException("The original calculator DLL changed during the demo.");

        Console.WriteLine("Verified all changes. The original calculator DLL is unchanged.");
    }

    private static void RewriteCalculator(string inputPath, string outputPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = true
        });

        var calculator = assembly.MainModule.GetType("DllExplorerSample.Calculator")
            ?? throw new InvalidOperationException("The input does not contain DllExplorerSample.Calculator.");
        var add = calculator.Methods.Single(method => method.Name == "Add");
        var addition = add.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Add);

        // Keep the original call-count update and add a bonus to the sum already on the stack.
        var il = add.Body.GetILProcessor();
        var loadBonus = il.Create(OpCodes.Ldc_I4, AdditionBonus);
        il.InsertAfter(addition, loadBonus);
        il.InsertAfter(loadBonus, il.Create(OpCodes.Add));

        var greeting = calculator.Methods.Single(method => method.Name == "PrintGreeting");
        var message = greeting.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr);
        message.Operand = RewrittenGreeting;

        var exploration = add.CustomAttributes.Single(attribute =>
            attribute.AttributeType.FullName == "DllExplorerSample.ExplorationAttribute");
        add.CustomAttributes.Remove(exploration);

        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The output path must name a file.");
        Directory.CreateDirectory(outputDirectory);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite);
        assembly.Write(output, new WriterParameters
        {
            WriteSymbols = true,
            SymbolWriterProvider = new EmbeddedPortablePdbWriterProvider(),
            DeterministicMvid = true
        });
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
