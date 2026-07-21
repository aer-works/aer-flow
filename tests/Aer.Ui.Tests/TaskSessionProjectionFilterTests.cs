using Aer.Adapters;

namespace Aer.Ui.Tests;

/// <summary>
/// Issue #262 follow-up: before <see cref="TaskSession.ShouldApplyProjectionPush"/> existed,
/// <c>ReceiveWebSocketDataAsync</c> applied every incoming <c>/api/ws</c> push unconditionally,
/// regardless of which directory it was for — so one client opening a different task on the same
/// daemon silently corrupted every other connected client's view. These tests exercise the
/// filtering decision directly, the same split <see cref="ChatViewModelTests"/> draws for pure
/// logic that doesn't need Avalonia or a live daemon.
/// </summary>
public class TaskSessionProjectionFilterTests
{
    private static TaskSession NewSession() => new(
        new LocalUiConfigurationStore(Path.Combine(Path.GetTempPath(), $"aer-ui-session-filter-{Guid.NewGuid():N}", "recent-task-directories.json")),
        new Dictionary<string, IWorkerAdapter>(),
        new MainWindowViewModel(),
        bindingsFilePathProvider: () => null,
        mutationStarted: () => { },
        mutationFailed: () => { },
        reopenTaskAsync: (_, _) => Task.CompletedTask);

    [Fact]
    public void AFreshSessionAdoptsTheFirstPushItSeesAsItsOwnCurrentDirectory()
    {
        var session = NewSession();

        var applied = session.ShouldApplyProjectionPush("/tmp/task-a");

        Assert.True(applied);
        Assert.Equal("/tmp/task-a", session.CurrentTaskDirectoryPath);
    }

    [Fact]
    public void OnceSeededAPushForADifferentDirectoryIsRejected()
    {
        var session = NewSession();
        session.ShouldApplyProjectionPush("/tmp/task-a");

        var applied = session.ShouldApplyProjectionPush("/tmp/task-b");

        Assert.False(applied);
        Assert.Equal("/tmp/task-a", session.CurrentTaskDirectoryPath);
    }

    [Fact]
    public void APushMatchingThisClientsOwnOpenDirectoryIsStillApplied()
    {
        var session = NewSession();
        session.SetCurrentTaskDirectory("/tmp/task-a");

        var applied = session.ShouldApplyProjectionPush("/tmp/task-a");

        Assert.True(applied);
        Assert.Equal("/tmp/task-a", session.CurrentTaskDirectoryPath);
    }

    [Fact]
    public void ExplicitlyOpeningADifferentDirectoryStartsAcceptingPushesForItInstead()
    {
        var session = NewSession();
        session.ShouldApplyProjectionPush("/tmp/task-a");

        // This client's own action (SetCurrentTaskDirectory, as OpenAsync/RunAsync call) reassigns
        // which directory it cares about -- unlike another client's action, this one must win.
        session.SetCurrentTaskDirectory("/tmp/task-b");

        Assert.True(session.ShouldApplyProjectionPush("/tmp/task-b"));
        Assert.False(session.ShouldApplyProjectionPush("/tmp/task-a"));
    }
}
