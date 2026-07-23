namespace Aer.Journeys.Tests;

/// <summary>
/// Which in-process runner drives a journey leg. The two UI runners render the <em>real</em>
/// surface — not a mock, not the daemon API behind it — so a leg passes only when the thing a
/// person actually looks at behaves. <see cref="Attest"/> legs cannot run in process at all and
/// stand on a dated human sign-off (see the runbook).
/// </summary>
public enum Runner
{
    /// <summary>Avalonia view-mounted headless (this project). Desktop legs.</summary>
    DesktopHeadless,

    /// <summary>Flutter widget test (<c>src/Aer.Mobile/test/journeys</c>). Phone legs.</summary>
    PhoneWidget,

    /// <summary>Adapter / Flow-level .NET test, no UI (this project). Engine legs.</summary>
    Engine,

    /// <summary>Real cross-device / live-vendor walk — a human gate, never CI. See the runbook.</summary>
    Attest,
}

/// <summary>How much of a journey's promise is under an executable test today.</summary>
public enum Coverage
{
    /// <summary>A test drives the real surface and currently fails — the promise is not kept yet.</summary>
    DrivenRed,

    /// <summary>A test drives the real surface and currently passes — the promise is kept for this leg.</summary>
    DrivenGreen,

    /// <summary>The surface exists and is driveable, but no test is written yet (a fast-follow).</summary>
    Pending,

    /// <summary>Not in-process testable; stands on a human sign-off.</summary>
    HumanAttested,
}

/// <summary>One surface a journey crosses, and how #313 covers it.</summary>
/// <param name="Surface">The human-readable surface this leg is, e.g. "desktop first-run".</param>
/// <param name="Runner">Which runner drives it.</param>
/// <param name="Coverage">Its coverage state in this repo today.</param>
/// <param name="Note">What the covering test asserts, or why it's pending / attested.</param>
public sealed record JourneyLeg(string Surface, Runner Runner, Coverage Coverage, string Note);

/// <summary>
/// One product journey (<c>spec/journeys.md</c>) as the harness sees it: its stable id, its title,
/// and its declared status <em>verbatim</em> from the spec. <see cref="ReconcileTests"/> asserts
/// this registry and <c>spec/journeys.md</c> never drift — a status edited in one place and not the
/// other breaks the reconcile gate (#314 extends this to also compare the declared status against
/// the journey tests' actual pass/fail).
/// </summary>
/// <param name="Id">The journey's stable id, e.g. "J6". Matches the <c>[Trait("Journey", …)]</c> on its tests.</param>
/// <param name="Title">The header title in the spec, after the em dash.</param>
/// <param name="DeclaredStatus">The spec's <c>**Status:**</c> line, byte-for-byte.</param>
/// <param name="Legs">The surfaces it crosses and how each is covered here.</param>
/// <param name="Serves">The issues the journey serves, for cross-reference.</param>
public sealed record Journey(
    string Id,
    string Title,
    string DeclaredStatus,
    IReadOnlyList<JourneyLeg> Legs,
    IReadOnlyList<int> Serves);

/// <summary>
/// The journey registry — the code-side source of truth the reconcile gate checks against
/// <c>spec/journeys.md</c>. Adding a journey means adding it here and to the spec in the same
/// change; that coupling is the point.
/// </summary>
public static class Journeys
{
    /// <summary>The trait key every journey test carries, so <c>--filter Journey=J6</c> selects one.</summary>
    public const string TraitKey = "Journey";

