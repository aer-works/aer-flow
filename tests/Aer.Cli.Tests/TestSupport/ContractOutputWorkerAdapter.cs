using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Cli.Tests.TestSupport;

/// <summary>
/// A CI-safe stand-in for a well-behaved (or silently no-op) worker, driven by what the
/// <see cref="WorkerContract"/> declares rather than by the prompt — so an <c>aer dispatch</c> test
/// can run a real catalog role through the whole pump without a live LLM and without the prompt having
/// to be a literal shell command (which <see cref="ShellCommandWorkerAdapter"/> requires and
/// <c>RoleDispatch</c>'s prose prompt is not). When <paramref name="satisfyOutputs"/> is true it writes
/// each declared output into <c>$AER_OUTPUT_DIR</c>; when false it exits 0 having written nothing — the
/// exact "exit 0 but produced nothing" the role's contract floor exists to catch.
/// </summary>
internal sealed class ContractOutputWorkerAdapter(bool satisfyOutputs) : IWorkerAdapter
{
    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        var script = satisfyOutputs && contract.ProducedOutputs.Count > 0
            ? string.Join(
                OperatingSystem.IsWindows() ? " & " : " && ",
                contract.ProducedOutputs.Select(o => WriteCommand(o.Name)))
            : "exit 0";

        return OperatingSystem.IsWindows()
            ? new CoreDispatchTarget("cmd", ["/c", script], invocation.WorkingDirectory)
            : new CoreDispatchTarget("sh", ["-c", script], invocation.WorkingDirectory);
    }

    private static string WriteCommand(string outputName) => OperatingSystem.IsWindows()
        ? $"echo x>%AER_OUTPUT_DIR%\\{outputName}"
        : $"echo x > \"$AER_OUTPUT_DIR/{outputName}\"";
}
