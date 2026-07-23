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
    public Task RecordOpenedAsync(string taskDirectoryPath, CancellationToken cancellationToken = default)
        => _configurationStore.RecordOpenedAsync(taskDirectoryPath, cancellationToken);

    public Task<IReadOnlyList<string>> LoadRecentTaskDirectoriesAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadRecentTaskDirectoriesAsync(cancellationToken);

    public Task<string?> LoadLastBindingsFilePathAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadLastBindingsFilePathAsync(cancellationToken);

    public Task<string?> LoadLastWorkflowTemplateFilePathAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadLastWorkflowTemplateFilePathAsync(cancellationToken);

    public Task<string?> LoadTailscaleAuthKeyAsync(CancellationToken cancellationToken = default)
        => _configurationStore.LoadTailscaleAuthKeyAsync(cancellationToken);

    public Task RecordTailscaleAuthKeyAsync(string? tailscaleAuthKey, CancellationToken cancellationToken = default)
        => _configurationStore.RecordTailscaleAuthKeyAsync(tailscaleAuthKey, cancellationToken);
}
