namespace Aer.Workers.Dialogue;

/// <summary>
/// Runs the dialogue exchange (M17 Phase 2, #165): a fixed number of alternating turns
/// (<see cref="DialogueWorkerConfig.TurnBudget"/>), starting with <see cref="DialogueWorkerConfig.Initiator"/>,
/// writing each to <c>transcript.jsonl</c> as it happens and the final turn's text to
/// <see cref="DialogueWorkerConfig.FinalOutputName"/> once the exchange ends.
/// <para>
/// <b>Excluded from this skeleton, by phase boundary:</b> stopping early on
/// <see cref="DialogueWorkerConfig.StopSentinel"/>, and any failure classification for a vendor CLI
/// exiting nonzero or producing empty turn text — both Phase 3 (#166). <b>Context threading is
/// deliberately minimal</b>: each turn's prompt is its speaker's <see cref="DialogueParticipant.Preamble"/>
/// plus only the immediately preceding turn's text (the seed prompt for turn 1) — not the full
/// transcript. Whether a real exchange needs more context than that (spike #21's prompt-size
/// realities) is exactly Phase 3's "how much of the transcript" open question; this skeleton only
/// needs enough threading to prove the loop and the schema.
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
        var threadedContext = config.SeedPrompt;

        await using (var transcript = new TranscriptWriter(Path.Combine(outputDirectory, "transcript.jsonl")))
        {
            for (var sequence = 1; sequence <= config.TurnBudget; sequence++)
            {
                var speaker = sequence % 2 == 1 ? config.Initiator : config.Responder;
                var prompt = $"{speaker.Preamble}\n\n{threadedContext}";

                var text = await turnClient.SendTurnAsync(speaker, prompt, cancellationToken).ConfigureAwait(false);

                var turn = new TranscriptTurn(sequence, speaker.Role, speaker.Vendor, prompt, text);
                await transcript.AppendAsync(turn, cancellationToken).ConfigureAwait(false);
                turns.Add(turn);

                threadedContext = text;
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, config.FinalOutputName), turns[^1].Text, cancellationToken)
            .ConfigureAwait(false);

        return turns;
    }
}
