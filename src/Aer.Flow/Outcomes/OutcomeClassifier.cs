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
public sealed record OutcomeClassification(OutcomeVerdict Verdict, FailureClassification? FailureClassification = null);

/// <summary>
/// Maps a <see cref="CoreDispatchResult"/> plus a step's <see cref="WorkerContract"/> into one of
/// the three terminal classifications spec §8 defines. Flow alone interprets Core's purely
/// mechanical report (exit code + reason) — Core itself has no notion of "success" beyond that.
/// </summary>
public static class OutcomeClassifier
{
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

        if (result.Reason == CoreExitReason.Natural &&
            result.ExitCode == 0 &&
            ContractValidator.IsSatisfied(contract, outputDirectory))
        {
            return new OutcomeClassification(OutcomeVerdict.Succeeded);
        }

        return new OutcomeClassification(OutcomeVerdict.Failed, ReadFailureClassification(contract, outputDirectory));
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
