using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Ui.Core;
using Microsoft.AspNetCore.Mvc;

[assembly: InternalsVisibleTo("Aer.Ui.Tests")]

await Aer.Daemon.DaemonHost.RunDaemonAsync(args);

namespace Aer.Daemon
{
    public static class DaemonHost
    {
        public static WebApplication? App { get; set; }

        public static async Task RunDaemonAsync(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Ensure daemon listens on a fixed localhost port (allow override via --port)
            var portIndex = Array.IndexOf(args, "--port");
            var port = (portIndex >= 0 && portIndex < args.Length - 1) ? int.Parse(args[portIndex + 1]) : 5000;
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(port);
            });

            // Configure JSON options
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });

            // Register singletons
            builder.Services.AddSingleton(LocalUiConfigurationStore.CreateDefault());
            builder.Services.AddSingleton<IReadOnlyDictionary<string, IWorkerAdapter>>(WorkerAdapterRegistry.Default);
            builder.Services.AddSingleton<MainWindowViewModel>();

            // Thread-safe container for bindings path
            var bindingsPathHolder = new BindingsPathHolder();
            builder.Services.AddSingleton(bindingsPathHolder);

            // Active WebSocket connections
            var webSockets = new System.Collections.Concurrent.ConcurrentBag<WebSocket>();

            // Helper method for sending state to a single socket
            async Task SendStateAsync(WebSocket socket, TaskProjection projection)
            {
                var json = JsonSerializer.Serialize(projection, new JsonSerializerOptions
                {
                    Converters = { new JsonStringEnumConverter() },
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }

            // Helper method for broadcasting state to all sockets
            async Task BroadcastStateAsync(TaskProjection projection)
            {
                var activeSockets = webSockets.Where(s => s.State == WebSocketState.Open).ToList();
                foreach (var socket in activeSockets)
                {
                    try
                    {
                        await SendStateAsync(socket, projection);
                    }
                    catch
                    {
                        // Ignore single socket failures
                    }
                }
            }

            // Register TaskSession
            builder.Services.AddSingleton(sp =>
            {
                var configStore = sp.GetRequiredService<LocalUiConfigurationStore>();
                var adapters = sp.GetRequiredService<IReadOnlyDictionary<string, IWorkerAdapter>>();
                var viewModel = sp.GetRequiredService<MainWindowViewModel>();
                var pathHolder = sp.GetRequiredService<BindingsPathHolder>();

                TaskSession? session = null;

                Func<string, CancellationToken, Task> reopenTaskAsync = async (taskDirectoryPath, cancellationToken) =>
                {
                    if (session != null)
                    {
                        var outcome = await session.LoadAsync(taskDirectoryPath, cancellationToken);
                        if (outcome.Projection != null)
                        {
                            await BroadcastStateAsync(outcome.Projection);
                        }
                    }
                };

                session = new TaskSession(
                    configStore,
                    adapters,
                    viewModel,
                    bindingsFilePathProvider: () => pathHolder.BindingsFilePath,
                    mutationStarted: () => { },
                    mutationFailed: () => { },
                    reopenTaskAsync: reopenTaskAsync
                );

                return session;
            });

            var app = builder.Build();
            App = app;

            app.UseWebSockets();

            // WebSocket endpoint
            app.Map("/api/ws", async (HttpContext context, TaskSession session) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    webSockets.Add(webSocket);

                    // Send current projection immediately if loaded
                    if (session.CurrentTaskDirectoryPath != null && session.LastLoadSucceeded)
                    {
                        var outcome = await session.LoadAsync(session.CurrentTaskDirectoryPath);
                        if (outcome.Projection != null)
                        {
                            await SendStateAsync(webSocket, outcome.Projection);
                        }
                    }

                    // Keep connection open
                    var buffer = new byte[1024 * 4];
                    try
                    {
                        while (webSocket.State == WebSocketState.Open)
                        {
                            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            }
                        }
                    }
                    catch
                    {
                        // Ignore socket disconnect errors
                    }
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                }
            });

            // REST endpoints
            app.MapGet("/api/tasks/recent", async (TaskSession session) =>
            {
                var directories = await session.LoadRecentTaskDirectoriesAsync();
                return Results.Ok(directories);
            });

            app.MapPost("/api/tasks/open", async ([FromBody] OpenTaskRequest request, TaskSession session, BindingsPathHolder pathHolder) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return Results.BadRequest("DirectoryPath is required.");
                }

                session.SetCurrentTaskDirectory(request.DirectoryPath);
                await session.RecordOpenedAsync(request.DirectoryPath);
                var outcome = await session.LoadAsync(request.DirectoryPath);
                if (outcome.Projection != null)
                {
                    var bindingsPath = await session.LoadLastBindingsFilePathAsync();
                    if (bindingsPath != null)
                    {
                        pathHolder.BindingsFilePath = bindingsPath;
                    }
                    await BroadcastStateAsync(outcome.Projection);
                    return Results.Ok(outcome.Projection);
                }
                return Results.BadRequest(outcome.ErrorMessage);
            });

            app.MapPost("/api/tasks/run", async ([FromBody] RunTaskRequest request, TaskSession session, BindingsPathHolder pathHolder) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");
                if (string.IsNullOrEmpty(request.BindingsFilePath)) return Results.BadRequest("BindingsFilePath is required.");

                pathHolder.BindingsFilePath = request.BindingsFilePath;

                // Start background run pump asynchronously - don't block the HTTP request
                _ = Task.Run(async () =>
                {
                    var outcome = await session.RunAsync(
                        request.DirectoryPath,
                        request.WorkflowTemplateFilePath,
                        request.BindingsFilePath);
                });

                return Results.Ok();
            });

            app.MapPost("/api/tasks/decide", async ([FromBody] DecideTaskRequest request, TaskSession session) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");

                // Start background decide pump asynchronously
                _ = Task.Run(async () =>
                {
                    await session.DecideAsync(
                        request.DirectoryPath,
                        new StepId(request.StepId),
                        new ExecutionId(request.ExecutionId),
                        request.DecisionType,
                        request.TargetStepId != null ? new StepId(request.TargetStepId) : null,
                        request.RevisionFilePath,
                        request.SupplementaryWorker,
                        request.SupplementaryOutputName);
                });

                return Results.Ok();
            });

            app.MapPost("/api/tasks/cancel", async ([FromBody] CancelTaskRequest request, TaskSession session) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");

                if (!string.IsNullOrEmpty(request.ExecutionId))
                {
                    await session.CancelExecutionAsync(request.DirectoryPath, new ExecutionId(request.ExecutionId));
                }
                else
                {
                    session.RequestHostStop();
                }

                return Results.Ok();
            });

            await app.RunAsync();
        }
    }

    // Request models
    public record OpenTaskRequest(string DirectoryPath);
    public record RunTaskRequest(string DirectoryPath, string? WorkflowTemplateFilePath, string BindingsFilePath);
    public record DecideTaskRequest(
        string DirectoryPath,
        string StepId,
        string ExecutionId,
        DecisionType DecisionType,
        string? TargetStepId = null,
        string? RevisionFilePath = null,
        string? SupplementaryWorker = null,
        string? SupplementaryOutputName = null);
    public record CancelTaskRequest(string DirectoryPath, string? ExecutionId = null);

    public class BindingsPathHolder
    {
        public string? BindingsFilePath { get; set; }
    }
}
