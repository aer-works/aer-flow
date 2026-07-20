using System.Text.Json;

namespace Aer.Ui.Core;

/// <summary>
/// Local UI Configuration (UI spec §3.1, §4): a remembered list of recently opened task
/// directories, plus (M15 Phase 1, issue #137) the last worker-bindings file and workflow template
/// file a Run action used — bindings are never persisted in a task directory (M14 Phase 2's
/// decision of record) and a template is only ever consulted on a fresh start, so both are UI
/// inputs asked for every time, with the value remembered here purely to pre-fill that ask.
/// Deliberately never authoritative — a task directory's own contents are (§3.1's
/// self-describing-directory contract) — so this store treats a missing or corrupt config file as
/// "nothing remembered yet" rather than a startup failure, and drops any remembered task directory
/// path that no longer exists on disk when it loads the list back, rather than surfacing it as an
/// error. This is this phase's concrete answer to §3.1's "how a UI populates its list"
/// implementation choice: ask the user for a path (or pick a remembered one), never scan a
/// configured root.
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
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);

        // A listed directory that no longer exists is stale list state, reflected here by
        // omission rather than surfaced as a system error (UI spec §3.1).
        return configuration.RecentTaskDirectories.Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Records <paramref name="taskDirectoryPath"/> as the most recently opened directory,
    /// deduplicated against any existing entry for the same path and capped at
    /// <see cref="MaxRecentTaskDirectories"/> — the list is a bounded convenience, not a full
    /// history.
    /// </summary>
    public async Task RecordOpenedAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(taskDirectoryPath);

        var updated = new List<string> { fullPath };
        updated.AddRange(configuration.RecentTaskDirectories.Where(
            path => !string.Equals(Path.GetFullPath(path), fullPath, StringComparison.Ordinal)));
        if (updated.Count > MaxRecentTaskDirectories)
        {
            updated.RemoveRange(MaxRecentTaskDirectories, updated.Count - MaxRecentTaskDirectories);
        }

        await SaveConfigurationAsync(configuration with { RecentTaskDirectories = updated }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The bindings file (M15 Phase 1, issue #137): never persisted in a task directory (M14 Phase
    /// 2's decision of record), so a Run action asks the user for it every time — this is only the
    /// remembered default that pre-fills the ask, exactly the same non-authoritative convenience the
    /// recents list already is (§3.1, §4).
    /// </summary>
    public async Task<string?> LoadLastBindingsFilePathAsync(CancellationToken cancellationToken = default) =>
        (await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false)).LastBindingsFilePath;

    public async Task RecordBindingsFilePathAsync(string bindingsFilePath, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await SaveConfigurationAsync(
            configuration with { LastBindingsFilePath = Path.GetFullPath(bindingsFilePath) }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The remembered workflow template path — only ever asked for on a fresh start (§137's resolved open question).</summary>
    public async Task<string?> LoadLastWorkflowTemplateFilePathAsync(CancellationToken cancellationToken = default) =>
        (await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false)).LastWorkflowTemplateFilePath;

    public async Task RecordWorkflowTemplateFilePathAsync(string workflowTemplateFilePath, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await SaveConfigurationAsync(
            configuration with { LastWorkflowTemplateFilePath = Path.GetFullPath(workflowTemplateFilePath) }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A reusable Tailscale auth key (M21 Phase 7 follow-up, #246): once the tsnet sidecar is ready,
    /// the pairing QR embeds this so a phone's own embedded tsnet node can join the tailnet
    /// non-interactively — the `tailscale` Dart package requires a real auth key for a device's
    /// first-ever enrollment (confirmed against its vendored source; it does not support the
    /// keyless-then-`needsLogin` flow for a device with zero prior state). One key, generated once in
    /// the Tailscale admin console and pasted here, covers every phone that ever scans the QR — never
    /// sent anywhere over the network, only rendered into the on-screen QR image.
    /// </summary>
    public async Task<string?> LoadTailscaleAuthKeyAsync(CancellationToken cancellationToken = default) =>
        (await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false)).TailscaleAuthKey;

    public async Task RecordTailscaleAuthKeyAsync(string? tailscaleAuthKey, CancellationToken cancellationToken = default)
    {
        var configuration = await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await SaveConfigurationAsync(
            configuration with { TailscaleAuthKey = string.IsNullOrWhiteSpace(tailscaleAuthKey) ? null : tailscaleAuthKey.Trim() },
            cancellationToken).ConfigureAwait(false);
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<StoredConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(configFilePath))
        {
            return new StoredConfiguration([], null, null, null);
        }

        for (var i = 0; i < 5; i++)
        {
            try
            {
                await using var stream = new FileStream(configFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var configuration = await JsonSerializer.DeserializeAsync<StoredConfiguration>(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return configuration ?? new StoredConfiguration([], null, null, null);
            }
            catch (IOException) when (i < 4)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Local UI Configuration is a rebuildable convenience, never authoritative (§3.1) — a
                // corrupt file is treated as empty, not a startup failure.
                return new StoredConfiguration([], null, null, null);
            }
        }
        return new StoredConfiguration([], null, null, null);
    }

    private async Task SaveConfigurationAsync(StoredConfiguration configuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(configFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            for (var i = 0; i < 5; i++)
            {
                try
                {
                    await using var stream = new FileStream(configFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    await JsonSerializer.SerializeAsync(stream, configuration, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (i < 4)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The on-disk shape of this file. A plain JSON array was the whole file before M15 Phase 1
    /// (issue #137) added the two remembered file paths below it; that old shape deserializes as
    /// neither a JSON object nor a valid <see cref="StoredConfiguration"/>, so an upgrade from it
    /// falls through the same corrupt-file recovery <see cref="LoadConfigurationAsync"/> already has
    /// — Local UI Configuration is a rebuildable convenience, never authoritative (§3.1), so losing a
    /// stale recents list across this shape change is an acceptable, silent reset rather than a
    /// migration worth writing.
    /// </summary>
    private sealed record StoredConfiguration(
        List<string> RecentTaskDirectories,
        string? LastBindingsFilePath,
        string? LastWorkflowTemplateFilePath,
        string? TailscaleAuthKey);
}
