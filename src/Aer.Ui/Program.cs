using Aer.Flow;
using Aer.Ui;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: aer-ui <task-directory>");
    return 64;
}

try
{
    var projection = await TaskProjectionLoader.LoadAsync(args[0]).ConfigureAwait(false);
    TaskStatusRenderer.Render(Console.Out, projection);
    return 0;
}
catch (AerFlowException ex)
{
    // Mirrors Aer.Cli's Program.cs boundary: every domain-level read failure — an invalid task
    // directory, a malformed snapshot, a malformed event log — surfaces as a clean message here
    // instead of a raw stack trace, per CLAUDE.md's error-handling rules.
    Console.Error.WriteLine(ex.Message);
    return 1;
}
