using System.Text.Json;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Flow.Outcomes;

/// <summary>The three terminal outcomes spec §8 classifies a completed dispatch into.</summary>
public enum OutcomeVerdict
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// The classified result of a completed dispatch — the input to whichever
/// <see cref="Domain.FlowEvent"/> terminal case the <c>MutationInterface</c> appends to the log.
/// </summary>
/// <param name="Reason">
/// A human-readable diagnostic for a <see cref="OutcomeVerdict.Failed"/> verdict — why exit code,
/// exit reason, and contract state add up to a failure, computed once here from data available at
/// classification time. Distinct from <paramref name="FailureClassification"/>, which is the
/// worker's own self-reported retry hint, not a diagnostic Flow derives. Every failure path
/// <i>in this class</i> sets it, and it is null for
/// <see cref="OutcomeVerdict.Succeeded"/> and <see cref="OutcomeVerdict.Cancelled"/>.
/// <para>
/// That is deliberately a claim about this class and not about stored events. An earlier version of
/// this comment inferred that a null <c>Reason</c> on a persisted
/// <see cref="Domain.FlowEvent.ExecutionFailed"/> therefore means "written before this field
/// existed" — which nothing enforces, since <c>Reason</c> is an optional parameter any call site may
/// omit in silence, and several test fixtures already write real <c>flow.jsonl</c> lines that do.
/// Treat a null on a stored event as "no reason recorded", never as evidence of when it was written.
/// </para>
/// </param>
public sealed record OutcomeClassification(
    OutcomeVerdict Verdict,
    FailureClassification? FailureClassification = null,
    string? Reason = null);

/// <summary>
/// Maps a <see cref="CoreDispatchResult"/> plus a step's <see cref="WorkerContract"/> into one of
/// the three terminal classifications spec §8 defines. Flow alone interprets Core's purely
/// mechanical report (exit code + reason) — Core itself has no notion of "success" beyond that.
/// </summary>
public static class OutcomeClassifier
{
    private const int MaxReasonLength = 500;

    /// <summary>
    /// How many unsatisfied outputs a reason names before summarising the rest as "(+N more)".
    /// A contract with more failures than this has a problem the count communicates better than
    /// the list would.
    /// </summary>
    private const int MaxListedOutputs = 8;

    /// <summary>
    /// Classifies <paramref name="result"/> per spec §8's table:
    /// <c>NaturalExit + code 0 + all ProducedOutputs satisfied</c> → Succeeded;
    /// <c>NaturalExit</c> otherwise, or <c>TimedOut</c> → Failed;
    /// <c>CancelRequested</c> → Cancelled.
    /// </summary>
    public static OutcomeClassification Classify(
        CoreDispatchResult result,
        WorkerContract contract,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        if (result.Reason == CoreExitReason.CancelRequested)
        {
            // §9: a cancellation is never classified as a failure, and (§10) never retried.
            return new OutcomeClassification(OutcomeVerdict.Cancelled);
        }

        if (result.Reason == CoreExitReason.TimedOut)
        {
            return new OutcomeClassification(
                OutcomeVerdict.Failed,
                ReadFailureClassification(contract, outputDirectory),
                "Execution timed out.");
        }

        // Only CoreExitReason.Natural remains.
        if (result.ExitCode != 0)
        {
            return new OutcomeClassification(
                OutcomeVerdict.Failed,
                ReadFailureClassification(contract, outputDirectory),
                $"Worker exited with non-zero code {result.ExitCode}.");
        }

        var validation = ContractValidator.Validate(contract, outputDirectory);
        if (validation.IsSatisfied)
        {
            return new OutcomeClassification(OutcomeVerdict.Succeeded);
        }

        return new OutcomeClassification(
            OutcomeVerdict.Failed,
            ReadFailureClassification(contract, outputDirectory),
            BuildContractFailureReason(validation.UnsatisfiedOutputs));
    }

