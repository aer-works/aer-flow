using System.Text.Json;

namespace Aer.Ui;

/// <summary>
/// Local UI Configuration (UI spec §3.1, §4): a remembered list of recently opened task
/// directories. Deliberately never authoritative — a task directory's own contents are (§3.1's
/// self-describing-directory contract) — so this store treats a missing or corrupt config file as
/// "no recents remembered yet" rather than a startup failure, and drops any remembered path that no
/// longer exists on disk when it loads the list back, rather than surfacing it as an error. This is
/// this phase's concrete answer to §3.1's "how a UI populates its list" implementation choice: ask
/// the user for a path (or pick a remembered one), never scan a configured root.
/// </summary>
public sealed class LocalUiConfigurationStore(string configFilePath)
{
    private const int MaxRecentTaskDirectories = 10;

    /// <summary>
    /// The production location: a per-user config directory, never a path a test could collide
    /// with — tests construct this store directly against a temp file instead of calling this.
    /// </summary>
    public static LocalUiConfigurationStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "Aer.Ui",
        "recent-task-directories.json"));

    public async Task<IReadOnlyList<string>> LoadRecentTaskDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configFilePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(configFilePath);
            var paths = await JsonSerializer.DeserializeAsync<List<string>>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // A listed directory that no longer exists is stale list state, reflected here by
            // omission rather than surfaced as a system error (UI spec §3.1).
            return (paths ?? []).Where(Directory.Exists).ToList();
        }
        catch (JsonException)
        {
            // Local UI Configuration is a rebuildable convenience, never authoritative (§3.1) — a
            // corrupt file is treated as an empty list, not a startup failure.
            return [];
        }
    }

    /// <summary>
    /// Records <paramref name="taskDirectoryPath"/> as the most recently opened directory,
    /// deduplicated against any existing entry for the same path and capped at
    /// <see cref="MaxRecentTaskDirectories"/> — the list is a bounded convenience, not a full
    /// history.
    /// </summary>
    public async Task RecordOpenedAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        var existing = await LoadRecentTaskDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(taskDirectoryPath);

        var updated = new List<string> { fullPath };
        updated.AddRange(existing.Where(path => !string.Equals(Path.GetFullPath(path), fullPath, StringComparison.Ordinal)));
        if (updated.Count > MaxRecentTaskDirectories)
        {
            updated.RemoveRange(MaxRecentTaskDirectories, updated.Count - MaxRecentTaskDirectories);
        }

        var directory = Path.GetDirectoryName(configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(configFilePath);
        await JsonSerializer.SerializeAsync(stream, updated, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
