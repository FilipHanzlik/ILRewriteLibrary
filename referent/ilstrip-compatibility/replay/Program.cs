// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.Build.Utilities;

namespace StripReplay;

internal static class Program
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["capture", string binlog, string corpus, string project] => Capture(binlog, corpus, project),
                ["exercise", string manifest, string output] => Exercise(manifest, output),
                ["whole", string input, string output] => StripWhole(input, output),
                ["methods", string input, string tokens, string output] => StripMethods(input, tokens, output),
                ["inspect", string input, string output] => Inspect(input, output),
                ["normalize", string input, string output] => Normalize(input, output),
                ["verify-whole", string manifest, string output] => VerifyWholeCandidates(manifest, output),
                ["verify-methods", string input, string tokens, string output] => VerifyMethodCandidate(input, tokens, output),
                _ => throw new ArgumentException("Usage: capture <binlog> <corpus> <project> | exercise <manifest> <new-output-directory> | whole <input.dll> <output.dll> | methods <input.dll> <token-file> <new-intermediate-directory> | inspect <input.dll> <output.json> | normalize <input.dll> <output.dll> | verify-whole <manifest> <candidate-directory> | verify-methods <input.dll> <token-file> <candidate.dll>")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Capture(string binlog, string corpus, string project)
    {
        corpus = Path.GetFullPath(corpus);
        EnsureNewDirectory(corpus);
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!;
        HashSet<string> strippingTasks = [];
        List<ITaskItem> inputs = [];
        List<ITaskItem> outputs = [];
        bool buildSucceeded = false;
        int completedTasks = 0;
        BinaryLogReplayEventSource source = new();
        source.AnyEventRaised += (_, eventArgs) =>
        {
            if (eventArgs is TaskStartedEventArgs started && started.TaskName.EndsWith("MobileILStrip", StringComparison.Ordinal))
                strippingTasks.Add(ContextKey(started.BuildEventContext));
            if (eventArgs is TaskFinishedEventArgs finished && strippingTasks.Contains(ContextKey(finished.BuildEventContext)))
            {
                Require(finished.Succeeded, "The recorded stripping task failed.");
                completedTasks++;
            }
            if (eventArgs is TaskParameterEventArgs parameter && strippingTasks.Contains(ContextKey(parameter.BuildEventContext)))
            {
                string name = parameter.ParameterName ?? parameter.ItemType;
                if (parameter.Kind == TaskParameterMessageKind.TaskInput && name == "Assemblies")
                    inputs.AddRange(parameter.Items.Cast<ITaskItem>());
                if (parameter.Kind == TaskParameterMessageKind.TaskOutput && name == "StrippedAssemblies")
                    outputs.AddRange(parameter.Items.Cast<ITaskItem>());
            }
            if (eventArgs is BuildFinishedEventArgs buildFinished)
                buildSucceeded = buildFinished.Succeeded;
        };
        source.Replay(Path.GetFullPath(binlog));
        Require(buildSucceeded && completedTasks == 1 && inputs.Count > 0, "Expected one successful MobileILStrip invocation in a successful build.");
        Require(inputs.Count == outputs.Count, $"Recorded input/output item counts differ: {inputs.Count}/{outputs.Count}.");

        Dictionary<string, ITaskItem> outputByOriginal = outputs.ToDictionary(
            item => Path.GetFullPath(item.GetMetadata("UntrimmedAssemblyFilePath"), projectDirectory),
            StringComparer.Ordinal);
        List<Fixture> fixtures = [];
        List<object> recordedItems = [];
        for (int index = 0; index < inputs.Count; index++)
        {
            ITaskItem input = inputs[index];
            string originalPath = Path.GetFullPath(input.ItemSpec, projectDirectory);
            Require(outputByOriginal.TryGetValue(originalPath, out ITaskItem? output), $"No recorded output for {originalPath}.");
            string goldenPath = Path.GetFullPath(output!.ItemSpec, projectDirectory);
            string relativeName = $"{index:D3}/{Path.GetFileName(originalPath)}";
            string inputName = $"inputs/{relativeName}";
            string outputName = $"expected/whole/{relativeName}";
            string deterministicName = $"expected/whole-deterministic/{relativeName}";
            CopyNew(originalPath, Path.Combine(corpus, inputName));
            CopyNew(goldenPath, Path.Combine(corpus, outputName));
            WriteNew(Path.Combine(corpus, deterministicName), BinaryInspection.NormalizedBytes(goldenPath));
            string pdbPath = Path.ChangeExtension(originalPath, ".pdb");
            if (File.Exists(pdbPath))
                CopyNew(pdbPath, Path.ChangeExtension(Path.Combine(corpus, inputName), ".pdb"));
            BinarySnapshot before = BinaryInspection.Read(originalPath);
            BinarySnapshot after = BinaryInspection.Read(goldenPath);
            List<string> observations = BinaryInspection.VerifyWhole(originalPath, goldenPath);
            Save(Path.Combine(corpus, inputName + ".json"), before);
            Save(Path.Combine(corpus, outputName + ".json"), after);
            fixtures.Add(new(inputName, outputName, deterministicName, before.Sha256, after.Sha256, after.NormalizedSha256, before.Mvid, before.Length, after.Length, observations));
            recordedItems.Add(new
            {
                input = originalPath,
                output = goldenPath,
                inputMetadata = Metadata(input),
                outputMetadata = Metadata(output)
            });
        }

        Save(Path.Combine(corpus, "manifest.json"), new Manifest(1, "whole", fixtures));
        Save(Path.Combine(corpus, "recorded-task.json"), new
        {
            binlog = Path.GetFullPath(binlog),
            binlogSha256 = HashFile(binlog),
            task = "MobileILStrip",
            trimIndividualMethods = false,
            recordedParallelism = "default",
            oracle = new[] { typeof(ILStrip).Assembly, typeof(AssemblyStripper.AssemblyStripper).Assembly }.Select(assembly => new
            {
                path = assembly.Location,
                identity = assembly.FullName,
                informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                sha256 = HashFile(assembly.Location)
            }),
            items = recordedItems
        });
        Console.WriteLine($"Captured and structurally verified {fixtures.Count} assembly pairs; {fixtures.Sum(item => item.InputLength)} input bytes -> {fixtures.Sum(item => item.OutputLength)} output bytes.");
        return 0;
    }

    private static int Exercise(string manifestPath, string outputDirectory)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        string corpus = Path.GetDirectoryName(manifestPath)!;
        EnsureNewDirectory(outputDirectory);
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), s_jsonOptions)
            ?? throw new InvalidDataException("The manifest is null.");
        Require(manifest.SchemaVersion == 1 && manifest.Mode == "whole", "Unsupported fixture manifest.");
        foreach (Fixture fixture in manifest.Fixtures)
        {
            Require(HashFile(Path.Combine(corpus, fixture.Input)) == fixture.InputSha256, $"Modified fixture input: {fixture.Input}");
            Require(HashFile(Path.Combine(corpus, fixture.Expected)) == fixture.OutputSha256, $"Modified fixture golden: {fixture.Expected}");
            Require(HashFile(Path.Combine(corpus, fixture.DeterministicExpected)) == fixture.NormalizedOutputSha256, $"Modified deterministic golden: {fixture.DeterministicExpected}");
        }

        List<object> wholeResults = [];
        RunWholeSet(manifest, corpus, Path.Combine(outputDirectory, "whole-1"));
        Thread.Sleep(1200);
        RunWholeSet(manifest, corpus, Path.Combine(outputDirectory, "whole-2"));
        foreach (Fixture fixture in manifest.Fixtures)
        {
            string first = Path.Combine(outputDirectory, "whole-1", fixture.Input["inputs/".Length..]);
            string second = Path.Combine(outputDirectory, "whole-2", fixture.Input["inputs/".Length..]);
            BinarySnapshot firstSnapshot = BinaryInspection.Read(first);
            BinarySnapshot secondSnapshot = BinaryInspection.Read(second);
            Require(firstSnapshot.NormalizedSha256 == fixture.NormalizedOutputSha256, $"Standalone whole stripping differs from the iOS golden: {fixture.Input}");
            Require(firstSnapshot.NormalizedSha256 == secondSnapshot.NormalizedSha256, $"Whole output differs beyond permitted timestamps: {fixture.Input}");
            BinaryInspection.VerifyWhole(Path.Combine(corpus, fixture.Input), first);
            List<int> differences = Differences(File.ReadAllBytes(first), File.ReadAllBytes(second));
            wholeResults.Add(new
            {
                fixture.Input,
                byteIdentical = differences.Count == 0,
                normalizedIdentical = true,
                changedOffsets = differences,
                firstSnapshot.NormalizationRanges,
                firstSha256 = firstSnapshot.Sha256,
                secondSha256 = secondSnapshot.Sha256
            });
        }

        Fixture application = manifest.Fixtures.Single(item => Path.GetFileName(item.Input) == "MauiStripSample.dll");
        string appInput = Path.Combine(corpus, application.Input);
        List<MethodSnapshot> methods = BinaryInspection.Read(appInput).Methods.Where(method => method.Rva != 0).ToList();
        Require(methods.Any(method => method.HeaderKind == "tiny") && methods.Any(method => method.HeaderKind == "fat") && methods.Any(method => method.ExceptionRegions > 0), "The app fixture does not cover tiny, fat and exception-bearing bodies.");
        int[] mixed =
        [
            methods.First(method => method.HeaderKind == "tiny").Token,
            methods.First(method => method.HeaderKind == "fat").Token,
            methods.First(method => method.ExceptionRegions > 0).Token
        ];
        List<object> selectiveResults = [];
        selectiveResults.Add(SelectiveCase("empty", appInput, [], corpus, outputDirectory));
        selectiveResults.Add(SelectiveCase("mixed", appInput, mixed.Distinct().ToArray(), corpus, outputDirectory));
        selectiveResults.Add(SelectiveCase("all", appInput, methods.Select(method => method.Token).ToArray(), corpus, outputDirectory));
        string sharedInput = Path.Combine(corpus, application.Expected);
        int[] sharedTokens = BinaryInspection.Read(sharedInput).Methods.Where(method => method.Rva != 0).Select(method => method.Token).ToArray();
        Require(sharedTokens.Length > 1, "Expected multiple methods sharing the whole-stripped app stub.");
        selectiveResults.Add(SelectiveCase("shared-partial", sharedInput, [sharedTokens[0]], corpus, outputDirectory));
        selectiveResults.Add(SelectiveCase("shared-all", sharedInput, sharedTokens, corpus, outputDirectory));
        selectiveResults.Add(ErrorCase("wrong-mvid", appInput, [], "00000000-0000-0000-0000-000000000000", corpus, outputDirectory));
        selectiveResults.Add(ErrorCase("unknown-token", appInput, ["06000000"], null, corpus, outputDirectory));
        MethodSnapshot uniqueMethod = methods.First(method => methods.Count(other => other.Rva == method.Rva) == 1);
        string duplicate = uniqueMethod.Token.ToString("x8");
        selectiveResults.Add(ErrorCase("duplicate-token", appInput, [duplicate, duplicate], null, corpus, outputDirectory));
        selectiveResults.Add(ErrorCase("malformed-token", appInput, ["not-a-hex-token"], null, corpus, outputDirectory));

        string inPlace = Path.Combine(outputDirectory, "in-place", "MauiStripSample.dll");
        CopyNew(appInput, inPlace);
        Require(RunTask([Item(inPlace, inPlace)]).Succeeded, "In-place whole stripping failed.");
        Require(BinaryInspection.Read(inPlace).NormalizedSha256 == application.NormalizedOutputSha256, "In-place output differs from separate-path stripping.");
        foreach (Fixture fixture in manifest.Fixtures)
            Require(HashFile(Path.Combine(corpus, fixture.Input)) == fixture.InputSha256, $"An oracle operation modified fixture input {fixture.Input}.");
        Save(Path.Combine(outputDirectory, "report.json"), new
        {
            manifestSha256 = HashFile(manifestPath),
            wholeResults,
            selectiveResults,
            inPlaceEquivalent = true,
            inputsUnmodified = true,
            runtimeExecution = "not run"
        });
        Console.WriteLine($"Verified {manifest.Fixtures.Count} whole pairs against iOS goldens, two independent whole runs, 5 selective transformations, 4 selective failure cases, and in-place equivalence.");
        return 0;
    }

    private static object SelectiveCase(string name, string input, int[] tokens, string corpus, string outputDirectory)
    {
        string expectedRoot = Path.Combine(corpus, "expected/selective", name);
        bool recording = !Directory.Exists(expectedRoot);
        if (recording)
            EnsureNewDirectory(expectedRoot);
        string tokenFile = Path.Combine(outputDirectory, "tokens", name + ".txt");
        WriteTokens(tokenFile, input, BinaryInspection.Read(input).Mvid, tokens.Select(token => token.ToString("x8")));
        Outcome first = RunMethods(input, tokenFile, Path.Combine(outputDirectory, "selective-1", name));
        Outcome second = RunMethods(input, tokenFile, Path.Combine(outputDirectory, "selective-2", name));
        Require(first.Succeeded && second.Succeeded, $"Selective case {name} failed: {string.Join("; ", first.Errors.Concat(second.Errors))}");
        string firstOutput = first.Outputs.Single();
        string secondOutput = second.Outputs.Single();
        byte[] independentExpected = BinaryInspection.ExpectedSelective(input, tokens);
        Require(File.ReadAllBytes(firstOutput).SequenceEqual(independentExpected), $"Selective byte transformation mismatch: {name}");
        Require(File.ReadAllBytes(firstOutput).SequenceEqual(File.ReadAllBytes(secondOutput)), $"Selective output is nondeterministic: {name}");
        if (tokens.Length > 0 && recording)
            CopyNew(firstOutput, Path.Combine(expectedRoot, Path.GetFileName(input)));
        if (tokens.Length > 0 && !recording)
            Require(File.ReadAllBytes(Path.Combine(expectedRoot, Path.GetFileName(input))).SequenceEqual(independentExpected), $"Modified selective golden: {name}");
        Require(tokens.Length != 0 || firstOutput == input, "An empty selection should return the original assembly.");
        object result = new
        {
            name,
            input = Path.GetRelativePath(corpus, input),
            tokens = tokens.Select(token => token.ToString("x8")),
            mvid = BinaryInspection.Read(input).Mvid,
            output = tokens.Length == 0 ? null : $"expected/selective/{name}/{Path.GetFileName(input)}",
            outputSha256 = HashBytes(independentExpected),
            inputLength = new FileInfo(input).Length,
            outputLength = independentExpected.Length,
            selectedMethodCount = tokens.Length,
            changedByteCount = Differences(File.ReadAllBytes(input), independentExpected).Count,
            byteIdenticalAcrossRuns = true,
            producesFile = tokens.Length > 0
        };
        if (recording)
        {
            WriteTokens(Path.Combine(expectedRoot, "methods.txt"), Path.GetRelativePath(corpus, input), BinaryInspection.Read(input).Mvid, tokens.Select(token => token.ToString("x8")), absolutePath: false);
            Save(Path.Combine(expectedRoot, "request.json"), result);
        }
        return result;
    }

    private static object ErrorCase(string name, string input, string[] lines, string? mvid, string corpus, string outputDirectory)
    {
        string expectedRoot = Path.Combine(corpus, "expected/selective-errors", name);
        bool recording = !Directory.Exists(expectedRoot);
        if (recording)
            EnsureNewDirectory(expectedRoot);
        string tokenFile = Path.Combine(outputDirectory, "tokens", name + ".txt");
        WriteTokens(tokenFile, input, mvid ?? BinaryInspection.Read(input).Mvid, lines);
        Outcome? outcome = null;
        string? exceptionType = null;
        try
        {
            outcome = RunMethods(input, tokenFile, Path.Combine(outputDirectory, "selective-errors", name));
        }
        catch (AggregateException exception) when (name == "malformed-token" && exception.InnerException is FormatException)
        {
            exceptionType = $"{nameof(AggregateException)}({nameof(FormatException)})";
        }
        Require(outcome is { Succeeded: false } || exceptionType is not null, $"Expected selective failure: {name}");
        string output = Path.Combine(outputDirectory, "selective-errors", name, "stripped", Path.GetFileName(input));
        bool producesFile = File.Exists(output);
        if (producesFile)
        {
            Require(File.ReadAllBytes(input).SequenceEqual(File.ReadAllBytes(output)), $"Unexpected modified failure output: {name}");
            if (recording)
                CopyNew(output, Path.Combine(expectedRoot, Path.GetFileName(input)));
            else
                Require(File.ReadAllBytes(Path.Combine(expectedRoot, Path.GetFileName(input))).SequenceEqual(File.ReadAllBytes(output)), $"Modified failure golden: {name}");
        }
        object result = new
        {
            name,
            input = Path.GetRelativePath(corpus, input),
            mvid = mvid ?? BinaryInspection.Read(input).Mvid,
            tokens = lines,
            taskSucceeded = false,
            cliExitCode = 1,
            exceptionType,
            errors = outcome?.Errors.Select(error => error.Replace(corpus, "<corpus>", StringComparison.Ordinal)),
            producesFile,
            outputSha256 = producesFile ? HashFile(output) : null
        };
        if (recording)
        {
            WriteTokens(Path.Combine(expectedRoot, "methods.txt"), Path.GetRelativePath(corpus, input), mvid ?? BinaryInspection.Read(input).Mvid, lines, absolutePath: false);
            Save(Path.Combine(expectedRoot, "request.json"), result);
        }
        return result;
    }

    private static void RunWholeSet(Manifest manifest, string corpus, string directory)
    {
        EnsureNewDirectory(directory);
        ITaskItem[] items = manifest.Fixtures.Select(fixture =>
        {
            string output = Path.Combine(directory, fixture.Input["inputs/".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            return Item(Path.Combine(corpus, fixture.Input), output);
        }).ToArray();
        Outcome outcome = RunTask(items);
        Require(outcome.Succeeded && outcome.Outputs.Count == items.Length, $"Whole set failed: {string.Join("; ", outcome.Errors)}");
    }

    private static int StripWhole(string input, string output)
    {
        input = Path.GetFullPath(input);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        return RunTask([Item(input, output)]).Succeeded ? 0 : 1;
    }

    private static int StripMethods(string input, string tokenFile, string output)
    {
        EnsureNewDirectory(Path.GetFullPath(output));
        return RunMethods(Path.GetFullPath(input), Path.GetFullPath(tokenFile), Path.GetFullPath(output)).Succeeded ? 0 : 1;
    }

    private static int Inspect(string input, string output)
    {
        Save(Path.GetFullPath(output), BinaryInspection.Read(Path.GetFullPath(input)));
        return 0;
    }

    private static int Normalize(string input, string output)
    {
        WriteNew(Path.GetFullPath(output), BinaryInspection.NormalizedBytes(Path.GetFullPath(input)));
        return 0;
    }

    private static int VerifyWholeCandidates(string manifestPath, string outputDirectory)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        string corpus = Path.GetDirectoryName(manifestPath)!;
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), s_jsonOptions)
            ?? throw new InvalidDataException("The manifest is null.");
        foreach (Fixture fixture in manifest.Fixtures)
        {
            Require(HashFile(Path.Combine(corpus, fixture.Input)) == fixture.InputSha256, $"Modified fixture input: {fixture.Input}");
            string candidate = Path.Combine(Path.GetFullPath(outputDirectory), fixture.Input["inputs/".Length..]);
            Require(BinaryInspection.Read(candidate).NormalizedSha256 == fixture.NormalizedOutputSha256, $"Candidate does not match the legacy whole golden: {fixture.Input}");
            BinaryInspection.VerifyWhole(Path.Combine(corpus, fixture.Input), candidate);
        }
        Console.WriteLine($"Verified {manifest.Fixtures.Count} whole-assembly candidate outputs.");
        return 0;
    }

    private static int VerifyMethodCandidate(string input, string tokenFile, string output)
    {
        string[] lines = File.ReadAllLines(tokenFile);
        Require(lines.Length >= 2 && string.Equals(lines[1], BinaryInspection.Read(input).Mvid, StringComparison.OrdinalIgnoreCase), "Token-file MVID does not match the input.");
        Require(Path.GetFullPath(lines[0]) == Path.GetFullPath(input), "Token-file assembly path does not match the input.");
        int[] tokens = lines.Skip(2).Select(line => Convert.ToInt32(line, 16)).ToArray();
        Require(File.ReadAllBytes(output).SequenceEqual(BinaryInspection.ExpectedSelective(input, tokens)), "Candidate does not match the selective byte transformation.");
        Console.WriteLine($"Verified selective output for {tokens.Length} requested methods.");
        return 0;
    }

    private static Outcome RunMethods(string input, string tokens, string intermediate)
    {
        TaskItem item = new(Path.GetFullPath(input));
        item.SetMetadata("MethodTokenFile", Path.GetFullPath(tokens));
        return RunTask([item], trimMethods: true, intermediate);
    }

    private static Outcome RunTask(ITaskItem[] items, bool trimMethods = false, string? intermediate = null)
    {
        LoggingEngine engine = new();
        ILStrip task = new()
        {
            Assemblies = items,
            BuildEngine = engine,
            DisableParallelStripping = true,
            TrimIndividualMethods = trimMethods,
            IntermediateOutputPath = intermediate
        };
        bool succeeded = task.Execute();
        return new(succeeded, engine.Errors, task.UpdatedAssemblies?.Select(item => item.ItemSpec).ToList() ?? []);
    }

    private static TaskItem Item(string input, string output)
    {
        TaskItem item = new(Path.GetFullPath(input));
        item.SetMetadata("OutputPath", Path.GetFullPath(output));
        return item;
    }

    private static Dictionary<string, string> Metadata(ITaskItem item)
    {
        IDictionary metadata = item.CloneCustomMetadata();
        return metadata.Keys.Cast<string>().ToDictionary(key => key, key => (string)metadata[key]!);
    }

    private static string ContextKey(BuildEventContext? context)
        => context is null ? "none" : $"{context.NodeId}/{context.ProjectContextId}/{context.TaskId}";

    private static void WriteTokens(string path, string input, string mvid, IEnumerable<string> tokens, bool absolutePath = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, new[] { absolutePath ? Path.GetFullPath(input) : input, mvid }.Concat(tokens));
    }

    internal static void Save(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, s_jsonOptions) + Environment.NewLine);
    }

    internal static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));

    private static List<int> Differences(byte[] left, byte[] right)
    {
        Require(left.Length == right.Length, "Compared files have different lengths.");
        return Enumerable.Range(0, left.Length).Where(index => left[index] != right[index]).ToList();
    }

    private static void EnsureNewDirectory(string path)
    {
        Require(!Directory.Exists(path) && !File.Exists(path), $"Refusing to overwrite existing fixture directory: {path}");
        Directory.CreateDirectory(path);
    }

    private static void CopyNew(string input, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Copy(input, output, overwrite: false);
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}

internal sealed record Fixture(string Input, string Expected, string DeterministicExpected, string InputSha256, string OutputSha256, string NormalizedOutputSha256, string Mvid, long InputLength, long OutputLength, List<string> CompatibilityObservations);
internal sealed record Manifest(int SchemaVersion, string Mode, List<Fixture> Fixtures);
internal sealed record Outcome(bool Succeeded, List<string> Errors, List<string> Outputs);

internal sealed class LoggingEngine : IBuildEngine
{
    public List<string> Errors { get; } = [];
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => string.Empty;
    public void LogErrorEvent(BuildErrorEventArgs eventArgs)
    {
        string message = eventArgs.Message ?? "(error without message)";
        Errors.Add(message);
        Console.Error.WriteLine(message);
    }
    public void LogWarningEvent(BuildWarningEventArgs eventArgs) => Console.Error.WriteLine(eventArgs.Message);
    public void LogMessageEvent(BuildMessageEventArgs eventArgs) { }
    public void LogCustomEvent(CustomBuildEventArgs eventArgs) => Console.Error.WriteLine(eventArgs.Message);
    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
        => throw new NotSupportedException("The standalone stripping oracle does not build projects.");
}
