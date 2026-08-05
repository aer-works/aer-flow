using Aer.Flow;

namespace Aer.Adapters;

/// <summary>
/// Raised by <see cref="WorkerBindingResolver.Resolve"/> when an entry specifies
/// <see cref="Aer.Flow.Domain.GrantAuditMode.AuditedNotEnforced"/> without a provisioned worktree.
/// Post-run grant audit requires workspace isolation (<see cref="WorkerBindingConfigEntry.Worktree"/>
/// or provisioned via <see cref="WorktreeWorkspaces.Provision"/>); an audit against a shared
/// working directory would see unrelated dirt or miss nothing.
/// </summary>
public sealed class UnisolatedGrantAuditException : AerFlowException
{
    public string WorkerName { get; }

    public UnisolatedGrantAuditException(string workerName)
        : base(
            $"Worker-binding config entry for '{workerName}' specifies GrantAuditMode.AuditedNotEnforced " +
            "without a provisioned worktree. Post-run grant audit requires workspace isolation " +
            "(WorkerBindingConfigEntry.Worktree); an audit against a shared working directory would see unrelated dirt or miss nothing.")
    {
        WorkerName = workerName;
    }
}
