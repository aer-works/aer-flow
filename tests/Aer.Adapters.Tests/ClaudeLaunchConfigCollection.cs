namespace Aer.Adapters.Tests;

/// <summary>
/// Serialises the test classes that touch the one shared <c>claude-settings.json</c> under the
/// worker-launch directory. Every test assembly gets a single <c>AER_HOME</c> (see
/// <c>tests/Shared/AerHomeRedirect.cs</c>), so every class in this assembly resolves against the same
/// file, and xUnit runs classes in parallel by default.
/// </summary>
/// <remarks>
/// <para>
/// <b>#667 changed why this is needed, and did not remove the need.</b> Before it, the hazard was the
/// adapter's own rewrite-on-every-resolve: two classes resolving at once could catch each other
/// mid-<c>File.Move</c>. That is gone — a resolve whose content already matches performs no write at
/// all. #667's "done when" asked for this type to be deleted on that basis, and deleting it made the
/// suite red for a different reason, which is why it is still here.
/// </para>
/// <para>
/// The surviving hazard is the tests that write the shared file <i>directly</i> to create drift for
/// the adapter to correct (<see cref="ClaudeWorkerAdapterTests"/>'s stale-content test). A resolve
/// still opens the destination on every call — for a read now rather than a rename — and a
/// hand-written <c>File.WriteAllText</c> contends with that. Whether the read contends more or less
/// than the rename did was not measured; what was measured is that removing this type turned the
/// stale-content path red. Serialising the classes is the cheap fix; giving those tests their own
/// <c>AER_HOME</c> would be the real one, and needs its own issue rather than a drive-by here.
/// </para>
/// <para>
/// This makes the suite deterministic; it has never made the adapter concurrency-safe, and still
/// does not. What bounds the production race is the skipped write, measured in
/// <see cref="LaunchConfigRewriteTests"/>.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClaudeLaunchConfigCollection
{
    public const string Name = "claude-launch-config";
}
