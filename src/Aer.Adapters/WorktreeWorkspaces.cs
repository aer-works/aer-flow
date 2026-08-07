using Aer.Flow.Concurrency;
using Aer.Flow.Workspaces;

namespace Aer.Adapters;

/// <summary>
/// The pre-dispatch pass that turns a binding's declared <see cref="WorktreeWorkspace"/> into a real
/// directory the worker runs in (#669). For each entry declaring one it provisions a git worktree
/// under the room directory — one per worker, never shared — and rewrites that entry's
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
    /// <summary>The room-directory-relative parent the per-worker worktrees are created under.</summary>
    public const string WorkspacesDirectoryName = "workspaces";

    /// <summary>
    /// Provisions every declared worktree and returns the bindings with each such entry's
    /// WorkingDirectory rewritten to its worktree, plus the list to hand to teardown on Terminal. When
    /// no entry declares a worktree the input dictionary is returned unchanged.
    /// <para>
    /// The strict half of the pair: it is <see cref="ProvisionLazily"/>'s walk, with the first entry
    /// that could not be provisioned rethrown rather than skipped. Two copies of the walk would be two
    /// things to keep in step, and the skip/throw choice is the only difference between them.
    /// </para>
    /// </summary>
    public static (IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings,
                   IReadOnlyList<ProvisionedWorktree> Provisioned)
        Provision(IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string roomDirectoryPath)
    {
        var (rewritten, provisioned, _) = Walk(bindings, roomDirectoryPath, throwOnFailure: true);
        return (rewritten, provisioned);
    }

    /// <summary>
    /// Same provisioning as <see cref="Provision"/>, but skips any entry whose worktree specification is invalid
    /// or fails to provision, leaving its binding untouched and returning it in the skipped list (#1012).
    ///
    /// <para>
    /// A skipped entry keeps no isolation stamp, so if it is ever actually dispatched the existing refusal fires —
    /// <see cref="UnisolatedGrantAuditException"/> for an audited binding — and the failure re-surfaces where it is
    /// actionable instead of blocking an unrelated cancel.
    /// </para>
    /// </summary>
    public static (IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings,
                   IReadOnlyList<ProvisionedWorktree> Provisioned,
                   IReadOnlyList<SkippedWorktreeProvisioning> Skipped)
        ProvisionLazily(IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string roomDirectoryPath) =>
        Walk(bindings, roomDirectoryPath, throwOnFailure: false);

    /// <summary>
    /// The one walk both entry points above share. <paramref name="throwOnFailure"/> rethrows at the
    /// failing entry rather than skipping it — which also stops the walk there, so the strict caller
    /// never leaves later entries' trees provisioned behind a refusal it is about to throw.
    /// </summary>
    private static (IReadOnlyDictionary<string, WorkerBindingConfigEntry> Bindings,
                    IReadOnlyList<ProvisionedWorktree> Provisioned,
                    IReadOnlyList<SkippedWorktreeProvisioning> Skipped)
        Walk(IReadOnlyDictionary<string, WorkerBindingConfigEntry> bindings, string roomDirectoryPath, bool throwOnFailure)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomDirectoryPath);

        using var guard = ConcurrencyGuard.Acquire(roomDirectoryPath, "worktree provisioning");

        Dictionary<string, WorkerBindingConfigEntry>? rewritten = null;
        var provisioned = new List<ProvisionedWorktree>();
        var skipped = new List<SkippedWorktreeProvisioning>();

        foreach (var (workerName, entry) in bindings)
        {
            if (entry.Worktree is not { } spec)
            {
                continue;
            }

            if (entry.WorkingDirectory is not null)
            {
                var bothDeclared = new InvalidWorkspaceSpecException(
                    $"Worker '{workerName}' declares both a WorkingDirectory and a worktree workspace; " +
                    "a worker runs in exactly one place. Set one, not both.");

                if (throwOnFailure)
                {
                    throw bothDeclared;
                }

                skipped.Add(new SkippedWorktreeProvisioning(workerName, bothDeclared));
                continue;
            }

            try
            {
                // Validate on every path (a resume reuses the tree but must still refuse a bad spec).
                WorktreeProvisioner.ValidateSpec(spec.Repository, spec.Ref);
                var worktreePath = Path.Combine(roomDirectoryPath, WorkspacesDirectoryName, workerName);

                if (!Directory.Exists(worktreePath))
                {
                    WorktreeProvisioner.Provision(worktreePath, spec.Repository, spec.Ref);
                }

                provisioned.Add(new ProvisionedWorktree(spec.Repository, worktreePath));
                rewritten ??= new Dictionary<string, WorkerBindingConfigEntry>(bindings);
                rewritten[workerName] = entry with { WorkingDirectory = worktreePath, Worktree = null, IsWorktree = true };
            }
            catch (Exception ex) when (!throwOnFailure
                && ex is InvalidWorkspaceSpecException or WorktreeProvisioningException)
            {
                skipped.Add(new SkippedWorktreeProvisioning(workerName, ex));
            }
        }

        return (rewritten ?? bindings, provisioned, skipped);
    }
}

/// <summary>
/// An entry whose worktree could not be provisioned during <see cref="WorktreeWorkspaces.ProvisionLazily"/>.
/// </summary>
public sealed record SkippedWorktreeProvisioning(string WorkerName, Exception Exception);
