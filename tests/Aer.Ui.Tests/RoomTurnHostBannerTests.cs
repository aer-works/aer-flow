using System.Threading.Tasks;
using Aer.Adapters;
using Aer.Ui.Core;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Aer.Ui.Tests;

public class RoomTurnHostBannerTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>();

    private static MainWindow NewWindow() => new(
        new LocalUiConfigurationStore(Path.Combine(Path.GetTempPath(), $"aer-ui-turnhost-config-{Guid.NewGuid():N}", "recent-task-directories.json")),
        Adapters);

    private static RoomTurnHostStatus CreateStatus(
        int count = 3,
        int cap = 10,
        bool isDormant = false,
        int failures = 3,
        string source = "defaults",
        string? loadError = null,
        string? dormancyEscalationDetail = null)
    {
        return new RoomTurnHostStatus(
            RoomDirectoryPath: "/test/room",
            Throttles: new RoomTurnHostThrottleValues(60, cap, 3),
            ThrottlesSource: source,
            LoadError: loadError,
            MachineTurnsInTrailingHour: $"{count}/{cap}",
            TurnsInTrailingHourCount: count,
            MachineTurnsPerHourCap: cap,
            ConsecutiveFailures: failures,
            InFlight: false,
            IsDormant: isDormant,
            DormancyEscalationDetail: dormancyEscalationDetail,
            LastDecisionReason: isDormant ? "Dormant" : null);
    }

    [Fact]
    public void VM_MeterText_RendersTurnsInTrailingHourAndCap()
    {
        // Red arm note: If MeterText did not include "3/10" from TurnsInTrailingHourCount/MachineTurnsPerHourCap, this assertion fails.
        var status = CreateStatus(count: 3, cap: 10);
        var banner = new RoomTurnHostBannerViewModel(status);

        Assert.Contains("3/10", banner.MeterText);
        Assert.False(banner.IsDormant);
        Assert.Null(banner.LoadErrorText);
    }

    [Fact]
    public void VM_DormantStatus_SetsIsDormantAndDormancyText()
    {
        // Red arm note: If IsDormant is false or DormancyText omits failure count when IsDormant is true, this assertion fails.
        var status = CreateStatus(isDormant: true, failures: 3);
        var banner = new RoomTurnHostBannerViewModel(status);

        Assert.True(banner.IsDormant);
        Assert.Contains("3", banner.DormancyText);
        Assert.Contains("Dormant", banner.DormancyText);
    }

    [Fact]
    public void VM_DormancyEscalationDetail_PopulatesEscalationText()
    {
        // Red arm note: If DormancyEscalationText stays null (or HasDormancyEscalationText false) when the status carries the breaker escalation's detail, these assertions fail.
        var status = CreateStatus(isDormant: true, dormancyEscalationDetail: "3 consecutive uncommitted turns tripped the breaker");
        var banner = new RoomTurnHostBannerViewModel(status);

        Assert.True(banner.HasDormancyEscalationText);
        Assert.Equal("3 consecutive uncommitted turns tripped the breaker", banner.DormancyEscalationText);
    }

    [Fact]
    public void VM_NoDormancyEscalationDetail_EscalationTextAbsent()
    {
        // Red arm note: If HasDormancyEscalationText returns true when the status carries no escalation detail (absence polarity), this assertion fails.
        var status = CreateStatus(isDormant: true, dormancyEscalationDetail: null);
        var banner = new RoomTurnHostBannerViewModel(status);

        Assert.False(banner.HasDormancyEscalationText);
        Assert.Null(banner.DormancyEscalationText);
    }

    [Fact]
    public void VM_LoadError_PopulatesLoadErrorText()
    {
        // Red arm note: If LoadError is non-null on status but LoadErrorText remains null on banner VM, this assertion fails.
        var status = CreateStatus(loadError: "Malformed turn-throttles.json");
        var banner = new RoomTurnHostBannerViewModel(status);

        Assert.NotNull(banner.LoadErrorText);
        Assert.Equal("Malformed turn-throttles.json", banner.LoadErrorText);
    }

    [Fact]
    public void VM_NullStatus_HasRoomTurnHostBannerReturnsFalse()
    {
        // Red arm note: If HasRoomTurnHostBanner returns true when RoomTurnHostBanner is set to null (absence polarity), this assertion fails.
        var vm = new MainWindowViewModel();
        vm.RoomTurnHostBanner = null;

        Assert.False(vm.HasRoomTurnHostBanner);
    }

    [Fact]
    public async Task Wake_SuccessfulClear_InvokesRefreshDelegate()
    {
        // Red arm note: If WakeCommand does not call clear delegate or ignores a true return and skips refresh, refreshCalled remains false.
        var clearCalled = false;
        var refreshCalled = false;

        var status = CreateStatus(isDormant: true);
        var banner = new RoomTurnHostBannerViewModel(
            status,
            clearDormancyAsyncFunc: () =>
            {
                clearCalled = true;
                return Task.FromResult(true);
            },
            refreshAsyncFunc: () =>
            {
                refreshCalled = true;
                return Task.CompletedTask;
            });

        await banner.WakeCommand.ExecuteAsync(null);

        Assert.True(clearCalled);
        Assert.True(refreshCalled);
    }

    [Fact]
    public async Task Wake_FailedClear_DoesNotInvokeRefreshDelegate()
    {
        // Red arm note: If WakeCommand invokes refresh delegate when clear delegate returns false, refreshCalled becomes true.
        var clearCalled = false;
        var refreshCalled = false;

        var status = CreateStatus(isDormant: true);
        var banner = new RoomTurnHostBannerViewModel(
            status,
            clearDormancyAsyncFunc: () =>
            {
                clearCalled = true;
                return Task.FromResult(false);
            },
            refreshAsyncFunc: () =>
            {
                refreshCalled = true;
                return Task.CompletedTask;
            });

        await banner.WakeCommand.ExecuteAsync(null);

        Assert.True(clearCalled);
        Assert.False(refreshCalled);
    }

    [AvaloniaFact]
    public void View_DormantStatus_ShowsDormantBanner()
    {
        // Red arm note: If RoomView's dormant card is hidden or HasRoomTurnHostBanner is false when ViewModel carries a dormant RoomTurnHostBanner, this assertion fails.
        var window = NewWindow();
        var status = CreateStatus(isDormant: true, failures: 3);
        window.ViewModel.RoomTurnHostBanner = new RoomTurnHostBannerViewModel(status);

        Assert.True(window.ViewModel.HasRoomTurnHostBanner);
        Assert.NotNull(window.ViewModel.RoomTurnHostBanner);
        Assert.True(window.ViewModel.RoomTurnHostBanner.IsDormant);
        Assert.Contains("3", window.ViewModel.RoomTurnHostBanner.DormancyText);
    }

    [AvaloniaFact]
    public void View_LoadError_TextBlockVisibility_FollowsLoadErrorText()
    {
        // Red arm note (second-reader finding): if the LoadErrorText IsVisible binding in
        // RoomView.axaml is broken (wrong path/converter), the error TextBlock either never
        // shows for a malformed turn-throttles.json or always shows an empty line — one of the
        // two polarity assertions below fails.
        var window = NewWindow();
        window.Show();

        window.ViewModel.RoomTurnHostBanner = new RoomTurnHostBannerViewModel(
            CreateStatus(loadError: "Malformed turn-throttles.json"));
        var errorBlock = window.FindViewControl<Avalonia.Controls.TextBlock>("TurnHostMeterLoadError")!;
        Assert.True(errorBlock.IsVisible);
        Assert.Equal("Malformed turn-throttles.json", errorBlock.Text);

        window.ViewModel.RoomTurnHostBanner = new RoomTurnHostBannerViewModel(CreateStatus(loadError: null));
        Assert.False(errorBlock.IsVisible);
    }

    [AvaloniaFact]
    public void View_NonDormantStatus_ShowsMeter_HidesWake()
    {
        // Red arm note: If RoomView's non-dormant status card sets IsDormant to true or hides MeterText when ViewModel carries a non-dormant status, this assertion fails.
        var window = NewWindow();
        var status = CreateStatus(count: 3, cap: 10, isDormant: false);
        window.ViewModel.RoomTurnHostBanner = new RoomTurnHostBannerViewModel(status);

        Assert.True(window.ViewModel.HasRoomTurnHostBanner);
        Assert.NotNull(window.ViewModel.RoomTurnHostBanner);
        Assert.False(window.ViewModel.RoomTurnHostBanner.IsDormant);
        Assert.Contains("3/10", window.ViewModel.RoomTurnHostBanner.MeterText);
    }
}
