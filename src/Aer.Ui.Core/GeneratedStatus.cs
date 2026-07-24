// GENERATED FILE — DO NOT EDIT.
// Source: design/tokens.json
// Regenerate: pixi run tokens
//
// Hand edits are reverted by the next regeneration and fail CI in the meantime
// (Aer.Architecture.Tests). Change the token file instead.

namespace Aer.Ui.Core;

/// <summary>The five states from #334's split — the vocabulary every status-rendering surface uses.</summary>
public enum AerStatus
{
    Working,
    NeedsInput,
    ReadyForReview,
    Finished,
    Failed,
}

/// <summary>
/// Decision 0006: a status must never be conveyed by hue alone, so every state carries a mark
/// and a word. Any surface that renders <see cref="ColorResourceKey"/> must also render
/// <see cref="MarkResourceKey"/> and <see cref="Label"/> — colour is the third channel, never
/// the only one.
/// </summary>
public static class AerStatusPresentation
{
    /// <summary>
    /// The resource key of the <c>StreamGeometry</c> that draws this status's mark, defined in
    /// <c>Aer.Ui/Theme/Icons.axaml</c>. A shape rather than a character: the shipped faces do not
    /// cover the codepoints originally chosen, and between them carry no checkmark and no cross
    /// at all, so a text glyph cannot express this set on both platforms (#458).
    /// </summary>
    public static string MarkResourceKey(this AerStatus status) => status switch
    {
        AerStatus.Working => "Icon.Ring",
        AerStatus.NeedsInput => "Icon.Diamond",
        AerStatus.ReadyForReview => "Icon.Page",
        AerStatus.Finished => "Icon.Check",
        AerStatus.Failed => "Icon.Cross",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
    };

    /// <summary>The status in words — rendered alongside the mark, never replaced by it.</summary>
    public static string Label(this AerStatus status) => status switch
    {
        AerStatus.Working => "Working",
        AerStatus.NeedsInput => "Needs input",
        AerStatus.ReadyForReview => "Ready for review",
        AerStatus.Finished => "Finished",
        AerStatus.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
    };

    /// <summary>
    /// The key of this status's <c>Color</c> in the generated theme dictionaries. A colour, not a
    /// brush: it resolves per theme variant, so a consumer must look it up against the live
    /// variant rather than through the theme-oblivious overload (the washed-out DAG boxes of
    /// #204/#205 were exactly that mistake).
    /// </summary>
    public static string ColorResourceKey(this AerStatus status) => status switch
    {
        AerStatus.Working => "StatusWorkingColor",
        AerStatus.NeedsInput => "StatusNeedsInputColor",
        AerStatus.ReadyForReview => "StatusReadyForReviewColor",
        AerStatus.Finished => "StatusFinishedColor",
        AerStatus.Failed => "StatusFailedColor",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
    };
}
