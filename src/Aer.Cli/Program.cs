using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;

if (args.Length == 0 || (args[0] != "run" && args[0] != "cancel"))
{
    Console.Error.WriteLine(
        "Usage: aer run <workflow-file> --bindings <bindings-file> [--task-dir <dir>] [--workflow-id <id>]");
    Console.Error.WriteLine(
        "       aer cancel <task-dir> --execution <execution-id> --bindings <bindings-file> [--workflow-id <id>]");
    return 64;
}

using var hostStopSource = new CancellationTokenSource();

// §9's host-initiated stop (M10 Phase 2), finally wired to something: Ctrl+C no longer kills the
// process outright — it cancels the ambient token the pump races against, which records
// CancellationRequested for every in-flight execution before signalling any of them (§7's
// intent-first ordering). Suppressing the default SIGINT behavior is what keeps the process alive
// long enough for that to happen.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    hostStopSource.Cancel();
};

try
{
    FlowState finalState;
    if (args[0] == "run")
    {
        var options = RunOptionsParser.Parse(args[1..]);
        finalState = await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
            .ConfigureAwait(false);
    }
    else
    {
        var options = CancelOptionsParser.Parse(args[1..]);
        finalState = await CancelCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default, hostStopSource.Token)
            .ConfigureAwait(false);
    }

    Console.WriteLine($"Workflow status: {finalState.Status}");
    foreach (var step in finalState.Steps)
    {
        Console.WriteLine($"  {step.StepId}: {step.Status}");
    }

    return finalState.Status == WorkflowStatus.Terminal && finalState.Steps.All(step => step.Status == StepStatus.Succeeded)
        ? 0
        : 1;
}
catch (AerFlowException ex)
{
    // The typed-exception boundary CLAUDE.md's error-handling rules require: every malformed
    // workflow/bindings/argument failure surfaces as one of these further up the call stack, so
    // this is the one place that turns it into a clean CLI failure instead of a raw stack trace.
    Console.Error.WriteLine(ex.Message);
    return 1;
}
