using System.Text.Json;
using Aer.Flow.Mutation;

namespace Aer.Flow.Domain;

/// <summary>
/// A versioned entry in a room memory document's version history (#672 M26 floor).
/// </summary>
public sealed record RoomMemoryVersion(
    int Version,
    string Operation,
    string TargetPath,
    string? Content,
    string Rationale,
    string Proposer,
    string Approver,
    DateTimeOffset Timestamp);

/// <summary>
/// A versioned room memory document owned by the room directory (#672 M26 floor, decision 0044).
/// Lifetime is coupled to the room directory, never to any conversation or session.
/// </summary>
public sealed record RoomMemoryDocument(
    int Version,
    string IndexContent,
    IReadOnlyDictionary<string, string> FactFiles,
    IReadOnlyList<RoomMemoryVersion> History)
{
    /// <summary>
    /// Loads the current room memory document and version from <paramref name="roomDirectoryPath"/>.
    /// </summary>
    public static async Task<RoomMemoryDocument> LoadAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var memoryRoot = Path.Combine(roomDirectoryPath, MemoryProposalApplier.MemoryDirectoryName);
        if (!Directory.Exists(memoryRoot))
        {
            return new RoomMemoryDocument(0, string.Empty, new Dictionary<string, string>(), Array.Empty<RoomMemoryVersion>());
        }

        var indexPath = Path.Combine(memoryRoot, MemoryProposalApplier.IndexFileName);
        var indexContent = File.Exists(indexPath)
            ? await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var versionsPath = Path.Combine(memoryRoot, MemoryProposalApplier.VersionsFileName);
        var history = new List<RoomMemoryVersion>();
        if (File.Exists(versionsPath))
        {
            var lines = await File.ReadAllLinesAsync(versionsPath, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var versionRecord = JsonSerializer.Deserialize<RoomMemoryVersion>(line);
                    if (versionRecord is not null)
                    {
                        history.Add(versionRecord);
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed lines if any
                }
            }
        }

        var currentVersion = history.Count > 0 ? history[^1].Version : 0;

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        var factFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(memoryRoot))
        {
            foreach (var file in Directory.GetFiles(memoryRoot, "*", enumeration))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals(MemoryProposalApplier.IndexFileName, StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(MemoryProposalApplier.VersionsFileName, StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(memoryRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                factFiles[relativePath] = content;
            }
        }

        return new RoomMemoryDocument(currentVersion, indexContent, factFiles, history.AsReadOnly());
    }
}
