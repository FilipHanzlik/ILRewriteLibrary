using System;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using SrmMetadataBuilderRewrite;

namespace SrmMetadataBuilderStrip;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: SrmMetadataBuilderStrip <input-path> <output-path>");
            return 1;
        }

        string inputPath = args[0];
        string outputPath = args[1];

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
    /// Whole-assembly post-AOT IL stripping implementation driven by SrmMetadataBuilderRewrite.
    /// Replaces all method bodies with a single shared ret stub and marks all methods NoInlining.
    /// Uses MetadataBuilder to reconstruct tables and heaps.
    /// </summary>
    public static void StripAssembly(string inputPath, string outputPath)
    {
        using var context = AssemblyRewriteContext.Load(inputPath);
        var reader = context.Reader;
        var editor = context.Editor;

        // Build a single 1-byte 'ret' body using standard SRM InstructionEncoder
        var codeBuilder = new BlobBuilder();
        var il = new InstructionEncoder(codeBuilder);
        il.OpCode(ILOpCode.Ret);
        EditedMethodBodyHandle sharedRetBody = editor.CreateMethodBody(il, maxStack: 0);

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var methodEditor = editor.Edit(methodHandle)
                                     .SetImplAttributes(method.ImplAttributes | MethodImplAttributes.NoInlining);

            if (method.HasBody)
            {
                methodEditor.SetBody(sharedRetBody);
            }
        }

        context.Writer.Write(outputPath);
    }
}
