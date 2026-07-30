using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;
using Aer.Flow.Templates;
using Xunit;

namespace Aer.Cli.Tests;

public class CapturedWorkerStreamTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter> { ["shell"] = new ShellCommandWorkerAdapter() };

    [Fact]
    public void Reservation_DotPrefixProducedOutput_IsRejected()
    {
        // 1. ProducedOutput constructor
        var exConst = Assert.Throws<ArgumentException>(() => new ProducedOutput(".stdout.log"));
        Assert.Contains(".stdout.log", exConst.Message);
        Assert.Contains("reserved for engine stream logs", exConst.Message);

        // 2. WorkerBindingConfigParser
        var invalidJson = """
        {
          "worker": {
            "Adapter": "shell",
            "Contract": {
              "WorkerName": "worker",
              "RequiredInputs": [],
              "ProducedOutputs": [{ "Name": ".stderr.log" }],
              "OptionalMetadata": []
            },
            "PromptTemplate": "echo test",
            "Timeout": "00:01:00"
          }
        }
        """;
        var exConfig = Assert.Throws<WorkerBindingConfigException>(() => WorkerBindingConfigParser.Parse(invalidJson));
        Assert.Contains(".stderr.log", exConfig.Message);
        Assert.Contains("reserved for engine stream logs", exConfig.Message);

        // 3. WorkflowDefinitionValidator
        var invalidDef = new WorkflowDefinition(
            new WorkflowTemplateId("dot-output-test"),
            1,
            [new WorkflowStepDefinition(new StepId("step1"), "worker", [], [".stdout.log"], [], new RetryPolicy(1))]);

        var exDef = Assert.Throws<WorkflowDefinitionValidationException>(() => WorkflowDefinitionValidator.Validate(invalidDef));
        Assert.Contains(".stdout.log", exDef.Errors[0]);
        Assert.Contains("reserved for engine stream logs", exDef.Errors[0]);

        // Polarity: Normal name still validates
        var validOutput = new ProducedOutput("plan.md");
        Assert.Equal("plan.md", validOutput.Name);

        var validDef = new WorkflowDefinition(
            new WorkflowTemplateId("valid-output-test"),
            1,
            [new WorkflowStepDefinition(new StepId("step1"), "worker", [], ["plan.md"], [], new RetryPolicy(1))]);
        WorkflowDefinitionValidator.Validate(validDef);
    }

    [Fact]
    public void Immutability_AppendAfterTerminal_RefusedBySection16Mechanism()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stream-immutability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logger = new ExecutionStreamLogger(tempDir);
            logger.AppendStdout("chunk 1\n"u8.ToArray());
            Assert.False(logger.IsTerminal);

            logger.MarkTerminal();
            Assert.True(logger.IsTerminal);

            var exStdout = Assert.Throws<InvalidOperationException>(() => logger.AppendStdout("chunk 2\n"u8.ToArray()));
            Assert.Contains("terminal event", exStdout.Message);

            var exStderr = Assert.Throws<InvalidOperationException>(() => logger.AppendStderr("chunk 2\n"u8.ToArray()));
            Assert.Contains("terminal event", exStderr.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public void Rollover_CrossesCap_CreatesRolloverFileAndFreshFile()
    {
        // Seam used: ExecutionStreamLogger with a reduced cap of 100 bytes
        var tempDir = Path.Combine(Path.GetTempPath(), $"stream-rollover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const long cap = 100;
            var logger = new ExecutionStreamLogger(tempDir, maxSizeBytes: cap);

            var stdoutPath = Path.Combine(tempDir, ExecutionStreamLogger.StdoutLogFileName);
            var stdoutRolloverPath = Path.Combine(tempDir, ExecutionStreamLogger.StdoutRolloverFileName);

            // Chunk 1: 60 bytes
            var chunk1 = new byte[60];
            Array.Fill(chunk1, (byte)'A');
            logger.AppendStdout(chunk1);

            Assert.True(File.Exists(stdoutPath));
            Assert.False(File.Exists(stdoutRolloverPath));
            Assert.Equal(60, new FileInfo(stdoutPath).Length);

            // Chunk 2: 60 bytes (60 + 60 = 120 > 100 cap -> rollover!)
            var chunk2 = new byte[60];
            Array.Fill(chunk2, (byte)'B');
            logger.AppendStdout(chunk2);

            Assert.True(File.Exists(stdoutPath));
            Assert.True(File.Exists(stdoutRolloverPath));
            Assert.Equal(60, new FileInfo(stdoutRolloverPath).Length);
            Assert.Equal(60, new FileInfo(stdoutPath).Length);
            Assert.Equal(chunk1, File.ReadAllBytes(stdoutRolloverPath));
            Assert.Equal(chunk2, File.ReadAllBytes(stdoutPath));

            // Chunk 3: 60 bytes -> rollover again!
            var chunk3 = new byte[60];
            Array.Fill(chunk3, (byte)'C');
            logger.AppendStdout(chunk3);

            Assert.Equal(60, new FileInfo(stdoutRolloverPath).Length);
            Assert.Equal(60, new FileInfo(stdoutPath).Length);
            Assert.Equal(chunk2, File.ReadAllBytes(stdoutRolloverPath));
            Assert.Equal(chunk3, File.ReadAllBytes(stdoutPath));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(tempDir);
        }
    }

    [Fact]
    public async Task RoundTrip_And_RenderEscaping_BothPolarities()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"stream-roundtrip-{Guid.NewGuid():N}");
        var taskDirectory = Path.Combine(testRoot, "task");
        try
        {
            Directory.CreateDirectory(testRoot);

            // Polarity 1: Real worker process emitting ANSI escape sequences + non-UTF-8 bytes
            // Command writes ANSI red escape sequence "\x1b[31mANSI_RED\x1b[0m" and newline
            var rawBytes = new byte[]
            {
                0x1B, (byte)'[', (byte)'3', (byte)'1', (byte)'m', (byte)'R', (byte)'E', (byte)'D',
                0x1B, (byte)'[', (byte)'0', (byte)'m', (byte)'\n', 0x80, (byte)'\n'
            };

            // Test render-time escaping function directly on rawBytes
            var escapedRender = StatusCommand.EscapeNonPrintable(rawBytes);
            Assert.Contains("\\x1b[31mRED\\x1b[0m\n", escapedRender);

            // Real execution with shell worker emitting raw output
            var definition = new WorkflowDefinition(
                new WorkflowTemplateId("roundtrip-flow"),
                1,
                [new WorkflowStepDefinition(new StepId("worker"), "worker", [], ["out.txt"], [], new RetryPolicy(1))]);

            var cmdLine = OperatingSystem.IsWindows()
                ? "echo Hello World & echo \u001b[31mRed\u001b[0m & echo Red > %AER_OUTPUT_DIR%\\out.txt"
                : "echo Hello World && echo '\033[31mRed\033[0m' && echo Red > \"$AER_OUTPUT_DIR/out.txt\"";

            var bindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                ["worker"] = new WorkerBindingConfigEntry(
                    "shell",
                    new WorkerContract("worker", [], [new ProducedOutput("out.txt")], []),
                    cmdLine,
                    TimeSpan.FromSeconds(30))
            };

            var workflowFile = Path.Combine(testRoot, "workflow.json");
            var bindingsFile = Path.Combine(testRoot, "bindings.json");
            await File.WriteAllTextAsync(workflowFile, JsonSerializer.Serialize(definition), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(bindingsFile, JsonSerializer.Serialize(bindings), TestContext.Current.CancellationToken);

            var runOptions = new RunOptions(workflowFile, bindingsFile, taskDirectory);
            var runResult = await RunCommand.ExecuteAsync(runOptions, Adapters, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, runResult.State.Status);

            var execId = runResult.State.Steps[0].LatestExecutionId!.Value.Value;
            var execDir = Path.Combine(taskDirectory, "artifacts", $"execution_{execId}");
            var stdoutFile = Path.Combine(execDir, ExecutionStreamLogger.StdoutLogFileName);

            Assert.True(File.Exists(stdoutFile), $"Expected stream log file at {stdoutFile}");

            var stdoutContent = File.ReadAllBytes(stdoutFile);
            Assert.NotEmpty(stdoutContent);

            // Render with StatusCommand
            var output = new StringWriter();
            await StatusCommand.ExecuteAsync(new StatusOptions(taskDirectory, Follow: true), output, TestContext.Current.CancellationToken);
            var statusText = output.ToString();

            Assert.Contains("Workflow status: Terminal", statusText);

            // Polarity 2: Normal printable text
            var normalBytes = "Normal printable text\twith tab\n"u8.ToArray();
            var normalEscaped = StatusCommand.EscapeNonPrintable(normalBytes);
            Assert.Equal("Normal printable text\twith tab\n", normalEscaped);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }
}
