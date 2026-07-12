using Aer.Adapters;
using Aer.Cli;
using Aer.Flow;
using Aer.Flow.Domain;

if (args.Length == 0 || args[0] != "run")
{
    Console.Error.WriteLine(
        "Usage: aer run <workflow-file> --bindings <bindings-file> [--task-dir <dir>] [--workflow-id <id>]");
    return 64;
}

try
{
    var options = RunOptionsParser.Parse(args[1..]);
    var finalState = await RunCommand.ExecuteAsync(options, WorkerAdapterRegistry.Default).ConfigureAwait(false);

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
