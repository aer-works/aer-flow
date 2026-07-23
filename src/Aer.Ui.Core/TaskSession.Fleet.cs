using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.IO;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Ui.Core;

public sealed partial class TaskSession
{
    /// <summary>
    /// The fleet list (M24 Phase 5, #278): every known task/session directory's lightweight status.
    /// Daemon-only, same reasoning as <see cref="GetSessionCommandsAsync"/> — scanning both
    /// <c>~/.aer/tasks</c> and <c>~/.aer/sessions</c> is inherently a whole-host operation, not
    /// something this client instance's own in-process fallback state could answer meaningfully.
    /// </summary>
    public async Task<(IReadOnlyList<TaskFleetItem>? Items, string? ErrorMessage)> GetFleetAsync(
        bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        if (!await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            return (null, "Listing tasks requires the daemon, and none is reachable.");
        }

        try
        {
            var response = await _httpClient.GetAsync(
                $"{_activeDaemonUrl}/api/tasks?includeArchived={includeArchived}", cancellationToken).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }

            var items = await response.Content.ReadFromJsonAsync<List<TaskFleetItem>>(
                DefaultJsonOptions, cancellationToken: cancellationToken).ConfigureAwait(true);
            return (items, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Archives a task/session directory (M24 Phase 5, #278) — hidden from the default fleet list, name still reserved until a real delete.</summary>
    public async Task<MutationOutcome> ArchiveTaskAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_activeDaemonUrl}/api/tasks/archive", new TaskDirectoryRequest(taskDirectoryPath), cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    return new MutationOutcome(null);
                }
                return new MutationOutcome(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        try
        {
            await TaskLifecycle.ArchiveAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(null);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>Unarchives a task/session directory (M24 Phase 5, #278) — reappears in the default fleet list.</summary>
    public async Task<MutationOutcome> UnarchiveTaskAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_activeDaemonUrl}/api/tasks/unarchive", new TaskDirectoryRequest(taskDirectoryPath), cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    return new MutationOutcome(null);
                }
                return new MutationOutcome(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        try
        {
            await TaskLifecycle.UnarchiveAsync(taskDirectoryPath).ConfigureAwait(true);
            return new MutationOutcome(null);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }

    /// <summary>Really deletes a task/session directory (M24 Phase 5, #278) — the only action that frees its name for reuse — and strips it from the recents list so a stale recent never 404s on the next open.</summary>
    public async Task<MutationOutcome> DeleteTaskAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
    {
        if (await EnsureDaemonConnectedAsync(cancellationToken).ConfigureAwait(true))
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_activeDaemonUrl}/api/tasks/delete", new TaskDirectoryRequest(taskDirectoryPath), cancellationToken).ConfigureAwait(true);
                if (response.IsSuccessStatusCode)
                {
                    return new MutationOutcome(null);
                }
                return new MutationOutcome(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                return new MutationOutcome(ex.Message);
            }
        }

        // In-process fallback
        try
        {
            if (!Directory.Exists(taskDirectoryPath))
            {
                return new MutationOutcome($"'{taskDirectoryPath}' does not exist.");
            }

            Directory.Delete(taskDirectoryPath, recursive: true);
            await _configurationStore.RemoveRecentTaskDirectoryAsync(taskDirectoryPath, cancellationToken).ConfigureAwait(true);
            return new MutationOutcome(null);
        }
        catch (Exception ex)
        {
            return new MutationOutcome(ex.Message);
        }
    }
}
