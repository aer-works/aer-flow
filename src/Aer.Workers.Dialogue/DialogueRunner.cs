using System.Text;

namespace Aer.Workers.Dialogue;

/// <summary>
/// Runs the dialogue exchange (M17 Phase 3, #166; generalized to N-party round-robin M23 Phase 1,
/// #270): turns round-robin through <see cref="DialogueWorkerConfig.Participants"/> in list order
/// starting from index 0, writing each turn to <c>transcript.jsonl</c> as it happens and, once the
/// exchange ends, writing <see cref="DialogueWorkerConfig.FinalOutputName"/> per
/// <see cref="Aer.Workers.Dialogue.FinalOutputMode"/> (#736; see that type for what each value writes). Ends on
/// either of two conditions — the ceiling-clamped <see cref="DialogueWorkerConfig.TurnBudget"/> turns
/// having run (see <see cref="DialogueWorkerConfig.HardTurnCeiling"/>), or a participant calling the
/// <c>yield</c> MCP tool during its own turn (#585, decision 0035; see
/// <see cref="DialogueYieldWiring"/>) — and fails the whole exchange (throwing
/// <see cref="DialogueExecutionException"/>, caught by <see cref="Program"/> and mapped to a non-zero
/// process exit) if a vendor CLI exits non-zero or produces no text for a turn.
/// <para>
/// <b>Context threading is a bounded per-turn increment, not the full transcript</b> (decision 0039,
/// superseding M17 Phase 3's original full-transcript design): each turn's prompt is its speaker's
/// <see cref="DialogueParticipant.Preamble"/> plus either <see cref="DialogueWorkerConfig.SeedPrompt"/>
/// (the very first turn of the whole exchange, when nothing has been said yet) or the immediately
/// preceding turn's role and text (every turn after that) — never the accumulated thread. Coherence
/// across a participant's own turns comes from the vendor's own native session continuation instead
/// (<c>--resume</c>/<c>--session-id</c> for <c>claude</c>, <c>--conversation</c> for <c>agy</c>, wired
/// per participant by <see cref="ProcessVendorTurnClient"/> and threaded here via each
/// <see cref="VendorTurnResult.SessionId"/>), the same mechanism <c>Aer.Adapters</c>'s
/// <c>ClaudeWorkerAdapter</c>/<c>AgyWorkerAdapter</c> already use for Conversation/Pipeline. This is
/// what makes a turn's prompt bounded by construction rather than by an argv-length workaround: #579's
/// crash (a long transcript overflowing Windows' ~32,767-character command-line ceiling) and #582's
/// quadratic per-turn cost growth were both symptoms of resending history a resumed vendor session
/// already remembers, not of anything this worker needs to solve itself.
/// </para>
/// <para>
/// <b>The stop signal is a structured MCP tool call, not a text sentinel</b> (#585, decision 0035,
/// superseding M17 Phase 3, #166's original substring match): each participant's vendor CLI invocation
/// is wired, by <see cref="DialogueYieldWiring"/>, to its own instance of <c>Aer.Mcp.Host</c>, which a
/// turn can call the <c>yield</c> tool against. After a turn's process exits, this runner checks that
/// specific participant's own capture file — never another participant's, and never the turn's own
/// text — for a call, giving structural (not text-inferred) attribution of who yielded. The old
/// text-sentinel field itself was retired from <see cref="DialogueWorkerConfig"/> and the authoring
/// surface by #820.
/// </para>
/// </summary>
public sealed class DialogueRunner(IVendorTurnClient turnClient)
{
    public async Task<IReadOnlyList<TranscriptTurn>> RunAsync(
        DialogueWorkerConfig config, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        Directory.CreateDirectory(outputDirectory);

        var effectiveTurnBudget = Math.Min(config.TurnBudget, DialogueWorkerConfig.HardTurnCeiling);
        var turns = new List<TranscriptTurn>(effectiveTurnBudget);
        var wiredParticipants = DialogueYieldWiring.Wire(config.Participants, outputDirectory);

        // One vendor-native session id per participant (decision 0039) -- each side of the exchange
        // is a separate vendor session, never a shared one, so this is keyed by participant index
        // (stable across the whole exchange; see wiredParticipants above), not by role text.
        var sessionIds = new string?[wiredParticipants.Count];

        await using (var transcript = new TranscriptWriter(Path.Combine(outputDirectory, "transcript.jsonl")))
        {
            for (var sequence = 1; sequence <= effectiveTurnBudget; sequence++)
            {
                var participantIndex = (sequence - 1) % wiredParticipants.Count;
                var wired = wiredParticipants[participantIndex];
                var speaker = wired.Participant;
                var prompt = BuildPrompt(speaker, config.SeedPrompt, turns);

                var result = await turnClient.SendTurnAsync(speaker, prompt, sessionIds[participantIndex], cancellationToken).ConfigureAwait(false);
                sessionIds[participantIndex] = result.SessionId;

                if (result.TimedOut)
                {
                    var configuredCeiling = config.TurnTimeout ?? DialogueWorkerConfig.DefaultTurnTimeout;
                    throw new DialogueExecutionException(
                        $"Turn {sequence} ({speaker.Role}/{speaker.Vendor}) timed out after {configuredCeiling}.");
                }

                if (result.ExitCode != 0)
                {
                    var stderrDetail = string.IsNullOrWhiteSpace(result.StandardError)
                        ? string.Empty
                        : $" stderr: {result.StandardError}";
                    throw new DialogueExecutionException(
                        $"Turn {sequence} ({speaker.Role}/{speaker.Vendor}) exited with code {result.ExitCode}.{stderrDetail}");
                }


                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    throw new DialogueExecutionException(
                        $"Turn {sequence} ({speaker.Role}/{speaker.Vendor}) produced no text.");
                }

                var text = result.Text;
                var capture = DialogueYieldWiring.ReadAndConsumeCapture(wired.CaptureFilePath);

                var turn = new TranscriptTurn(sequence, speaker.Role, speaker.Vendor, prompt, text, capture?.Outcome, capture?.Note);
                await transcript.AppendAsync(turn, cancellationToken).ConfigureAwait(false);
                turns.Add(turn);

                if (capture is not null)
                {
                    break;
                }
            }
        }