    public static readonly IReadOnlyList<Journey> All =
    [
        new("J1", "Start work on the desktop, approve it from your phone", "Fails — automated",
        [
            new("desktop start → daemon → paired phone approve", Runner.Attest, Coverage.HumanAttested,
                "Cross-device: the phone's inbox scope (#335) and the desk→phone broadcast need a real paired device."),
        ], [335, 319, 330]),

        new("J2", "Open a folder, talk to an agent, and grow the room without leaving the chat",
            "Partial — automated + live",
        [
            new("desktop room (spawn / host / gate)", Runner.DesktopHeadless, Coverage.Pending,
                "The room model (decisions 0001/0008/0009) isn't built yet; the spawn/host/gate legs get driven as they land."),
            new("live-vendor review quality", Runner.Attest, Coverage.HumanAttested,
                "A review's live quality needs authenticated vendors — a live-smoke check."),
        ], [333, 335, 340]),

        new("J3", "Come back after a day and immediately see what needs you", "Fails — automated",
        [
            new("desktop inbox / cards (Home)", Runner.DesktopHeadless, Coverage.Pending,
                "Home already segregates waiting/running/finished and labels failed on the card; the remaining red edge (#355 summary counts a failed task as \"finished\") is a fast-follow view-mounted assertion."),
            new("phone inbox — running work still reads \"nothing waiting\" (#337)", Runner.PhoneWidget, Coverage.Pending,
                "InboxScreen builds its own DaemonClient from stored credentials, so it needs a client-injection seam before a widget test can drive it. Tracked with #337."),
        ], [337, 355, 334]),

        new("J4", "Pair a phone from scratch on an ordinary network", "Partial — human pairing",
        [
            new("fresh-device LAN pairing", Runner.Attest, Coverage.HumanAttested,
                "A physical phone on a real LAN, per the pairing runbook (#347, #349)."),
        ], [347, 349, 346]),

        new("J5", "Start the same piece of work from either surface and see it on both", "Fails — automated",
        [
            new("desktop ↔ daemon ↔ phone broadcast", Runner.Attest, Coverage.HumanAttested,
                "The broadcast path (#330, #348) is a cross-process/device concern — a real second surface."),
        ], [330, 348, 335]),

        new("J6", "Deny a tool and have it actually blocked", "Fails — automated · safety",
        [
            new("engine — grant enforcement at the dispatch boundary", Runner.Engine, Coverage.DrivenRed,
                "J6_DeniedToolEnforcementTests: a shell-denied grant must produce a dispatch that actively denies the tool (or fail closed), not merely omit it from --allowedTools. Red per decision 0004 / #331."),
            new("live worker actually refuses the tool", Runner.Attest, Coverage.HumanAttested,
                "The end-to-end refusal (worker attempts the tool, is blocked, it's recorded) needs a live vendor — a smoke check."),
        ], [331]),

        new("J7", "Lose the connection and get back to work", "Fails — automated + human",
        [
            new("phone disconnected state + recovery action", Runner.PhoneWidget, Coverage.Pending,
                "InboxScreen renders _connectionError with a Reconnect button, but isn't client-injectable yet; the truthful-state assertion waits on the same seam as J3-phone (#346, #349)."),
            new("real network-drop walk", Runner.Attest, Coverage.HumanAttested,
                "A real device losing the daemon, per the recovery runbook."),
        ], [346, 347, 349]),

        new("J8", "Open it for the first time and know what to do", "Fails — automated",
        [
            new("desktop first-run empty state", Runner.DesktopHeadless, Coverage.DrivenGreen,
                "J8_DesktopFirstRunTests: an empty Home renders \"No tasks yet.\" with real Start-from-template / Create-workflow actions (#190), not a blank wall. Green — this leg is kept."),
            new("phone empty task list dead-ends (#337)", Runner.PhoneWidget, Coverage.DrivenRed,
                "j8_first_run_phone_test: an empty TasksScreen shows only \"No tasks or sessions yet.\" text with no primary action. Red until it offers a real next step."),
        ], [337, 338, 339]),

        new("J9", "See what you're spending across every vendor", "Fails — automated",
        [
            new("cross-vendor usage view", Runner.DesktopHeadless, Coverage.Pending,
                "No usage surface exists yet (#360, #338); the aggregation/display leg gets driven when the surface lands."),
        ], [360, 338]),
    ];
}
