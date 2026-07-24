using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// <see cref="SessionTurnStubAdapter"/>'s blocking counterpart, for #335: a turn that stays in
/// flight until the test lets it go, so two runs can be held open in one daemon simultaneously.
/// </summary>
/// <remarks>
/// <para>
/// Markers are keyed per session, because the whole point is telling two concurrent runs apart — a
/// shared marker would make "did the right one stop?" unanswerable, which is precisely the question
/// #335 exists to fix.
/// </para>
/// <para>
/// The key comes from a sentinel the test embeds in the session's message, not from
/// <see cref="WorkerInvocation"/>: that record carries no task directory, and its
/// <c>WorkingDirectory</c> is the session's codebase, which two sessions can legitimately share.
/// A test-supplied sentinel is unambiguous and keeps the adapter from inventing a second notion of
/// session identity alongside the one under test.
/// </para>
/// </remarks>
internal sealed class BlockingSessionTurnStubAdapter(string markerDirectory, IReadOnlyList<string> sessionKeys) : IWorkerAdapter
{
    private readonly string _markerDirectory = markerDirectory;
    private readonly IReadOnlyList<string> _sessionKeys = sessionKeys;

    public static string StartedMarkerPath(string markerDirectory, string sessionKey) =>
        Path.Combine(markerDirectory, sessionKey + ".started");

    public static string ReleaseFilePath(string markerDirectory, string sessionKey) =>
        Path.Combine(markerDirectory, sessionKey + ".release");

    public static string FinishedMarkerPath(string markerDirectory, string sessionKey) =>
        Path.Combine(markerDirectory, sessionKey + ".finished");

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        var outputName = contract.ProducedOutputs.Count > 0
            ? contract.ProducedOutputs[0].Name
            : InteractiveSessionMaterializer.DefaultOutputFileName;

        var sessionKey = _sessionKeys.FirstOrDefault(
            key => invocation.PromptTemplate.Contains(key, StringComparison.Ordinal));

        // Fail loudly rather than blocking forever on a marker nothing will ever release: a turn
        // this adapter cannot attribute to a session is a broken test, and a silent hang would
        // present as an unrelated timeout somewhere else entirely.
        if (sessionKey is null)
        {
            throw new InvalidOperationException(
                $"No session sentinel from [{string.Join(", ", _sessionKeys)}] appears in this turn's prompt template, " +
                "so its markers cannot be attributed to a session.");
        }

        Directory.CreateDirectory(_markerDirectory);

        return ShellWorkerCommands.BlockUntilReleased(
            StartedMarkerPath(_markerDirectory, sessionKey),
            ReleaseFilePath(_markerDirectory, sessionKey),
            FinishedMarkerPath(_markerDirectory, sessionKey),
            outputName);
    }
}