        var effectiveFinalOutputMode = config.FinalOutputMode ?? DialogueWorkerConfig.DefaultFinalOutputMode;
        var finalOutputText = effectiveFinalOutputMode == FinalOutputMode.Transcript
            ? RenderTranscript(turns)
            : turns[^1].Text;

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, config.FinalOutputName), finalOutputText, cancellationToken)
            .ConfigureAwait(false);

        return turns;
    }

    /// <summary>
    /// The bounded per-turn increment decision 0039 asks for: <paramref name="speaker"/>'s own
    /// preamble plus exactly the turns this speaker has not yet seen — everything after its own
    /// last turn, prefixed by <paramref name="seedPrompt"/> only if it has never spoken (a
    /// participant's resumed vendor session remembers what IT was previously sent, and nothing
    /// else). Never the single last turn alone: with three or more participants that drops every
    /// intervening speaker's turn from the exchange entirely (this branch's review caught it).
    /// Still bounded by construction — in round-robin the unseen window is at most the other
    /// participants' one turn each, independent of how long the exchange runs.
    /// </summary>
    private static string BuildPrompt(DialogueParticipant speaker, string seedPrompt, IReadOnlyList<TranscriptTurn> priorTurns)
    {
        var lastOwnTurnIndex = -1;
        for (var i = priorTurns.Count - 1; i >= 0; i--)
        {
            if (priorTurns[i].Role == speaker.Role)
            {
                lastOwnTurnIndex = i;
                break;
            }
        }

        var increment = new StringBuilder();
        if (lastOwnTurnIndex < 0)
        {
            increment.Append(seedPrompt);
        }

        for (var i = lastOwnTurnIndex + 1; i < priorTurns.Count; i++)
        {
            if (increment.Length > 0)
            {
                increment.Append("\n\n");
            }

            increment.Append(FormatTurnLine(priorTurns[i]));
        }

        return $"{speaker.Preamble}\n\n{increment}";
    }

    /// <summary>
    /// Renders <paramref name="turns"/> for <see cref="Aer.Workers.Dialogue.FinalOutputMode.Transcript"/>
    /// (see that member for the output shape), via the same <see cref="FormatTurnLine"/>
    /// <see cref="BuildPrompt"/> already uses for context-threading.
    /// </summary>
    private static string RenderTranscript(IReadOnlyList<TranscriptTurn> turns)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < turns.Count; i++)
        {
            if (i > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(FormatTurnLine(turns[i]));
        }

        return builder.ToString();
    }

    /// <summary>The single "Role: Text" rendering both <see cref="BuildPrompt"/>'s context-threading and <see cref="RenderTranscript"/>'s final output share.</summary>
    private static string FormatTurnLine(TranscriptTurn turn) => $"{turn.Role}: {turn.Text}";
}
