using System.Text;

namespace Aer.Workers.Dialogue;

/// <summary>
/// Runs the dialogue exchange (M17 Phase 3, #166): alternating turns, starting with
/// <see cref="DialogueWorkerConfig.Initiator"/>, writing each to <c>transcript.jsonl</c> as it
/// happens and the final turn's text to <see cref="DialogueWorkerConfig.FinalOutputName"/> once the
/// exchange ends. Ends on either of two conditions — <see cref="DialogueWorkerConfig.TurnBudget"/>
/// turns having run, or a participant's turn containing <see cref="DialogueWorkerConfig.StopSentinel"/>
/// — and fails the whole exchange (throwing <see cref="DialogueExecutionException"/>, caught by
/// <see cref="Program"/> and mapped to a non-zero process exit) if a vendor CLI exits non-zero or
/// produces no text for a turn.
/// <para>
/// <b>Context threading is the full transcript so far</b>, not a sliding window: each turn's prompt
/// is its speaker's <see cref="DialogueParticipant.Preamble"/>, the exchange's
/// <see cref="DialogueWorkerConfig.SeedPrompt"/>, and every prior turn's role and text in order.
/// <see cref="DialogueWorkerConfig.TurnBudget"/> is this worker's own config, and deliberately small
/// (the phase plan's "bounded" exchange) — bounding it is what keeps the full transcript's size a
/// non-issue for spike #21's CLI-argument-length realities without this worker inventing a
/// token-budget or summarization scheme of its own. A model reasoning about the exchange needs the
/// whole conversation to stay coherent across turns, not just the immediately preceding message —
/// the same reason a human relaying every round by hand (§17.5, what this milestone automates) would
/// naturally carry the whole thread forward, not just the last reply.
/// </para>
/// <para>
/// <b>The stop signal is a literal substring of the turn's own text</b>, not a structured per-turn
/// output file: spike #21 already recorded that vendor CLIs are unreliable about writing extra files
/// on cue (the walkthrough's §8 finding — <c>agy</c> asking a clarifying question and writing nothing
/// at all) but reliably produce stdout text, so parsing the text this worker already reads for
/// threading is the more robust of the two shapes across two different vendors' output habits, not
/// a second per-turn contract each vendor's CLI would have to honor. The sentinel is stripped out of
/// the text recorded on the transcript and threaded forward — a reader of the transcript (and M18's
/// eventual conversation view) sees the participant's actual words, not the control token.
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

        var turns = new List<TranscriptTurn>(config.TurnBudget);

        await using (var transcript = new TranscriptWriter(Path.Combine(outputDirectory, "transcript.jsonl")))
        {
            for (var sequence = 1; sequence <= config.TurnBudget; sequence++)
            {
                var speaker = sequence % 2 == 1 ? config.Initiator : config.Responder;
                var prompt = BuildPrompt(speaker, config.SeedPrompt, turns);

                var result = await turnClient.SendTurnAsync(speaker, prompt, cancellationToken).ConfigureAwait(false);

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
                var isStop = TryStripStopSentinel(config.StopSentinel, ref text);

                var turn = new TranscriptTurn(sequence, speaker.Role, speaker.Vendor, prompt, text);
                await transcript.AppendAsync(turn, cancellationToken).ConfigureAwait(false);
                turns.Add(turn);

                if (isStop)
                {
                    break;
                }
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, config.FinalOutputName), turns[^1].Text, cancellationToken)
            .ConfigureAwait(false);

        return turns;
    }

    private static string BuildPrompt(DialogueParticipant speaker, string seedPrompt, IReadOnlyList<TranscriptTurn> priorTurns)
    {
        var context = new StringBuilder(seedPrompt);
        foreach (var turn in priorTurns)
        {
            context.Append("\n\n").Append(turn.Role).Append(": ").Append(turn.Text);
        }

        return $"{speaker.Preamble}\n\n{context}";
    }

    /// <summary>
    /// If <paramref name="stopSentinel"/> is configured and occurs in <paramref name="text"/>,
    /// removes the sentinel substring and trims the result, returning true. Leaves
    /// <paramref name="text"/> untouched and returns false otherwise. Only the sentinel occurrence
    /// is removed — the rest of the turn's text (before and after it) is preserved, so a participant
    /// may still say something meaningful in its final turn, not just the sentinel alone.
    /// </summary>
    private static bool TryStripStopSentinel(string? stopSentinel, ref string text)
    {
        if (string.IsNullOrEmpty(stopSentinel))
        {
            return false;
        }

        var index = text.IndexOf(stopSentinel, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        text = (text[..index] + text[(index + stopSentinel.Length)..]).Trim();
        return true;
    }
}
