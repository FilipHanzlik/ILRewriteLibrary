using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace CecilProfileRunner;

public record KeepAlivesStats(
    int TotalTypes,
    int MethodsModified,
    int KeepAliveCallsInjected,
    int TotalInstructionsInserted,
    int BranchesRetargeted,
    long OutputBytes,
    bool SymbolsProcessed = false,
    long OutputPdbBytes = 0
);

public static class KeepAlivesWorkload
{
    public static KeepAlivesStats Execute(string inputPath, string outputPath)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string pdbPath = Path.ChangeExtension(inputPath, ".pdb");
        bool hasSymbols = File.Exists(pdbPath);

        var readerParams = AssemblyResolverHelper.CreateReaderParameters(inputPath, readSymbols: hasSymbols);

        using var assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParams);

        // Resolve / import System.GC.KeepAlive(object)
        var keepAliveMethod = typeof(GC).GetMethod(nameof(GC.KeepAlive), new[] { typeof(object) })
            ?? throw new InvalidOperationException("Could not find System.GC.KeepAlive(object)");
        var keepAliveRef = assembly.MainModule.ImportReference(keepAliveMethod);

        int totalTypes = 0;
        int methodsModified = 0;
        int callsInjected = 0;
        int instructionsInserted = 0;
        int branchesRetargeted = 0;

        foreach (var type in assembly.MainModule.GetAllTypes())
        {
            totalTypes++;

            if (!type.HasMethods)
                continue;

            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.Body.Instructions.Count == 0)
                    continue;

                // Identify eligible reference-type parameters
                var eligibleParams = new List<ParameterDefinition>();
                foreach (var param in method.Parameters)
                {
                    var paramType = param.ParameterType;
                    if (!paramType.IsValueType && !paramType.IsPointer && !paramType.IsByReference)
                    {
                        eligibleParams.Add(param);
                    }
                }

                if (eligibleParams.Count == 0)
                    continue;

                // Find all return instructions
                var retInstructions = new List<Instruction>();
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Ret)
                    {
                        retInstructions.Add(ins);
                    }
                }

                if (retInstructions.Count == 0)
                    continue;

                var ilProcessor = method.Body.GetILProcessor();
                bool modifiedThisMethod = false;

                foreach (var ret in retInstructions)
                {
                    var injectionBlock = new List<Instruction>();
                    foreach (var param in eligibleParams)
                    {
                        injectionBlock.Add(Instruction.Create(OpCodes.Ldarg, param));
                        injectionBlock.Add(Instruction.Create(OpCodes.Call, keepAliveRef));
                        callsInjected++;
                        instructionsInserted += 2;
                    }

                    var firstInjected = injectionBlock[0];

                    // Insert the keep-alive sequence before ret
                    foreach (var injectedIns in injectionBlock)
                    {
                        ilProcessor.InsertBefore(ret, injectedIns);
                    }

                    // Retarget branch instructions that previously jumped directly to ret
                    foreach (var ins in method.Body.Instructions)
                    {
                        if (ins.Operand == ret)
                        {
                            ins.Operand = firstInjected;
                            branchesRetargeted++;
                        }
                        else if (ins.Operand is Instruction[] switchTargets)
                        {
                            for (int i = 0; i < switchTargets.Length; i++)
                            {
                                if (switchTargets[i] == ret)
                                {
                                    switchTargets[i] = firstInjected;
                                    branchesRetargeted++;
                                }
                            }
                        }
                    }

                    // Retarget exception handler start points if pointing to ret
                    if (method.Body.HasExceptionHandlers)
                    {
                        foreach (var handler in method.Body.ExceptionHandlers)
                        {
                            if (handler.TryStart == ret)
                                handler.TryStart = firstInjected;
                            if (handler.HandlerStart == ret)
                                handler.HandlerStart = firstInjected;
                            if (handler.FilterStart == ret)
                                handler.FilterStart = firstInjected;
                        }
                    }

                    modifiedThisMethod = true;
                }

                if (modifiedThisMethod)
                {
                    methodsModified++;
                }
            }
        }

        var writeParams = new WriterParameters
        {
            DeterministicMvid = true,
            WriteSymbols = hasSymbols
        };

        assembly.Write(outputPath, writeParams);

        long outputBytes = new FileInfo(outputPath).Length;
        string outputPdbPath = Path.ChangeExtension(outputPath, ".pdb");
        long outputPdbBytes = hasSymbols && File.Exists(outputPdbPath) ? new FileInfo(outputPdbPath).Length : 0;

        return new KeepAlivesStats(
            totalTypes,
            methodsModified,
            callsInjected,
            instructionsInserted,
            branchesRetargeted,
            outputBytes,
            hasSymbols,
            outputPdbBytes
        );
    }
}
