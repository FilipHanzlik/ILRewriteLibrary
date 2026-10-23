using System;
using System.IO;
using System.Reflection;
using SrmRewrite;

namespace SrmIlStrip;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: SrmIlStrip <input-path> <output-path>");
            return 1;
        }

        string inputPath = args[0];
        string outputPath = args[1];

        // Handle possible -i / -o flags if supplied
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-i" && i + 1 < args.Length)
                inputPath = args[++i];
            else if (args[i] == "-o" && i + 1 < args.Length)
                outputPath = args[++i];
        }

        try
        {
            StripAssembly(inputPath, outputPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>
    /// Whole-assembly post-AOT IL stripping implementation driven by SrmRewrite triad.
    /// Replaces all method bodies with a single shared ret stub and marks them NoInlining.
    /// Preserves MVID, metadata heaps, tables, resources, and debug CodeView entries.
    /// </summary>
    public static void StripAssembly(string inputPath, string outputPath)
    {
        using var context = AssemblyRewriteContext.Load(inputPath);
        var reader = context.Reader;
        var editor = context.Editor;

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var methodEditor = editor.Edit(methodHandle)
                                     .AddImplAttributes(MethodImplAttributes.NoInlining);

            if (reader.HasBody(methodHandle))
            {
                methodEditor.ReplaceBodyWithSharedRet();
            }
        }

        context.Writer.Write(outputPath);
    }
}
