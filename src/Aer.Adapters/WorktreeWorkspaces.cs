using Aer.Flow.Workspaces;

namespace Aer.Adapters;

/// <summary>
/// The pre-dispatch pass that turns a binding's declared <see cref="WorktreeWorkspace"/> into a real
/// directory the worker runs in (#669). For each entry declaring one it provisions a git worktree
/// under the task directory — one per worker, never shared — and rewrites that entry's
/// <see cref="WorkerBindingConfigEntry.WorkingDirectory"/> to point at it, so
/// <see cref="WorkerBindingResolver.Resolve"/> downstream sees an ordinary directory and needs no
/// worktree knowledge. Returns the worktrees to tear down once the run reaches Terminal.
///
/// <para>
/// Idempotent across resume: a worktree that already exists on a second <c>aer run</c> is reused, not
/// re-added (which git would refuse). Refuses an entry that sets both a WorkingDirectory and a
/// worktree, because a worker runs in exactly one place — a bind-time refusal, before the pump starts.
/// </para>
/// </summary>
public static class WorktreeWorkspaces
{
    /// <summary>The task-directory-relative parent the per-worker worktrees are created under.</summary>
    public const string WorkspacesDirectoryName = "workspaces";

    /// <summary>
    /// Provisions every declared worktree and returns the bindings with each such entry's
    /// WorkingDirectory rewritten to its worktree, plus the list to hand to teardown on Terminal. When
    /// no entry declares a worktree the input dictionary is returned unchanged.
    /// </summary>
    public static (IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings,
                   IReadOnlyList<ProvisionedWorktree> Provisioned)
        Provision(IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string taskDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDirectoryPath);

        Dictionary<string, WorkerBindingConfigEntry>? rewritten = null;
        var provisioned = new List<ProvisionedWorktree>();

        foreach (var (workerName, entry) in bindings)
        {
            if (entry.Worktree is not { } spec)
            {
                continue;
            }

            if (entry.WorkingDirectory is not null)
            {
                throw new InvalidWorkspaceSpecException(
                    $"Worker '{workerName}' declares both a WorkingDirectory and a worktree workspace; " +
                    "a worker runs in exactly one place. Set one, not both.");
            }

            // Validate on every path (a resume reuses the tree but must still refuse a bad spec).
            WorktreeProvisioner.ValidateSpec(spec.Repository, spec.Ref);
            var worktreePath = Path.Combine(taskDirectoryPath, WorkspacesDirectoryName, workerName);

            if (!Directory.Exists(worktreePath))
            {
                WorktreeProvisioner.Provision(worktreePath, spec.Repository, spec.Ref);
            }

            provisioned.Add(new ProvisionedWorktree(spec.Repository, worktreePath));
            rewritten ??= new Dictionary<string, WorkerBindingConfigEntry>(bindings);
            rewritten[workerName] = entry with { WorkingDirectory = worktreePath, Worktree = null, IsWorktree = true };
        }

        return (rewritten ?? bindings, provisioned);
    }
}

/// <summary>
/// A worktree provisioned for a run, held so <c>WorktreeProvisioner.Teardown</c> can be called on it
/// once the run reaches Terminal.
/// </summary>
public sealed record ProvisionedWorktree(string Repository, string WorktreePath);
