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
    public static string GeometryKeyFor(StepStatus status) => status switch
    {
        StepStatus.Running => "Icon.Ring",
        StepStatus.Succeeded => "Icon.Check",
        StepStatus.Failed or StepStatus.Rejected => "Icon.Cross",
        StepStatus.Paused => "Icon.Dot",
        _ => "Icon.Dot", // Pending, Cancelled: idle
    };

    public static string ColorKeyFor(StepStatus status) => status switch
    {
        StepStatus.Running => "Status.Running",
        StepStatus.Succeeded => "Status.Succeeded",
        StepStatus.Failed or StepStatus.Rejected => "Status.Failed",
        StepStatus.Paused => "Status.NeedsYou",
        StepStatus.Cancelled => "Status.Idle",
        _ => "Status.Idle", // Pending
    };

    public static string GeometryKeyFor(TaskCardStatus status) => status switch
    {
        TaskCardStatus.Running => "Icon.Ring",
        TaskCardStatus.NeedsYou => "Icon.Dot",
        TaskCardStatus.Finished => "Icon.Check",
        TaskCardStatus.Failed => "Icon.Cross",
        _ => "Icon.Refresh", // Unavailable: §3's stale-list state
    };

    public static string ColorKeyFor(TaskCardStatus status) => status switch
    {
        TaskCardStatus.Running => "Status.Running",
        TaskCardStatus.NeedsYou => "Status.NeedsYou",
        TaskCardStatus.Finished => "Status.Succeeded",
        TaskCardStatus.Failed => "Status.Failed",
        _ => "Status.Idle", // Unavailable
    };
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
