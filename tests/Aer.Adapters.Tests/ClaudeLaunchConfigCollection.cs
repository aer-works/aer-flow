namespace Aer.Adapters.Tests;

/// <summary>
/// Serialises the test classes that call <c>ClaudeWorkerAdapter.Resolve</c>. Every call rewrites one
/// shared <c>claude-settings.json</c> under the worker-launch directory, so two classes resolving in
/// parallel — xUnit's default across classes — can catch each other mid-write and fail with a file
/// lock rather than an assertion.
/// </summary>
/// <remarks>
/// This makes the suite deterministic; it does not make the adapter concurrency-safe. The same
/// unsynchronised read-modify-write is reachable in production wherever two bindings resolve at once,
/// which is #667 rather than something to paper over here.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClaudeLaunchConfigCollection
{
    public const string Name = "claude-launch-config";
}
