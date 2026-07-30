namespace Aer.Cli;

/// <summary>
/// Parsed arguments for <c>aer status</c> (#730): a read-only projection of a task directory's
/// recorded events, never a mutation surface — so unlike every other command in this namespace it
/// takes no <c>--bindings</c> file at all (it never resolves a worker binding, spec §730's own
/// scope) and no <c>--workflow-id</c> (nothing here dispatches, so there is nothing to label).
/// </summary>
/// <param name="TaskDirectoryPath">
/// An already-started task's durable state directory. <c>aer status</c> never binds a fresh
/// snapshot the way <c>aer run</c> does — it only ever reads one that already exists.
/// </param>
/// <param name="Follow">
/// When set, keep polling <c>flow.jsonl</c> for new events after printing the current state,
/// printing each as it lands, until the workflow reaches a terminal state or the caller cancels.
/// </param>
public sealed record StatusOptions(string TaskDirectoryPath, bool Follow = false);
