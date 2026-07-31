namespace Aer.Workers.Dialogue;

/// <summary>
/// Raised by <see cref="ProcessVendorTurnClient.SendTurnAsync(DialogueParticipant, string, string?, CancellationToken)"/>
/// when a substituted argument exceeds <see cref="ProcessVendorTurnClient.MaxArgumentLength"/> (decision
/// 0039, originally asked for by #581). Bounded per-turn prompts should never approach this threshold —
/// it is a defensive guard against an unanticipated outlier, not the primary fix, so hitting it is
/// always a loud, typed failure rather than a silent platform-level crash (#579's original argv-limit
/// failure mode).
/// </summary>
public sealed class DialogueArgumentTooLargeException : Exception
{
    public DialogueArgumentTooLargeException(string message)
        : base(message)
    {
    }

    public DialogueArgumentTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
