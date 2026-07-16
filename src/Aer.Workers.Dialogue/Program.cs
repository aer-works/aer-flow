using Aer.Workers.Dialogue;

// M17 Phase 2 (#165): a runnable skeleton only. How this executable actually receives its config
// path once Flow dispatches it (a required WorkerContract input vs. some other seam) is Phase 4's
// open question — until then, two positional arguments are enough to run and test the worker
// standalone: the dialogue config file, and the directory to write transcript.jsonl plus the
// declared final output into.
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: aer-dialogue <config-file> <output-dir>");
    return 64;
}

try
{
    var config = await DialogueWorkerConfigParser.LoadFromFileAsync(args[0]).ConfigureAwait(false);
    var runner = new DialogueRunner(new ProcessVendorTurnClient());
    await runner.RunAsync(config, args[1]).ConfigureAwait(false);
    return 0;
}
catch (DialogueWorkerConfigException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (DialogueExecutionException ex)
{
    // M17 Phase 3 (#166): a turn failed mid-exchange (non-zero vendor exit or an empty turn). The
    // declared final output was never written (the throw happens before DialogueRunner reaches that
    // line), so this non-zero exit and the missing output agree — Flow's OutcomeClassifier sees an
    // ordinary failed worker on both counts, exactly like any other.
    Console.Error.WriteLine(ex.Message);
    return 1;
}
