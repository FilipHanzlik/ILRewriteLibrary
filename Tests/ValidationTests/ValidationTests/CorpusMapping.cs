using System.Text.Json.Serialization;

namespace ValidationTests;

public sealed class CorpusManifest
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("sourceCorpus")]
    public string SourceCorpus { get; set; } = "";

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("fixtures")]
    public List<CorpusFixture> Fixtures { get; set; } = [];

    [JsonPropertyName("byName")]
    public Dictionary<string, CorpusFixture> ByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CorpusFixture
{
    [JsonPropertyName("index")]
    public string Index { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("inputFile")]
    public string InputFile { get; set; } = "";

    [JsonPropertyName("expectedFile")]
    public string ExpectedFile { get; set; } = "";

    [JsonPropertyName("hasPdb")]
    public bool HasPdb { get; set; }

    [JsonPropertyName("pdbFile")]
    public string? PdbFile { get; set; }

    [JsonPropertyName("inputLength")]
    public long InputLength { get; set; }

    [JsonPropertyName("expectedLength")]
    public long ExpectedLength { get; set; }

    [JsonPropertyName("inputSha256")]
    public string InputSha256 { get; set; } = "";

    [JsonPropertyName("expectedSha256")]
    public string ExpectedSha256 { get; set; } = "";

    [JsonPropertyName("rawExpectedSha256")]
    public string RawExpectedSha256 { get; set; } = "";

    [JsonPropertyName("mvid")]
    public string Mvid { get; set; } = "";

    [JsonPropertyName("compatibilityObservations")]
    public List<string> CompatibilityObservations { get; set; } = [];
}
