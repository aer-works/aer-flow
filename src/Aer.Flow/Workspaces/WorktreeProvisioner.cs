using System.Diagnostics;

namespace Aer.Flow.Workspaces;

/// <summary>
/// Provisions a git worktree as a worker's workspace and tears it down once the task is Terminal —
/// the engine half of #669, so a reviewer can be dispatched at a branch without a human checking it
/// out anywhere, and without the review and the ongoing work fighting over one tree.
///
/// <para>
/// Vendor-agnostic (Architecture Rule 2): <c>Aer.Flow</c> never learns which vendor runs in the tree —
/// git is infrastructure, not an AI vendor, so this belongs beside <c>ArtifactManager</c> in the
/// dispatch layer rather than in <c>Aer.Adapters</c>. <b>Local worktrees only</b> — no clone, no fetch,
/// no network: a worktree of a repository already on disk needs no credential, so Rule 4 (Credential
/// Isolation) is untouched. The moment this grows a clone it acquires a credential problem, which is a
/// different decision (#669).
/// </para>
/// </summary>
public static class WorktreeProvisioner
{
    /// <summary>The task-directory-relative name the worktree is created under.</summary>
    public const string WorkspaceDirectoryName = "workspace";

    /// <summary>
    /// The bind-time check, separated so a caller can refuse a bad spec before the pump starts rather
    /// than discovering it at dispatch (#668's class). The repository must be an absolute, fully
    /// qualified path — AER and the worker resolve a relative one against different bases, so the run
    /// would fail its contract after paying in full (#668; <see cref="Path.IsPathFullyQualified(string)"/>,
    /// not <c>IsPathRooted</c>, is the predicate that actually means it, since <c>IsPathRooted("C:x")</c>
    /// is true while the path is still relative to a drive's current directory) — and the ref must be
    /// non-empty.
    /// </summary>
    public static void ValidateSpec(string repository, string reference)
    {
        if (string.IsNullOrWhiteSpace(repository) || !Path.IsPathFullyQualified(repository))
        {
            throw new InvalidWorkspaceSpecException(
                $"A worktree workspace needs an absolute repository path; '{repository}' is not fully " +
                "qualified. A relative path resolves against a different base for AER and the worker, so " +
                "the run would fail its contract after paying in full (#668).");
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidWorkspaceSpecException(
                "A worktree workspace needs a non-empty git ref (a branch or commit) to check out.");
        }
    }

    /// <summary>
    /// Creates a git worktree of <paramref name="repository"/> at <paramref name="reference"/> under
    /// <paramref name="taskDirectoryPath"/> and returns its absolute path — the value the worker's
    /// WorkingDirectory then points at. Validates first (<see cref="ValidateSpec"/>); a git failure
    /// (an unknown ref, a ref already checked out elsewhere) throws
    /// <see cref="WorktreeProvisioningException"/>.
    /// </summary>
    public static string Provision(string taskDirectoryPath, string repository, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDirectoryPath);
        ValidateSpec(repository, reference);

        var worktreePath = Path.Combine(taskDirectoryPath, WorkspaceDirectoryName);
        var (exitCode, _, stderr) = RunGit(repository, "worktree", "add", worktreePath, reference);
        if (exitCode != 0)
        {
            throw new WorktreeProvisioningException(
                $"Provisioning a worktree of '{reference}' from '{repository}' failed (git worktree add, " +
                $"exit {exitCode}): {stderr.Trim()}");
        }

        return worktreePath;
    }

    /// <summary>
    /// Removes the worktree at <paramref name="worktreePath"/> once the task is Terminal. <b>Never
    /// throws</b> — a teardown fault must not fail a task that has already completed. Two of the three
    /// outcomes are not a removal: a tree carrying <b>uncommitted changes is kept</b> (discarding a
    /// worker's only output is worse than leaving a directory behind), and a removal <b>blocked by a
    /// still-held file</b> — a live build process holding an output, observed repeatedly on this host —
    /// is reported rather than forced. A path that is already gone is reported as removed.
    /// </summary>
    public static WorktreeTeardownResult Teardown(string repository, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new WorktreeTeardownResult(WorktreeTeardownOutcome.Removed, worktreePath, null);
        }

        // `git status --porcelain` prints one line per dirty path and nothing at all when clean.
        var (statusCode, statusOut, _) = RunGit(worktreePath, "status", "--porcelain");
        if (statusCode == 0 && !string.IsNullOrWhiteSpace(statusOut))
        {
            return new WorktreeTeardownResult(
                WorktreeTeardownOutcome.KeptUncommitted, worktreePath,
                "kept: the worktree carries uncommitted changes, and discarding a worker's only output " +
                "is worse than leaving a directory behind");
        }

        var (removeCode, _, removeErr) = RunGit(repository, "worktree", "remove", worktreePath);
        return removeCode == 0
            ? new WorktreeTeardownResult(WorktreeTeardownOutcome.Removed, worktreePath, null)
            : new WorktreeTeardownResult(
                WorktreeTeardownOutcome.RemovalBlocked, worktreePath,
                $"removal did not complete (typically a live build process still holds a file under it): " +
                removeErr.Trim());
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new WorktreeProvisioningException("could not start 'git' — is it installed and on PATH?");

        // Drain both streams concurrently before waiting: reading one to end while the other's buffer
        // fills would deadlock on a chatty git command.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Task.WaitAll(stdout, stderr);
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

/// <summary>What <see cref="WorktreeProvisioner.Teardown"/> did — the three honest outcomes.</summary>
public enum WorktreeTeardownOutcome
{
    /// <summary>The worktree was removed (or was already gone).</summary>
    Removed,

    /// <summary>Uncommitted changes were present, so the worktree was kept rather than discarded.</summary>
    KeptUncommitted,

    /// <summary><c>git worktree remove</c> could not complete — typically a still-held build output.</summary>
    RemovalBlocked,
}

/// <summary>
/// The result of a <see cref="WorktreeProvisioner.Teardown"/> — surfaced, never thrown, so a teardown
/// fault cannot fail a task that already reached Terminal. <paramref name="Detail"/> is null for a
/// clean removal and carries the reason otherwise.
/// </summary>
public sealed record WorktreeTeardownResult(WorktreeTeardownOutcome Outcome, string WorktreePath, string? Detail);
