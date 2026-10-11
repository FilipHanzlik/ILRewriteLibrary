using System;
using System.IO;
using Mono.Cecil;

namespace CecilProfileRunner;

public static class AssemblyResolverHelper
{
    public static DefaultAssemblyResolver CreateResolver(string targetAssemblyPath)
    {
        var resolver = new DefaultAssemblyResolver();

        string? targetDir = Path.GetDirectoryName(targetAssemblyPath);
        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
        {
            resolver.AddSearchDirectory(targetDir);

            // If the target is inside a corpus folder like corpus/inputs/015/...,
            // add all sister input directories (inputs/000 .. inputs/066)
            string parentDir = Path.GetFullPath(Path.Combine(targetDir, ".."));
            if (Directory.Exists(parentDir))
            {
                foreach (var dir in Directory.GetDirectories(parentDir))
                {
                    resolver.AddSearchDirectory(dir);
                }
            }
        }

        // Also search known corpus roots so dependencies like Microsoft.Maui.dll are always resolved
        string[] knownCorpusRoots =
        {
            Path.Combine("referent", "ilstrip-compatibility", "corpus", "inputs"),
            Path.Combine("..", "referent", "ilstrip-compatibility", "corpus", "inputs"),
            Path.Combine("..", "..", "referent", "ilstrip-compatibility", "corpus", "inputs"),
            Path.Combine("..", "..", "..", "referent", "ilstrip-compatibility", "corpus", "inputs"),
            Path.Combine("..", "..", "..", "..", "referent", "ilstrip-compatibility", "corpus", "inputs"),
        };

        foreach (var corpusRoot in knownCorpusRoots)
        {
            string fullCorpus = Path.GetFullPath(corpusRoot);
            if (Directory.Exists(fullCorpus))
            {
                foreach (var dir in Directory.GetDirectories(fullCorpus))
                {
                    resolver.AddSearchDirectory(dir);
                }
                break;
            }
        }

        // Add current application base directory and framework directory
        resolver.AddSearchDirectory(AppContext.BaseDirectory);

        return resolver;
    }

    public static ReaderParameters CreateReaderParameters(string targetAssemblyPath, bool readSymbols = false)
    {
        bool hasPdb = readSymbols && File.Exists(Path.ChangeExtension(targetAssemblyPath, ".pdb"));
        return new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            AssemblyResolver = CreateResolver(targetAssemblyPath),
            ReadSymbols = hasPdb
        };
    }
}
