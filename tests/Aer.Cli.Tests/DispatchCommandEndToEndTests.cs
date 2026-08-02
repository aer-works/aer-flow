using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Domain;

namespace Aer.Cli.Tests;

/// <summary>
/// <c>aer dispatch &lt;role&gt;</c> end to end (#900): a real shipped catalog role is materialized into
/// a single-step workflow and driven through the exact pump <c>aer run</c> uses, so the outputs the
/// role declares become a contract the engine enforces — satisfied means Succeeded, a silent no-op
/// means Failed. The fake adapter (<see cref="ContractOutputWorkerAdapter"/>) stands in for the worker
/// so no live LLM is needed; the role, its outputs, and the contract are the real ones.
/// </summary>
[Collection(WorkerCatalogEnvCollection.Name)]
public sealed class DispatchCommandEndToEndTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>
        {
            ["fake"] = new ContractOutputWorkerAdapter(satisfyOutputs: true),
            ["fake-noop"] = new ContractOutputWorkerAdapter(satisfyOutputs: false),
        };

    private readonly string? _priorRoles = Environment.GetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable);
    private readonly string? _priorTiers = Environment.GetEnvironmentVariable(WorkerRoleCatalog.TiersPathEnvironmentVariable);

    // Pin the shipped catalog. Without this these tests resolve through ResolvePath's middle rung
    // ({AerPaths.Root}/worker-roles.json) and would silently read an operator's local override on a
    // machine that has one -- the exact hazard WorkerRoleCatalogTests.ShippedDefault documents and
    // guards. The env edit is process-global, and one test below deliberately points the roles path at a
    // malformed catalog -- so this class is not the only catalog reader that matters. It shares
    // [Collection(WorkerCatalogEnvCollection.Name)] with DispatchTemplateEndToEndTests precisely so that
    // malformed path cannot bleed across into a parallel template dispatch mid-run (#929); the ctor/Dispose
    // set-and-restore keeps it clean within this serialized group.
    public DispatchCommandEndToEndTests()
    {
        Environment.SetEnvironmentVariable(
            WorkerRoleCatalog.RolesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"));
        Environment.SetEnvironmentVariable(
            WorkerRoleCatalog.TiersPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable, _priorRoles);
        Environment.SetEnvironmentVariable(WorkerRoleCatalog.TiersPathEnvironmentVariable, _priorTiers);
    }

    [Fact]
    public async Task Dispatching_a_role_whose_worker_writes_its_declared_output_succeeds()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var taskDirectory = Path.Combine(testRoot, "task");
            var options = new DispatchOptions("advise", specPath, taskDirectory, Adapter: "fake");

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            Assert.Equal(WorkflowStatus.Terminal, state.Status);
            var step = Assert.Single(state.Steps);
            Assert.Equal("advise", step.StepId.Value);
            Assert.Equal(StepStatus.Succeeded, step.Status);

            // advise declares advice.md; the contract the engine enforced is the role's own.
            var advicePath = Path.Combine(
                taskDirectory, "artifacts", $"execution_{step.LatestExecutionId}", "advice.md");
            Assert.True(File.Exists(advicePath));

            // The dispatch persisted the same files a template run would, so the task is resumable.
            Assert.True(File.Exists(Path.Combine(taskDirectory, "workflow.json")));
            Assert.True(File.Exists(Path.Combine(taskDirectory, "bindings.json")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_a_role_whose_worker_writes_nothing_fails_the_contract()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "Weigh the options for X.");
            var taskDirectory = Path.Combine(testRoot, "task");
            // Exits 0 but produces no advice.md — the floor a per-role output exists to catch.
            var options = new DispatchOptions("advise", specPath, taskDirectory, Adapter: "fake-noop");

            var state = (await DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken)).State;

            var step = Assert.Single(state.Steps);
            Assert.Equal(StepStatus.Failed, step.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_an_unknown_role_is_a_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            var specPath = await WriteSpecAsync(testRoot, "spec");
            var options = new DispatchOptions("no-such-role", specPath, Path.Combine(testRoot, "task"));

            var ex = await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
            Assert.Contains("no-such-role", ex.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task An_unreadable_catalog_is_a_typed_argument_error_not_a_crash()
    {
        // A typo'd env override or a hand-broken worker-roles.json must exit cleanly, not dump an
        // unhandled JsonException; before the broadened catch (see DispatchCommand) this escaped
        // Program's boundary as exit 127.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var badCatalog = Path.Combine(testRoot, "worker-roles.json");
            await File.WriteAllTextAsync(badCatalog, "{ not valid json", TestContext.Current.CancellationToken);
            Environment.SetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable, badCatalog);

            var specPath = await WriteSpecAsync(testRoot, "spec");
            var options = new DispatchOptions("advise", specPath, Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_with_a_missing_spec_file_is_a_typed_argument_error()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-e2e-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testRoot);
            var options = new DispatchOptions(
                "advise", Path.Combine(testRoot, "does-not-exist.md"), Path.Combine(testRoot, "task"));

            await Assert.ThrowsAsync<CliArgumentException>(
                () => DispatchCommand.ExecuteAsync(options, Adapters, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spec.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