    /// <summary>
    /// Assembles the diagnostic for a natural, exit-0 completion whose contract still isn't
    /// satisfied — the exact signature (worker exited 0, wrote none of its declared outputs) that
    /// previously surfaced as a bare <c>ExecutionFailed</c> with no reason.
    /// </summary>
    /// <remarks>
    /// Bounded in two places, and the split matters. Each output's <i>count</i> is capped here and
    /// each rendered <i>value</i> is capped in <see cref="ContractValidator"/>, both with their own
    /// explicit marker; only then is <see cref="Truncate"/> applied to the whole. Capping solely at
    /// the end was wrong: one large value would eat the entire budget and silently drop every other
    /// unsatisfied output, so a reason that promised to name them all named one. With the per-item
    /// bounds in place the final <see cref="Truncate"/> is a backstop that should not normally fire.
    /// </remarks>
    private static string BuildContractFailureReason(IReadOnlyList<UnsatisfiedOutput> unsatisfiedOutputs)
    {
        var listed = unsatisfiedOutputs.Count <= MaxListedOutputs
            ? unsatisfiedOutputs
            : unsatisfiedOutputs.Take(MaxListedOutputs).ToList();

        var reason = "Contract not satisfied: " + string.Join("; ", listed.Select(DescribeUnsatisfiedOutput));

        if (unsatisfiedOutputs.Count > listed.Count)
        {
            reason += $" (+{unsatisfiedOutputs.Count - listed.Count} more)";
        }

        return Truncate(reason, MaxReasonLength);
    }

    private static string DescribeUnsatisfiedOutput(UnsatisfiedOutput output) => output.Reason switch
    {
        UnsatisfiedOutputReason.Missing => $"'{output.Name}' is missing",
        UnsatisfiedOutputReason.NotJson => $"'{output.Name}' is not valid JSON",
        UnsatisfiedOutputReason.ConditionFailed => output.ActualValue is null
            ? $"'{output.Name}': JSON Pointer '{output.ConditionPath}' did not resolve (expected {output.ExpectedValue})"
            : $"'{output.Name}': JSON Pointer '{output.ConditionPath}' resolved to {output.ActualValue}, expected {output.ExpectedValue}",
        _ => throw new ArgumentOutOfRangeException(nameof(output), output.Reason, "Unknown UnsatisfiedOutputReason."),
    };

    /// <summary>
    /// Backstop cap on the assembled reason. Delegates the cut to
    /// <see cref="ContractValidator.TrimWithoutSplittingSurrogatePair"/> so there is one
    /// surrogate-safe truncation in the codebase rather than two that can drift — the per-value cap
    /// needs the identical rule, and a second copy of it is the shape that goes wrong quietly.
    /// The <c>cut &gt; 0</c> guard lives there: it is unreachable while
    /// <see cref="MaxReasonLength"/> is 500, but lowering a display cap is an ordinary later edit and
    /// an unguarded index would throw out of <see cref="Classify"/> while recording an outcome.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        const string ellipsis = "...";
        return ContractValidator.TrimWithoutSplittingSurrogatePair(value, maxLength - ellipsis.Length) + ellipsis;
    }

    /// <summary>
    /// Looks for a worker's optional self-reported <see cref="Domain.FailureClassification"/>
    /// (spec §8.1), reported through one of the contract's declared <c>OptionalMetadata</c> file
    /// roles as a top-level <c>FailureClassification</c> JSON field. Checked in declaration order;
    /// the first metadata file that exists, parses as JSON, and carries a recognized value wins.
    /// Absent or unrecognized — including no <c>OptionalMetadata</c> file at all — is null, which
    /// the domain type documents as "treated as Retryable".
    /// </summary>
    private static FailureClassification? ReadFailureClassification(WorkerContract contract, string outputDirectory)
    {
        foreach (var metadataName in contract.OptionalMetadata)
        {
            var path = Path.Combine(outputDirectory, metadataName);
            if (!File.Exists(path))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllBytes(path));
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("FailureClassification", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<FailureClassification>(value.GetString(), ignoreCase: true, out var classification))
                {
                    return classification;
                }
            }
        }

        return null;
    }
}
