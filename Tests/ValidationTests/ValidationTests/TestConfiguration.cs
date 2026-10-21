namespace ValidationTests;

public static class TestConfiguration
{
    private static readonly Lazy<string> s_repoRoot = new(FindRepoRoot);

    public static string RepoRoot => s_repoRoot.Value;

    public static string DataDir => Path.Combine(RepoRoot, "Tests/data/dlls/ILStrip");

    public static string MappingPath => Path.Combine(DataDir, "mapping.json");

    public static string CorpusManifestPath => Path.Combine(RepoRoot, "referent/ilstrip-compatibility/corpus/manifest.json");

    public static string ReferentStripReplayDll => Path.Combine(RepoRoot, "referent/ilstrip-compatibility/replay/bin/Release/net10.0/StripReplay.dll");

    public static string TestArtifactsDir => Path.Combine(RepoRoot, "Tests/ValidationTests/ValidationTests/TestArtifacts");

    /// <summary>
    /// Gets the path to the ILStrip implementation to test.
    /// Priority:
    /// 1. Environment variable: NEW_ILSTRIP_PATH or ILSTRIP_PATH
    /// 2. Local config file: ilstrip.config.txt
    /// 3. Default fallback: Referent legacy oracle (StripReplay.dll whole)
    /// </summary>
    public static (string StripperPath, string ArgumentTemplate, bool IsOracleFallback) GetConfiguredStripper()
    {
        string? envPath = Environment.GetEnvironmentVariable("NEW_ILSTRIP_PATH")
            ?? Environment.GetEnvironmentVariable("ILSTRIP_PATH");

        if (!string.IsNullOrWhiteSpace(envPath) && (File.Exists(envPath) || File.Exists(Path.Combine(RepoRoot, envPath))))
        {
            string fullPath = File.Exists(envPath) ? Path.GetFullPath(envPath) : Path.GetFullPath(Path.Combine(RepoRoot, envPath));
            string template = Environment.GetEnvironmentVariable("ILSTRIP_ARGS") ?? "{input} {output}";
            return (fullPath, template, false);
        }

        string configFile = Path.Combine(RepoRoot, "Tests/ValidationTests/ValidationTests/ilstrip.config.txt");
        if (File.Exists(configFile))
        {
            string line = File.ReadAllText(configFile).Trim();
            if (!string.IsNullOrEmpty(line) && File.Exists(line))
                return (Path.GetFullPath(line), "{input} {output}", false);
        }

        // Fallback to legacy oracle
        return (ReferentStripReplayDll, "whole {input} {output}", true);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "referent/SPEC.md")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    }
}
