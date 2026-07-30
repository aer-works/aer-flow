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
/// <b>Context threading is the full transcript so far</b>, not a sliding window: each turn's prompt
/// is its speaker's <see cref="DialogueParticipant.Preamble"/>, the exchange's
/// <see cref="DialogueWorkerConfig.SeedPrompt"/>, and every prior turn's role and text in order.
/// <see cref="DialogueWorkerConfig.TurnBudget"/> is this worker's own config, and deliberately small
/// (the phase plan's "bounded" exchange) — bounding it is what keeps cost and wall-clock time
/// predictable without this worker inventing a token-budget or summarization scheme of its own. A
/// model reasoning about the exchange needs the whole conversation to stay coherent across turns,
/// not just the immediately preceding message — the same reason a human relaying every round by hand
/// (§17.5, what this milestone automates) would naturally carry the whole thread forward, not just
/// the last reply. This does <b>not</b> bound the size of what reaches the vendor CLI's own
/// command-line, which would otherwise grow every turn: each turn's full prompt (preamble + seed +
/// every prior turn) is written to a file in <paramref name="outputDirectory"/> and only that file's
/// short path crosses the process boundary (see <see cref="ProcessVendorTurnClient"/> and
/// <see cref="DialogueParticipant.PromptFilePlaceholder"/>) — issue #579 was a real crash from
/// threading the whole transcript directly into argv on Windows, whose ~32,767-character
/// command-line ceiling a long exchange eventually exceeded.
/// </para>
/// <para>
/// <b>The stop signal is a structured MCP tool call, not a text sentinel</b> (#585, decision 0035,
/// superseding M17 Phase 3, #166's original substring match): each participant's vendor CLI invocation
/// is wired, by <see cref="DialogueYieldWiring"/>, to its own instance of <c>Aer.Mcp.Host</c>, which a
/// turn can call the <c>yield</c> tool against. After a turn's process exits, this runner checks that
/// specific participant's own capture file — never another participant's, and never the turn's own
/// text — for a call, giving structural (not text-inferred) attribution of who yielded.
/// <see cref="DialogueWorkerConfig.StopSentinel"/> is still parsed onto every config but no longer acted
/// on here; see that property's own remarks and #820.
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

        await using (var transcript = new TranscriptWriter(Path.Combine(outputDirectory, "transcript.jsonl")))
        {
            for (var sequence = 1; sequence <= effectiveTurnBudget; sequence++)
            {
                var wired = wiredParticipants[(sequence - 1) % wiredParticipants.Count];
                var speaker = wired.Participant;
                var prompt = BuildPrompt(speaker, config.SeedPrompt, turns);
                var promptPath = Path.Combine(outputDirectory, $"prompt-turn-{sequence}.txt");
                await File.WriteAllTextAsync(promptPath, prompt, cancellationToken).ConfigureAwait(false);

                var result = await turnClient.SendTurnAsync(speaker, promptPath, cancellationToken).ConfigureAwait(false);

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

    private static string BuildPrompt(DialogueParticipant speaker, string seedPrompt, IReadOnlyList<TranscriptTurn> priorTurns)
    {
        var context = new StringBuilder(seedPrompt);
        foreach (var turn in priorTurns)
        {
            context.Append("\n\n").Append(FormatTurnLine(turn));
        }

        return $"{speaker.Preamble}\n\n{context}";
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
