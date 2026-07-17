using System.Text;
using System.Text.Json;

namespace Aer.Ui;

/// <summary>
/// Discovery and loading for the conversation view's read model (M18 Phase 1, #177; UI spec
/// §10.1): an execution offers a conversation projection if and only if its durable artifact
/// directory contains <c>transcript.jsonl</c> — discovery by durable content alone, §3.1's
/// self-describing rule applied one level down, so no registry, no worker-binding inspection, and
/// no special-casing which worker wrote the file. A pure function of the file's bytes (§11
/// determinism), reading the artifact directory the same way <see cref="ArtifactLineageProjector"/>
/// already does (§12 names artifact directories a legitimate projection input).
/// </summary>
public static class TranscriptProjectionLoader
{
    public const string TranscriptFileName = "transcript.jsonl";

    /// <summary>
    /// §10.1's discovery rule by itself, without loading the file: whether this execution's output
    /// directory offers a conversation projection at all. Kept on the loader so no caller re-encodes
    /// the rule (or the file name) for cheap existence checks over many executions.
    /// </summary>
    public static bool HasTranscript(string executionOutputDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionOutputDirectory);
        return File.Exists(Path.Combine(executionOutputDirectory, TranscriptFileName));
    }

    /// <summary>
    /// Loads the transcript projection for one execution's output directory, or <c>null</c> when
    /// the directory or file does not exist — absence means "this execution has no conversation
    /// projection", never an error. An empty or partial file is not absence: a failed exchange's
    /// forensic prefix (§10.1) projects exactly as far as it durably got.
    /// </summary>
    public static TranscriptProjection? Load(string executionOutputDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionOutputDirectory);

        var transcriptFilePath = Path.Combine(executionOutputDirectory, TranscriptFileName);
        if (!File.Exists(transcriptFilePath))
        {
            return null;
        }

        // FileShare.ReadWrite: the producing worker appends with FileShare.Read, holding the file
        // open for the whole exchange — a load-on-refresh while the execution is still running must
        // read what is durably there so far, not fail on a sharing violation. Whole complete lines
        // are the writer's flush unit, so the only mid-write shape a reader can observe is a torn
        // final line, which projects as Malformed below.
        using var stream = new FileStream(
            transcriptFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<TranscriptLine>();
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            lines.Add(ParseLine(line, lineNumber));
        }

        return new TranscriptProjection(lines);
    }

    private static TranscriptLine ParseLine(string line, int lineNumber)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new TranscriptLine.Malformed(lineNumber);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Sequence", out var sequence) ||
                sequence.ValueKind != JsonValueKind.Number ||
                !sequence.TryGetInt32(out var sequenceValue) ||
                GetString(root, "Role") is not { } role ||
                GetString(root, "Vendor") is not { } vendor ||
                GetString(root, "Prompt") is not { } prompt ||
                GetString(root, "Text") is not { } text)
            {
                return new TranscriptLine.Malformed(lineNumber);
            }

            return new TranscriptLine.Turn(sequenceValue, role, vendor, prompt, text);
        }
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
