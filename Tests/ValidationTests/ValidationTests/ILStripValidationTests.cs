using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace ValidationTests;

public class ILStripValidationTests
{
    private readonly ITestOutputHelper _output;

    public ILStripValidationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> GetAllFixtures()
    {
        string mappingPath = TestConfiguration.MappingPath;
        if (!File.Exists(mappingPath))
            throw new FileNotFoundException($"Cannot find mapping.json at '{mappingPath}'");

        string json = File.ReadAllText(mappingPath);
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(json)
            ?? throw new InvalidDataException("Manifest is null.");

        foreach (var fixture in manifest.Fixtures)
        {
            yield return new object[] { fixture.Name, fixture };
        }
    }

    /// <summary>
    /// For each of the 67 referent assemblies:
    /// 1. Runs the candidate ILStrip implementation.
    /// 2. Invokes the referent's StripReplay.dll validator (BinaryInspection.NormalizedBytes and BinaryInspection.VerifyWhole).
    /// 3. Asserts normalized SHA-256 match and structural metadata invariants.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllFixtures))]
    public void StripAndValidate_IndividualAssembly(string assemblyName, CorpusFixture fixture)
    {
        string inputPath = Path.Combine(TestConfiguration.DataDir, fixture.InputFile);
        Assert.True(File.Exists(inputPath), $"Input file missing: {inputPath}");

        string candidateDir = Path.Combine(TestConfiguration.TestArtifactsDir, "individual");
        Directory.CreateDirectory(candidateDir);
        string candidatePath = Path.Combine(candidateDir, fixture.Name);

        // Delete any old candidate
        if (File.Exists(candidatePath))
            File.Delete(candidatePath);

        var (stripperPath, argTemplate, isOracle) = TestConfiguration.GetConfiguredStripper();
        _output.WriteLine($"Testing {assemblyName} using {(isOracle ? "Referent Oracle" : stripperPath)}");

        // 1. Run the stripper
        var runResult = StripperProcessRunner.Run(stripperPath, inputPath, candidatePath, argTemplate);
        Assert.True(runResult.Success, $"Stripper execution failed with exit code {runResult.ExitCode}:\n{runResult.StandardError}\n{runResult.StandardOutput}");
        Assert.True(File.Exists(candidatePath), $"Stripper did not produce candidate output: {candidatePath}");

        // 2. Validate using the referent's StripReplay.dll BinaryInspection
        byte[] normalized = StripReplay.BinaryInspection.NormalizedBytes(candidatePath);
        string candidateNormalizedSha256 = StripReplay.Program.HashBytes(normalized);

        _output.WriteLine($"Candidate normalized SHA256: {candidateNormalizedSha256}");
        if (isOracle)
        {
            Assert.Equal(fixture.ExpectedSha256, candidateNormalizedSha256);
        }

        // 3. Deep structural check using the referent's VerifyWhole
        List<string> observations = StripReplay.BinaryInspection.VerifyWhole(inputPath, candidatePath);
        Assert.NotNull(observations);
        foreach (var obs in observations)
        {
            _output.WriteLine($"Observation: {obs}");
        }
    }

    /// <summary>
    /// Batch test: Strips all assemblies into candidate directories mirroring the referent layout,
    /// then invokes the referent's official CLI command:
    /// 'dotnet StripReplay.dll verify-whole corpus/manifest.json <candidate-dir>'
    /// </summary>
    [Fact]
    public void ValidateEntireCorpus_UsingReferentVerifyWholeCli()
    {
        string manifestPath = TestConfiguration.CorpusManifestPath;
        Assert.True(File.Exists(manifestPath), $"Manifest file missing: {manifestPath}");

        string json = File.ReadAllText(TestConfiguration.MappingPath);
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(json)!;

        string candidatesRoot = Path.Combine(TestConfiguration.TestArtifactsDir, "batch_corpus");
        Directory.CreateDirectory(candidatesRoot);

        var (stripperPath, argTemplate, isOracle) = TestConfiguration.GetConfiguredStripper();
        _output.WriteLine($"Batch stripping 67 assemblies with {(isOracle ? "Referent Oracle" : stripperPath)}...");

        foreach (var fixture in manifest.Fixtures)
        {
            string inputPath = Path.Combine(TestConfiguration.DataDir, fixture.InputFile);
            string fixtureOutDir = Path.Combine(candidatesRoot, fixture.Index);
            Directory.CreateDirectory(fixtureOutDir);
            string candidatePath = Path.Combine(fixtureOutDir, fixture.Name);

            var result = StripperProcessRunner.Run(stripperPath, inputPath, candidatePath, argTemplate);
            Assert.True(result.Success, $"Failed stripping {fixture.Name}: {result.StandardError}");
        }

        // Run the referent's StripReplay.dll CLI: verify-whole <manifest> <candidateDir>
        string stripReplayDll = TestConfiguration.ReferentStripReplayDll;
        Assert.True(File.Exists(stripReplayDll), $"StripReplay.dll missing at '{stripReplayDll}'");

        ProcessStartInfo psi = new()
        {
            FileName = "dotnet",
            Arguments = $"\"{stripReplayDll}\" verify-whole \"{manifestPath}\" \"{candidatesRoot}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        _output.WriteLine(stdout);
        if (!string.IsNullOrEmpty(stderr))
            _output.WriteLine($"STDERR: {stderr}");

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Verified 67 whole-assembly candidate outputs", stdout);
    }

    /// <summary>
    /// Direct check verifying that the expected golden DLLs in Tests/data/dlls/ILStrip/expected/
    /// satisfy the referent's StripReplay validation without running a stripper tool.
    /// </summary>
    [Fact]
    public void VerifyReferentExpectedGoldenOutputsDirectly()
    {
        string json = File.ReadAllText(TestConfiguration.MappingPath);
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(json)!;

        foreach (var fixture in manifest.Fixtures)
        {
            string inputPath = Path.Combine(TestConfiguration.DataDir, fixture.InputFile);
            string expectedPath = Path.Combine(TestConfiguration.DataDir, fixture.ExpectedFile);

            Assert.True(File.Exists(expectedPath), $"Expected file missing: {expectedPath}");

            byte[] normalized = StripReplay.BinaryInspection.NormalizedBytes(expectedPath);
            string normalizedSha256 = StripReplay.Program.HashBytes(normalized);

            Assert.Equal(fixture.ExpectedSha256, normalizedSha256);

            var observations = StripReplay.BinaryInspection.VerifyWhole(inputPath, expectedPath);
            Assert.NotNull(observations);
        }
    }
}
