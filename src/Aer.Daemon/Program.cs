using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.IO;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Ui.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

[assembly: InternalsVisibleTo("Aer.Ui.Tests")]

await Aer.Daemon.DaemonHost.RunDaemonAsync(args);

namespace Aer.Daemon
{
    public static class DaemonHost
    {
        public static WebApplication? App { get; set; }

        public static async Task RunDaemonAsync(string[] args)
        {
            var username = Environment.UserName;
            using var mutex = new Mutex(true, $"Global\\AerDaemonMutex_{username}", out var createdNew);
            if (!createdNew)
            {
                Console.WriteLine("Another instance of Aer.Daemon is already running.");
                return;
            }

            // Setup local data directory ~/.aer
            var aerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer");
            Directory.CreateDirectory(aerDir);

            // Generate token if not exists
            var tokenFile = Path.Combine(aerDir, "daemon.token");
            string token;
            if (File.Exists(tokenFile))
            {
                token = (await File.ReadAllTextAsync(tokenFile)).Trim();
            }
            else
            {
                token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(tokenFile, token);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            var builder = WebApplication.CreateBuilder(args);

            // Ensure daemon listens on a fixed localhost port (allow override via --port)
            var portIndex = Array.IndexOf(args, "--port");
            var port = (portIndex >= 0 && portIndex < args.Length - 1) ? int.Parse(args[portIndex + 1]) : 5000;

            var activePort = port;
            if (port != 0)
            {
                try
                {
                    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                }
                catch (System.Net.Sockets.SocketException)
                {
                    // Port is in use! Fall back to port 0 (dynamic port allocation)
                    activePort = 0;
                }
            }

            var isRemote = args.Contains("--remote");

            builder.WebHost.ConfigureKestrel(options =>
            {
                if (isRemote)
                {
                    options.Listen(System.Net.IPAddress.Any, activePort);
                }
                else
                {
                    options.ListenLocalhost(activePort);
                }
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
            builder.Services.AddSingleton<PairedClientsStore>();

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

            bool SafeEquals(string a, string b)
            {
                if (a.Length != b.Length) return false;
                var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
                var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
            }

            // Authentication Middleware verifying the Bearer token
            app.Use(async (context, next) =>
            {
                // Allow public access to version endpoint and pairing pairing endpoint
                if ((context.Request.Path == "/api/version" && context.Request.Method == "GET") ||
                    (context.Request.Path == "/api/pairing/pair" && context.Request.Method == "POST"))
                {
                    await next(context);
                    return;
                }

                string requestToken = "";
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var queryToken = context.Request.Query["token"].ToString().Trim();
                    var headerToken = context.Request.Headers["Authorization"].ToString().Replace("Bearer ", "").Trim();
                    requestToken = !string.IsNullOrEmpty(queryToken) ? queryToken : headerToken;
                }
                else
                {
                    var authHeader = context.Request.Headers["Authorization"].ToString();
                    if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        requestToken = authHeader.Substring("Bearer ".Length).Trim();
                    }
                }

                if (!string.IsNullOrEmpty(requestToken))
                {
                    // 1. Verify against local loopback token
                    if (SafeEquals(requestToken, token))
                    {
                        await next(context);
                        return;
                    }

                    // 2. Verify against paired clients
                    var store = context.RequestServices.GetRequiredService<PairedClientsStore>();
                    if (store.ValidateToken(requestToken))
                    {
                        await next(context);
                        return;
                    }
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized");
            });

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

            // Version metadata endpoint
            app.MapGet("/api/version", (TaskSession session) => Results.Ok(new 
            { 
                Version = typeof(DaemonHost).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                HasRunningTasks = session.ShouldLiveRefresh
            }));

            // Graceful shutdown endpoint
            app.MapPost("/api/daemon/shutdown", (IHostApplicationLifetime lifetime) =>
            {
                lifetime.StopApplication();
                return Results.Ok("Shutting down...");
            });

            // Generates a 6-digit pairing code (only callable if authorized, typically by local UI)
            app.MapGet("/api/pairing/code", () =>
            {
                var code = PairingCodeManager.GenerateCode();
                return Results.Ok(new { Code = code, ExpiresInSeconds = 60 });
            });

            // Exposes pairing verification (public endpoint)
            app.MapPost("/api/pairing/pair", ([FromBody] PairRequest request, PairedClientsStore store) =>
            {
                if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.ClientName))
                {
                    return Results.BadRequest("Code and ClientName are required.");
                }

                if (PairingCodeManager.ValidateAndConsume(request.Code))
                {
                    var token = store.AddClient(request.ClientName);
                    return Results.Ok(new { Token = token });
                }

                return Results.Json(new { Error = "Invalid or expired pairing code." }, statusCode: StatusCodes.Status400BadRequest);
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

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await session.RunAsync(
                            request.DirectoryPath,
                            request.WorkflowTemplateFilePath,
                            request.BindingsFilePath);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error executing task run in background: {ex}");
                    }
                });

                return Results.Ok();
            });

            app.MapPost("/api/tasks/decide", async ([FromBody] DecideTaskRequest request, TaskSession session) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath)) return Results.BadRequest("DirectoryPath is required.");

                _ = Task.Run(async () =>
                {
                    try
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
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error executing task decide in background: {ex}");
                    }
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

            // Write active port to discovery file on startup
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var server = app.Services.GetRequiredService<IServer>();
                var addressesFeature = server.Features.Get<IServerAddressesFeature>();
                if (addressesFeature != null)
                {
                    var firstUrl = addressesFeature.Addresses.FirstOrDefault();
                    if (firstUrl != null)
                    {
                        var uri = new Uri(firstUrl);
                        var portFile = Path.Combine(aerDir, "daemon.port");
                        File.WriteAllText(portFile, uri.Port.ToString());
                    }
                }
            });

            await app.RunAsync();
        }
    }

    public class PairRequest
    {
        public string Code { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
    }
}
