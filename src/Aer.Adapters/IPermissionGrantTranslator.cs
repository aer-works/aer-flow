namespace Aer.Adapters;

/// <summary>
/// Opt-in capability an <see cref="IWorkerAdapter"/> implements when its vendor CLI's permission
/// vocabulary can be driven from a structured <see cref="PermissionGrant"/> — the M21 Phase 1
/// builder UI in Aer.Ui's bindings editor checks for this interface before offering the checkbox
/// builder for a given adapter, falling back to the raw "Advanced" text field when an adapter
/// (e.g. <see cref="DialogueWorkerAdapter"/>, which never shells out to a permission-gated vendor
/// CLI at all) doesn't implement it. Kept separate from <see cref="IWorkerAdapter"/> itself rather
/// than added to that interface, so every existing/future adapter with no vendor permission
/// vocabulary to translate is never forced to implement a no-op method.
/// </summary>
public interface IPermissionGrantTranslator
{
    /// <summary>
    /// Attempts to translate <paramref name="grant"/> into this adapter's vendor-native permission
    /// flag value. <see cref="IWorkerAdapter.Resolve"/> calls this same logic internally when an
    /// invocation carries a <see cref="WorkerInvocation.PermissionGrant"/> — this method also exists
    /// standalone so the builder UI can validate a grant before Save, without needing a full
    /// <see cref="Aer.Flow.Domain.WorkerContract"/> to call <c>Resolve</c> with.
    /// <para>
    /// Must refuse (return <see langword="false"/>) rather than approximate whenever the requested
    /// grant cannot be expressed exactly — granting more than requested is as much a bug here as
    /// granting less (Adapter Isolation, CLAUDE.md rule #2, cuts both ways).
    /// </para>
    /// </summary>
    bool TryTranslatePermissionGrant(PermissionGrant grant, out string? resolvedValue, out string? gapReason);
}
