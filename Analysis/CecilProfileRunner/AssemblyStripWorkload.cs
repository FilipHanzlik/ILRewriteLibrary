using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace CecilProfileRunner;

public record AssemblyStripStats(
    int TotalTypes,
    int MethodsStripped,
    long InputBytes,
    long OutputBytes,
    double ReductionPercent
);

public static class AssemblyStripWorkload
{
    public static AssemblyStripStats Execute(string inputPath, string outputPath)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var readerParams = AssemblyResolverHelper.CreateReaderParameters(inputPath);

        using var assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParams);

        int totalTypes = 0;
        int methodsStripped = 0;

        foreach (var type in assembly.MainModule.GetAllTypes())
        {
            totalTypes++;

            if (!type.HasMethods)
                continue;

            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                // Replace body with a minimal stub containing only `ret`
                var newBody = new MethodBody(method);
                var il = newBody.GetILProcessor();
                il.Emit(OpCodes.Ret);

                method.Body = newBody;
                method.ImplAttributes |= MethodImplAttributes.NoInlining;
                methodsStripped++;
            }
        }

        var writeParams = new WriterParameters
        {
            DeterministicMvid = true
        };

        assembly.Write(outputPath, writeParams);

        long inputBytes = new FileInfo(inputPath).Length;
        long outputBytes = new FileInfo(outputPath).Length;
        double reduction = inputBytes > 0 ? (1.0 - (double)outputBytes / inputBytes) * 100.0 : 0.0;

        return new AssemblyStripStats(
            totalTypes,
            methodsStripped,
            inputBytes,
            outputBytes,
            reduction
        );
    }
}
