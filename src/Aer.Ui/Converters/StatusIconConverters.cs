using System.Globalization;
using Aer.Flow.Domain;
using Aer.Ui.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Aer.Ui.Converters;

/// <summary>
/// Post-M19 design review (issue #206): design-language.md's status→icon table, materialized as
/// one mapping every status-rendering surface goes through, so the same status always draws the
/// same glyph ("color + icon + word, never color alone" — <see cref="TaskCardViewModel"/>'s own
/// comment named this intent; nothing consumed it until now).
/// </summary>
internal static class StatusIconMap
{
    /// <summary>
    /// #458: <see cref="StepStatus.Paused"/> drew <c>Icon.Dot</c> — the same mark as Pending and
    /// Cancelled — so the one state that means "this is waiting on you" was shaped identically to
    /// "nothing is happening here", leaving colour as the only difference. That is the failure
    /// decision 0006's rule exists to prevent, and it was live.
    /// </summary>
    /// <remarks>
    /// <see cref="StepStatus"/> alone cannot distinguish a pause awaiting a *reply* from one awaiting
    /// a *review* — that lives in the step's <see cref="Aer.Flow.Domain.PausePointKind"/>, which this
    /// converter is not given. It therefore draws the reply mark for both, which is right for the
    /// common case and no worse than the single dot it replaces. #336 replaces this mapping wholesale
    /// with <c>AerStatus</c>, which carries the distinction.
    /// </remarks>
    public static string GeometryKeyFor(StepStatus status) => status switch
    {
        StepStatus.Running => "Icon.Ring",
        StepStatus.Succeeded => "Icon.Check",
        StepStatus.Failed or StepStatus.Rejected => "Icon.Cross",
        StepStatus.Paused => "Icon.Bubble",
        // #461: cancelled is no longer "idle". Stopping something on purpose is an outcome, and
        // rendering it as the pending dot said nothing happened.
        StepStatus.Cancelled => "Icon.Dash",
        _ => "Icon.Dot", // Pending: genuinely not started
    };

    /// <summary>
    /// Whether a status's mark is painted solid rather than stroked (#461). Delegates to the
    /// generated table so the fill decision is stated once, in <c>design/tokens.json</c> — the call
    /// sites used to set <c>Stroke</c> and never <c>Fill</c>, so a mark authored as a solid on mobile
    /// rendered as an outline here.
    /// </summary>
    public static bool IsFilled(string geometryKey) =>
        Enum.GetValues<AerStatus>().Any(status => status.MarkResourceKey() == geometryKey && status.MarkIsFilled());

    public static string ColorKeyFor(StepStatus status) => status switch
    {
        StepStatus.Running => "Status.Running",
        StepStatus.Succeeded => "Status.Succeeded",
        StepStatus.Failed or StepStatus.Rejected => "Status.Failed",
        StepStatus.Paused => "Status.NeedsYou",
        StepStatus.Cancelled => "Status.Idle",
        _ => "Status.Idle", // Pending
    };

    /// <summary>Same #458 correction as the <see cref="StepStatus"/> overload above: NeedsYou was a dot.</summary>
    public static string GeometryKeyFor(TaskCardStatus status) => status switch
    {
        TaskCardStatus.Running => "Icon.Ring",
        TaskCardStatus.NeedsYou => "Icon.Bubble",
        TaskCardStatus.Finished => "Icon.Check",
        TaskCardStatus.Failed => "Icon.Cross",
        TaskCardStatus.Cancelled => "Icon.Dash",
        // #461: the stale-list state gets its own mark. It previously borrowed Icon.Refresh, the
        // Retry *action*'s glyph — a state wearing an action's icon invites clicking it.
        _ => "Icon.Slashed", // Unavailable: §3's stale-list state
    };

    public static string ColorKeyFor(TaskCardStatus status) => status switch
    {
        TaskCardStatus.Running => "Status.Running",
        TaskCardStatus.NeedsYou => "Status.NeedsYou",
        TaskCardStatus.Finished => "Status.Succeeded",
        TaskCardStatus.Failed => "Status.Failed",
        // Cancelled shares the muted brush rather than earning a hue: it is a quiet outcome, and
        // colouring it like a failure is exactly the alarm #461 exists to remove.
        TaskCardStatus.Cancelled => "Status.Idle",
        _ => "Status.Idle", // Unavailable
    };
}

/// <summary>
/// Status → the mark's fill brush, or <c>null</c> where the mark is stroked (#461). Paired with
/// <see cref="StatusToIconGeometryConverter"/> at every call site: a <c>Path</c> that sets only
/// <c>Stroke</c> renders a closed shape as an outline, so before this a mark authored solid drew
/// solid on the phone and hollow on the desktop. The decision now comes from the token file.
/// </summary>
public sealed class StatusToIconFillConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var geometryKey = value switch
        {
            StepStatus stepStatus => StatusIconMap.GeometryKeyFor(stepStatus),
            TaskCardStatus cardStatus => StatusIconMap.GeometryKeyFor(cardStatus),
            _ => null,
        };

        if (geometryKey is null || !StatusIconMap.IsFilled(geometryKey) || Application.Current is not { } app)
        {
            return null;
        }

        var colorKey = value switch
        {
            StepStatus stepStatus => StatusIconMap.ColorKeyFor(stepStatus),
            TaskCardStatus cardStatus => StatusIconMap.ColorKeyFor(cardStatus),
            _ => null,
        };

        // Same live-variant lookup as the stroke converter below — the theme-oblivious overload is
        // what washed out the DAG boxes in #204/#205.
        return colorKey is not null && app.TryFindResource(colorKey, app.ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Status → glyph. Icon geometries live outside <c>ThemeDictionaries</c> (one shape, not
/// themed), so an ordinary theme-oblivious resource lookup is safe here — unlike the brush lookup
/// below.</summary>
public sealed class StatusToIconGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StepStatus stepStatus => StatusIconMap.GeometryKeyFor(stepStatus),
            TaskCardStatus cardStatus => StatusIconMap.GeometryKeyFor(cardStatus),
            _ => null,
        };

        return key is null ? null : Application.Current?.FindResource(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Status → the same brush the DAG node/border for that status already uses. Explicit
/// <see cref="ThemeVariant"/> argument, not the theme-oblivious <c>FindResource(key)</c> overload
/// that caused the washed-out DAG boxes (issue #204/#205) — <c>Application.Current.ActualThemeVariant</c>
/// is the live variant the running app renders in.</summary>
public sealed class StatusToIconBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StepStatus stepStatus => StatusIconMap.ColorKeyFor(stepStatus),
            TaskCardStatus cardStatus => StatusIconMap.ColorKeyFor(cardStatus),
            _ => null,
        };

        if (key is null || Application.Current is not { } app)
        {
            return Brushes.Transparent;
        }

        return app.TryFindResource(key, app.ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
