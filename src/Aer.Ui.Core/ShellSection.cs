namespace Aer.Ui.Core;

/// <summary>The shell's destinations — docs/ux/information-architecture.md's flat navigation: Home, Task, Author (M19 Phase 2, #187), Remote (M21 Phase 3, #234), Chat (M24 Phase 1, #262) for interactive sessions, plus Tasks (M24 Phase 5, #278) — the fleet management view, distinct from Home's capped recents cards.</summary>
public enum ShellSection
{
    Home,
    Task,
    Author,
    Remote,
    Chat,
    Tasks,
}
