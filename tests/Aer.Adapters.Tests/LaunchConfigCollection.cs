namespace Aer.Adapters.Tests;

/// <summary>
/// Serialises every test class that resolves an adapter which writes a launch config —
/// <c>claude-settings.json</c> or <c>.agents/hooks.json</c>. The assembly shares one
/// <c>AER_HOME</c> (<c>tests/Shared/AerHomeRedirect.cs</c>), so they all resolve to the same files.
/// </summary>
/// <remarks>
/// <para>
/// <b>#667 asked for this to be deleted and the measurement refused.</b> Without it, six runs of this
/// assembly gave five failures across three classes, every one an <c>UnauthorizedAccessException</c>
/// out of <see cref="AtomicLaunchConfigWriter"/>'s <c>File.Move</c> with its attempts spent. #667's
/// skip makes every resolve after the first a no-op, but at assembly start there is no file, so every
/// class racing to its own first resolve is a writer at once — and that budget is exhaustible (#682).
/// </para>
/// <para>
/// Eight classes rather than the original two: three of the failures were in classes never covered.
/// <see cref="GeminiWorkerAdapterTests"/> is included on the same mechanism rather than its own
/// observed failure — it writes the other launch config through the same writer.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LaunchConfigCollection
{
    public const string Name = "launch-config";
}
