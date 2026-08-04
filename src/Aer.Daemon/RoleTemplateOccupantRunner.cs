using System.Globalization;
using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Store;

namespace Aer.Daemon;

public sealed class RoleTemplateOccupantRunner : IOccupantTurnRunner
{
    private sealed class NoOpCoreEventLogWriter : ICoreEventLogWriter
    {
        public Task AppendAsync(CoreEvent coreEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private readonly IReadOnlyDictionary<string, IWorkerAdapter> _adapters;
    private readonly ICoreDispatcher _dispatcher;

    public RoleTemplateOccupantRunner(
        IReadOnlyDictionary<string, IWorkerAdapter> adapters,
        ICoreDispatcher? dispatcher = null)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _dispatcher = dispatcher ?? new CoreDispatcher(new NoOpCoreEventLogWriter());
    }

    public async Task<OccupantTurnResult> RunTurnAsync(OrchestratorTurnInput input, TimeSpan budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var roomDir = input.RoomDirectoryPath;
        if (string.IsNullOrWhiteSpace(roomDir))
        {
            return new OccupantTurnResult.Failed("RoomDirectoryPath was missing from OrchestratorTurnInput.");
        }

        // 1. Resolve "orchestrate" role from WorkerRoleCatalog
        WorkerRole role;
        try
        {
            role = WorkerRoleCatalog.For("orchestrate");
        }
        catch (Exception ex)
        {
            return new OccupantTurnResult.Failed($"Failed to resolve 'orchestrate' role from catalog: {ex.Message}");
        }

        // 2. Resolve adapter
        if (!_adapters.TryGetValue(role.Adapter, out var adapter))
        {
            return new OccupantTurnResult.Failed($"Worker adapter '{role.Adapter}' not found in registry.");
        }

        // 3. Render prompt
        var promptText = OrchestratorTurnPrompt.Render(input);

        // 4. Output directory
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var outputDir = Path.Combine(roomDir, ".aer", "occupant-turns", timestamp);
        Directory.CreateDirectory(outputDir);

        // 5. Build WorkerInvocation & WorkerContract, resolve target
        var contract = new WorkerContract(
            WorkerName: "orchestrator",
            RequiredInputs: [],
            ProducedOutputs: role.Outputs.Select(o => new ProducedOutput(o.Name, Schema: o.Schema)).ToList(),
            OptionalMetadata: []);

        var invocation = new WorkerInvocation(
            PromptTemplate: promptText,
            Model: role.Model,
            PermissionScope: null,
            PermissionGrant: role.Grant,
            WorkingDirectory: roomDir,
            BindingsFileDirectory: null,
            SessionId: null,
            ResumeSession: false,
            StreamJson: false,
            LogFilePath: null,
            Effort: role.Effort,
            Timeout: budget);

        CoreDispatchTarget target;
        try
        {
            target = adapter.Resolve(invocation, contract);
        }
        catch (Exception ex)
        {
            return new OccupantTurnResult.Failed($"Adapter resolution failed for '{role.Adapter}': {ex.Message}");
        }

        // 6. Execute process via dispatcher
        var execRequest = new ExecutionRequest(
            ExecutionId: new ExecutionId(Guid.NewGuid().ToString("N")),
            WorkflowId: new WorkflowId("occupant-turn"),
            StepId: null,
            Worker: "orchestrator",
            Inputs: [],
            Outputs: ["turn-actions.json"],
            Timeout: budget,
            Environment: [new EnvironmentVariable.AerComputed("AER_OUTPUT_DIR", outputDir)],
            UpstreamExecutionIds: new Dictionary<StepId, ExecutionId>());

        CoreDispatchResult dispatchResult;
        try
        {
            dispatchResult = await _dispatcher.DispatchAsync(execRequest, target, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new OccupantTurnResult.Failed($"Dispatch execution failed: {ex.Message}");
        }

        if (dispatchResult.Reason != CoreExitReason.Natural || dispatchResult.ExitCode != 0)
        {
            return new OccupantTurnResult.Failed(
                $"Worker process exited with code {dispatchResult.ExitCode} ({dispatchResult.Reason}). Stderr: {dispatchResult.StderrTail}");
        }

        // 7. Read turn-actions.json from output directory
        var actionsFilePath = Path.Combine(outputDir, "turn-actions.json");
        if (!File.Exists(actionsFilePath))
        {
            // Missing turn-actions.json counts toward the breaker upstream — that is the design, not an accident.
            return new OccupantTurnResult.Failed("no turn-actions.json found in occupant turn output directory.");
        }

        string jsonText;
        try
        {
            jsonText = await File.ReadAllTextAsync(actionsFilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Read failure counts toward the breaker upstream — that is the design, not an accident.
            return new OccupantTurnResult.Failed($"Failed to read turn-actions.json: {ex.Message}");
        }

        var (actions, parseError) = OccupantTurnActions.Parse(jsonText);
        if (actions is null || parseError is not null)
        {
            // Parse errors count toward the breaker upstream — that is the design, not an accident.
            return new OccupantTurnResult.Failed($"Failed to parse turn-actions.json: {parseError}");
        }

        // 8. Process escalations
        var roomLogPath = Path.Combine(roomDir, "room.jsonl");
        var reader = new RoomEventLogReader(roomLogPath);
        await using var writer = new RoomEventLogWriter(roomLogPath);
        var workerId = new WorkerId("orchestrator");

        foreach (var esc in actions.Escalations)
        {
            await RoomMutationInterface.RaiseEscalationAsync(
                roomDir,
                workerId,
                esc.Trigger,
                esc.Subject,
                reader,
                writer,
                cancellationToken: ct).ConfigureAwait(false);
        }

        return new OccupantTurnResult.Completed();
    }
}
