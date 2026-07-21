using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.IO;
using Aer.Adapters;
using Aer.Cli;
using Aer.Flow.Artifacts;
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
        // M21 Phase 2 (#232): see the /api/tasks/artifact handler below for why this is larger
        // than HomeViewModel's 400-char inbox snippet.
        private const int ArtifactPreviewMaxLength = 50_000;

        public static WebApplication? App { get; set; }

        public static async Task RunDaemonAsync(string[] args, IReadOnlyDictionary<string, IWorkerAdapter>? adapters = null)
        {
            var noMutex = args.Contains("--no-mutex");
            Mutex? mutex = null;
            if (!noMutex)
            {
                var username = Environment.UserName;
                mutex = new Mutex(true, $"Global\\AerDaemonMutex_{username}", out var createdNew);
                if (!createdNew)
                {
                    Console.WriteLine("Another instance of Aer.Daemon is already running.");
                    mutex.Dispose();
                    return;
                }
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

            // Default graceful-shutdown budget is 30s — found live: even after tying the /api/ws
            // receive loop's ReceiveAsync to context.RequestAborted (so it CAN unblock promptly on
            // shutdown), a real shutdown-then-respawn toggle still took over 20s end to end. Rather
            // than keep chasing which specific connection/timer is still cooperative-cancellation-shy,
            // bound the worst case directly: after this, Kestrel force-aborts anything still open.
            builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = TimeSpan.FromSeconds(3));

            // Ensure daemon listens on a fixed localhost port (allow override via --port)
            var portIndex = Array.IndexOf(args, "--port");
            var port = (portIndex >= 0 && portIndex < args.Length - 1) ? int.Parse(args[portIndex + 1]) : 5000;

            var activePort = port;
            if (portIndex < 0 && port != 0)
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
            builder.Services.AddSingleton(adapters ?? WorkerAdapterRegistry.Default);
            builder.Services.AddSingleton<MainWindowViewModel>();
            builder.Services.AddSingleton<PairedClientsStore>();

            // Thread-safe container for bindings path
            var bindingsPathHolder = new BindingsPathHolder();
            builder.Services.AddSingleton(bindingsPathHolder);

            // Active WebSocket connections
            var webSockets = new System.Collections.Concurrent.ConcurrentBag<WebSocket>();

            // M24 Phase 1's live in-turn streaming: a deliberately separate socket/bag from
            // `webSockets` above, not an overload of the existing `/api/ws` protocol. That endpoint's
            // frames are bare TaskProjection JSON with a couple of sibling properties bolted on
            // (DirectoryPath, WorkerAdapters) — every existing client deserializes each incoming
            // frame straight into TaskProjection with no type discriminator at all. Sending a
            // differently-shaped progress frame down that same socket risks corrupting an existing
            // client's projection state on a frame it doesn't recognize; a dedicated endpoint carries
            // zero compatibility risk for clients that never opt into it.
            var progressWebSockets = new System.Collections.Concurrent.ConcurrentBag<WebSocket>();

            async Task BroadcastSessionProgressAsync(string directoryPath, string stepId, WorkerProgressEvent progressEvent)
            {
                var activeSockets = progressWebSockets.Where(s => s.State == WebSocketState.Open).ToList();
                if (activeSockets.Count == 0)
                {
                    return;
                }

                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    DirectoryPath = directoryPath,
                    StepId = stepId,
                    progressEvent.Kind,
                    progressEvent.Text,
                    progressEvent.IsPartial,
                });

                foreach (var socket in activeSockets)
                {
                    try
                    {
                        await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch
                    {
                        // Ignore single socket failures — same tolerance as BroadcastStateAsync below.
                    }
                }
            }

            // Helper method for sending state to a single socket. DirectoryPath (M21 Phase 2, #232)
            // is added as a sibling property rather than a TaskProjection field: the desktop client
            // deserializes this same payload straight into TaskProjection and silently ignores
            // unmapped members, so this is additive and can't break it. Aer.Mobile needs it because
            // /api/tasks/decide and /api/tasks/cancel take an explicit directoryPath — with no way
            // to derive it from the projection itself, a client that only ever observes the WS
            // stream (never having called /api/tasks/open itself) would have no directory to send
            // decisions against.
            async Task SendStateAsync(WebSocket socket, TaskProjection projection, string? directoryPath)
            {
                var options = new JsonSerializerOptions
                {
                    Converters = { new JsonStringEnumConverter() },
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                var node = JsonSerializer.SerializeToNode(projection, options)!.AsObject();
                node["DirectoryPath"] = directoryPath;

                if (!string.IsNullOrEmpty(directoryPath))
                {
                    // M24 mobile chat UI follow-up (issue #262): lets a client that only observes
                    // this push (never having called /api/sessions/start itself — e.g. a phone
                    // whose _openDirectoryPath was seeded from another client's push, or picked
                    // from /api/tasks/recent) learn this directory is an interactive session and
                    // which SessionId to fetch turns for, without a GET /api/sessions list-scan on
                    // every push. Same additive-sibling pattern as DirectoryPath/WorkerAdapters
                    // above; still not part of TaskProjection itself.
                    var sessionMetadataPath = Path.Combine(directoryPath, ".aer", "session.json");
                    if (File.Exists(sessionMetadataPath))
                    {
                        try
                        {
                            var sessionMetadata = await InteractiveSessionMaterializer.LoadMetadataAsync(sessionMetadataPath).ConfigureAwait(true);
                            if (sessionMetadata != null)
                            {
                                node["SessionId"] = sessionMetadata.SessionId;
                            }
                        }
                        catch { }
                    }

                    var bindingsPath = Path.Combine(directoryPath, "bindings.json");
                    if (!File.Exists(bindingsPath))
                    {
                        var metaPath = Path.Combine(directoryPath, ".aer", "bindings-path");
                        if (File.Exists(metaPath))
                        {
                            try { bindingsPath = File.ReadAllText(metaPath).Trim(); } catch { }
                        }
                    }
                    if (File.Exists(bindingsPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(bindingsPath);
                            using var doc = JsonDocument.Parse(json);
                            var adaptersNode = new System.Text.Json.Nodes.JsonObject();
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                if (prop.Value.TryGetProperty("Adapter", out var adapterProp) || prop.Value.TryGetProperty("adapter", out adapterProp))
                                {
                                    if (adapterProp.GetString() is { } adapterStr)
                                    {
                                        adaptersNode[prop.Name] = adapterStr;
                                    }
                                }
                            }
                            node["WorkerAdapters"] = adaptersNode;
                        }
                        catch { }
                    }
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(options));
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }

            // Helper method for broadcasting state to all sockets
            async Task BroadcastStateAsync(TaskProjection projection, string? directoryPath)
            {
                var activeSockets = webSockets.Where(s => s.State == WebSocketState.Open).ToList();
                foreach (var socket in activeSockets)
                {
                    try
                    {
                        await SendStateAsync(socket, projection, directoryPath);
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
                            await BroadcastStateAsync(outcome.Projection, taskDirectoryPath);
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

            // M21 Phase 5 (#242): the Go tsnet sidecar, spawned only in --remote mode. This is
            // additive on top of the existing plain-LAN Kestrel bind above, not a replacement for
            // it -- the tsnet path is not yet proven live (no cross-network run has exercised it,
            // see IMPLEMENTATION_PLAN.md's Phase 5 entry), so Kestrel keeps listening on
            // IPAddress.Any exactly as it did before. Retiring that proven path in favor of an
            // unproven one in the same change would be a regression, not a hardening step -- Phase
            // 6's loopback-only rebind is deliberately deferred until the sidecar path has an
            // actual recorded green run, same convention CLAUDE.md's "Live-vendor smoke tests"
            // section already applies to vendor-CLI gates.
            Process? sidecarProcess = null;
            int? sidecarStatusPort = null;
            string? sidecarUnavailableReason = null;
            var sidecarStatusPortFile = Path.Combine(aerDir, "sidecar-status.port");
            var sidecarHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

            void TryAppendSidecarLog(string path, string line)
            {
                try { File.AppendAllText(path, line + Environment.NewLine); } catch { /* best-effort */ }
            }

            void TryStartSidecar(int kestrelPort)
            {
                var sidecarExeName = OperatingSystem.IsWindows() ? "aer-sidecar.exe" : "aer-sidecar";
                var sidecarPath = Path.Combine(AppContext.BaseDirectory, sidecarExeName);
                if (!File.Exists(sidecarPath))
                {
                    // Permanent, not "starting" -- without this, /api/remote/sidecar-status would
                    // say "starting" forever instead of telling the UI (and whoever's staring at
                    // it) that zero-config needs `pixi run build-sidecar` first.
                    sidecarUnavailableReason = "aer-sidecar isn't built -- run `pixi run build-sidecar` (requires a Go toolchain), then restart remote access. Falling back to plain LAN.";
                    Console.WriteLine($"aer-sidecar not found at {sidecarPath} -- --remote falls back to plain LAN only.");
                    return;
                }

                try { if (File.Exists(sidecarStatusPortFile)) File.Delete(sidecarStatusPortFile); } catch { /* best-effort */ }

                var stateDir = Path.Combine(aerDir, "sidecar-tsnet");
                Directory.CreateDirectory(stateDir);

                var args = $"--kestrel-port {kestrelPort} --status-port-file \"{sidecarStatusPortFile}\" --state-dir \"{stateDir}\" --hostname aer-{Environment.MachineName}";

                var startInfo = new ProcessStartInfo
                {
                    FileName = sidecarPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                try
                {
                    sidecarProcess = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    sidecarUnavailableReason = $"aer-sidecar failed to start: {ex.Message}";
                    Console.WriteLine(sidecarUnavailableReason);
                    return;
                }

                if (sidecarProcess == null) return;

                var logPath = Path.Combine(aerDir, "sidecar-spawn.log");
                try
                {
                    File.WriteAllText(logPath, $"--- spawn {DateTime.UtcNow:O} (args: {args}) ---{Environment.NewLine}");
                    sidecarProcess.OutputDataReceived += (_, e) => { if (e.Data != null) TryAppendSidecarLog(logPath, e.Data); };
                    sidecarProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) TryAppendSidecarLog(logPath, e.Data); };
                    sidecarProcess.BeginOutputReadLine();
                    sidecarProcess.BeginErrorReadLine();
                }
                catch { /* diagnostics only, never block a real spawn attempt */ }

                var startedProcess = sidecarProcess;
                _ = Task.Run(async () =>
                {
                    // Status port is OS-assigned, so it's only known once the sidecar writes it --
                    // same file-handoff convention as this daemon's own daemon.port. Not gated on
                    // tsnet's Up() completing (that can block indefinitely on first-run interactive
                    // auth): a sidecar that's alive and answering /status, but not yet Ready, still
                    // has to surface its AuthURL somewhere -- see sidecar-spawn.log.
                    for (var i = 0; i < 30; i++)
                    {
                        if (startedProcess.HasExited) return;
                        try
                        {
                            if (File.Exists(sidecarStatusPortFile))
                            {
                                var text = (await File.ReadAllTextAsync(sidecarStatusPortFile)).Trim();
                                if (int.TryParse(text, out var p))
                                {
                                    sidecarStatusPort = p;
                                    return;
                                }
                            }
                        }
                        catch { /* keep retrying */ }
                        await Task.Delay(200);
                    }
                }, CancellationToken.None);
            }

            // Must run before the auth middleware below: context.WebSockets.IsWebSocketRequest is
            // populated by this middleware (it wires up IHttpWebSocketFeature), not by Kestrel
            // directly. Registered afterward, it silently evaluated false for every WS handshake,
            // so the auth check fell through to the plain-Authorization-header branch — which the
            // WS client never sets (only ever the ?token= query string) — and every WS connection
            // was rejected with 401. Masked until now by a bare catch{} around the client's
            // connect call (TaskSession.StartWebSocketListenerAsync); found while building
            // Aer.Mobile (M21 Phase 2, #232), whose decision inbox depends entirely on this stream.
            app.UseWebSockets();

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
                            await SendStateAsync(webSocket, outcome.Projection, session.CurrentTaskDirectoryPath);
                        }
                    }

                    // Keep connection open
                    var buffer = new byte[1024 * 4];
                    try
                    {
                        while (webSocket.State == WebSocketState.Open)
                        {
                            // CancellationToken.None here meant this loop had no way to unblock on
                            // app shutdown — found live: SetRemoteEnabledAsync's shutdown-then-respawn
                            // toggle stalled for the full ~30s default graceful-shutdown grace period
                            // (HostOptions.ShutdownTimeout) before the host force-aborted this stuck
                            // connection, since the receive loop itself never observed shutdown at
                            // all. context.RequestAborted is signaled promptly on app shutdown as well
                            // as client disconnect, so this now unblocks immediately either way.
                            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
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

            // M24 Phase 1's live in-turn streaming WebSocket endpoint — see progressWebSockets'
            // remarks above for why this is separate from /api/ws rather than sharing it.
            app.Map("/api/ws/progress", async (HttpContext context) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    progressWebSockets.Add(webSocket);

                    var buffer = new byte[1024 * 4];
                    try
                    {
                        while (webSocket.State == WebSocketState.Open)
                        {
                            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
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
                HasRunningTasks = session.ShouldLiveRefresh,
                IsRemote = isRemote
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

            // Paired-device management (Phase 6, #243): revocation is a desktop-owner action, not
            // something a paired mobile client should be able to do to itself or to siblings — gated
            // to the local loopback token specifically, unlike most endpoints below which accept
            // either the local token or any paired client's token.
            bool IsLocalToken(HttpContext context)
            {
                var authHeader = context.Request.Headers["Authorization"].ToString();
                if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
                return SafeEquals(authHeader["Bearer ".Length..].Trim(), token);
            }

            app.MapGet("/api/pairing/clients", (HttpContext context, PairedClientsStore store) =>
            {
                if (!IsLocalToken(context))
                {
                    return Results.Json(new { Error = "Only the local desktop owner can list paired devices." }, statusCode: StatusCodes.Status403Forbidden);
                }

                var clients = store.ListClients()
                    .Select(c => new { c.ClientId, c.Name, c.PairedAt })
                    .ToList();
                return Results.Ok(clients);
            });

            app.MapDelete("/api/pairing/clients/{clientId}", (string clientId, HttpContext context, PairedClientsStore store) =>
            {
                if (!IsLocalToken(context))
                {
                    return Results.Json(new { Error = "Only the local desktop owner can revoke paired devices." }, statusCode: StatusCodes.Status403Forbidden);
                }

                return store.RemoveClient(clientId) ? Results.Ok() : Results.NotFound();
            });

            // Sidecar readiness/auth-URL surfacing (#242): loopback-owner-only, same gating as the
            // paired-clients endpoints above -- this is desktop setup state, not something a paired
            // mobile client needs. Proxies the sidecar's own /status rather than caching it, so it's
            // never stale relative to what the sidecar actually knows right now.
            app.MapGet("/api/remote/sidecar-status", async (HttpContext context) =>
            {
                if (!IsLocalToken(context))
                {
                    return Results.Json(new { Error = "Only the local desktop owner can view sidecar status." }, statusCode: StatusCodes.Status403Forbidden);
                }

                if (!isRemote)
                {
                    return Results.Ok(new { Ready = false, Error = "Remote access is off." });
                }

                if (sidecarUnavailableReason is { } reason)
                {
                    return Results.Ok(new { Ready = false, Error = reason });
                }

                if (sidecarStatusPort is not { } port)
                {
                    // No Error here -- absence of AuthUrl/Error/Ready is itself "still starting" to
                    // the client (RemoteViewModel.CurrentSidecarPhase's fallback case), not a
                    // distinct sentinel string to keep in sync between the two ends.
                    return Results.Ok(new { Ready = false });
                }

                try
                {
                    var response = await sidecarHttpClient.GetAsync($"http://127.0.0.1:{port}/status");
                    var body = await response.Content.ReadAsStringAsync();
                    return Results.Content(body, "application/json");
                }
                catch (Exception ex)
                {
                    return Results.Ok(new { Ready = false, Error = $"sidecar unreachable: {ex.Message}" });
                }
            });

            // Sidecar sign-out (#242 follow-up): the only way to disconnect the tsnet node used to
            // be deleting it from the Tailscale admin console and restarting Aer.Ui -- this proxies
            // the sidecar's own /forget, which logs the node out and immediately re-enters the
            // interactive-login flow (a fresh AuthUrl shows up on the next sidecar-status poll).
            app.MapPost("/api/remote/sidecar-forget", async (HttpContext context) =>
            {
                if (!IsLocalToken(context))
                {
                    return Results.Json(new { Error = "Only the local desktop owner can sign the sidecar out." }, statusCode: StatusCodes.Status403Forbidden);
                }

                if (sidecarStatusPort is not { } port)
                {
                    return Results.Json(new { Error = "Sidecar isn't running." }, statusCode: StatusCodes.Status409Conflict);
                }

                try
                {
                    var response = await sidecarHttpClient.PostAsync($"http://127.0.0.1:{port}/forget", null);
                    return response.IsSuccessStatusCode
                        ? Results.Accepted()
                        : Results.Json(new { Error = $"sidecar rejected forget: {response.StatusCode}" }, statusCode: StatusCodes.Status502BadGateway);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { Error = $"sidecar unreachable: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
                }
            });

            // REST endpoints
            app.MapGet("/api/templates", () =>
            {
                var templates = BuiltInWorkflowTemplates.Catalog;
                var availableVendors = VendorCliPresence.Probe();
                return Results.Ok(new { Templates = templates, AvailableVendors = availableVendors });
            });

            app.MapPost("/api/templates/run", async ([FromBody] RunTemplateRequest request, TaskSession session, BindingsPathHolder pathHolder) =>
            {
                if (string.IsNullOrWhiteSpace(request.TemplateId))
                {
                    return Results.BadRequest("TemplateId is required.");
                }

                var baseTasksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer", "tasks");
                var folderName = string.IsNullOrWhiteSpace(request.TaskName)
                    ? $"task-{DateTime.UtcNow:yyyyMMddHHmmss}"
                    : request.TaskName.Trim();
                var taskDirectoryPath = Path.GetFullPath(Path.Combine(baseTasksDir, folderName));
                var normalizedBaseTasksDir = Path.GetFullPath(baseTasksDir);
                if (!taskDirectoryPath.StartsWith(normalizedBaseTasksDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return Results.BadRequest("TaskName must be a simple folder name, not a path.");
                }

                try
                {
                    await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync(
                        request.TemplateId,
                        request.PrimaryAdapter ?? "claude",
                        request.SecondaryAdapter,
                        taskDirectoryPath,
                        request.CustomPrompt,
                        request.SecondaryCustomPrompt).ConfigureAwait(true);

                    var workflowFilePath = Path.Combine(taskDirectoryPath, "workflow.json");
                    var bindingsFilePath = Path.Combine(taskDirectoryPath, "bindings.json");

                    pathHolder.BindingsFilePath = bindingsFilePath;
                    session.SetCurrentTaskDirectory(taskDirectoryPath);
                    await session.RecordOpenedAsync(taskDirectoryPath).ConfigureAwait(true);
                    var outcome = await session.LoadAsync(taskDirectoryPath).ConfigureAwait(true);
                    if (outcome.Projection != null)
                    {
                        await BroadcastStateAsync(outcome.Projection, taskDirectoryPath).ConfigureAwait(true);
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await session.RunAsync(taskDirectoryPath, workflowFilePath, bindingsFilePath).ConfigureAwait(true);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error running template task in background: {ex}");
                        }
                    });

                    return Results.Ok(new { TaskDirectoryPath = taskDirectoryPath });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(ex.Message);
                }
            });

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
                    await BroadcastStateAsync(outcome.Projection, request.DirectoryPath);
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

                var revisionFilePath = request.RevisionFilePath;
                if (string.IsNullOrEmpty(revisionFilePath) && request.ArtifactReference != null)
                {
                    var referenceOutcome = await session.LoadAsync(request.DirectoryPath);
                    if (referenceOutcome.Projection is not { } referenceProjection)
                    {
                        return Results.BadRequest(referenceOutcome.ErrorMessage);
                    }

                    var referencedExecution = referenceProjection.Lineage.Executions.FirstOrDefault(
                        e => e.ExecutionId.Value == request.ArtifactReference.ExecutionId);
                    if (referencedExecution is null || !referencedExecution.OutputFiles.Contains(request.ArtifactReference.FileName))
                    {
                        return Results.BadRequest("ArtifactReference does not name a known output file for that execution.");
                    }

                    var outputDir = ArtifactManager.ResolveOutputDirectory(
                        Path.Combine(request.DirectoryPath, "artifacts"),
                        referencedExecution.ExecutionId);
                    var candidatePath = Path.Combine(outputDir, request.ArtifactReference.FileName);
                    if (File.Exists(candidatePath))
                    {
                        revisionFilePath = candidatePath;
                    }
                }

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
                            revisionFilePath,
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

            // M21 Phase 2 (#232): a client with no access to the daemon host's filesystem
            // (Aer.Mobile) otherwise has no way to see what it's approving — TaskProjection only
            // ever carries file *paths*, never bytes (HomeViewModel's desktop-side inbox preview
            // reads artifact content straight off local disk). fileName is validated against the
            // execution's own recorded OutputFiles rather than trusted as a raw path component,
            // the same containment guarantee that desktop-side preview already relies on. Text
            // content only — capped well above the Home inbox snippet's 400 chars, since a phone
            // has no "open the real file" fallback, but still bounded so one huge artifact can't
            // stall a slow LAN/cellular transfer.
            app.MapGet("/api/tasks/artifact", async (string directoryPath, string executionId, string fileName, TaskSession session) =>
            {
                if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(executionId) || string.IsNullOrEmpty(fileName))
                {
                    return Results.BadRequest("directoryPath, executionId, and fileName are required.");
                }

                var outcome = await session.LoadAsync(directoryPath);
                if (outcome.Projection is not { } projection)
                {
                    return Results.BadRequest(outcome.ErrorMessage);
                }

                var execution = projection.Lineage.Executions.FirstOrDefault(e => e.ExecutionId.Value == executionId);
                if (execution is null || !execution.OutputFiles.Contains(fileName))
                {
                    return Results.NotFound();
                }

                var outputDirectory = ArtifactManager.ResolveOutputDirectory(
                    Path.Combine(directoryPath, "artifacts"), execution.ExecutionId);

                try
                {
                    var content = await File.ReadAllTextAsync(Path.Combine(outputDirectory, fileName));
                    var truncated = content.Length > ArtifactPreviewMaxLength;
                    return Results.Ok(new
                    {
                        Content = truncated ? content[..ArtifactPreviewMaxLength] + "…" : content,
                        Truncated = truncated,
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Results.NotFound();
                }
            });

            // M24 Phase 1 (#262): Interactive Sessions (Chat) endpoints
            app.MapPost("/api/sessions/start", async ([FromBody] StartSessionRequest request, TaskSession session, BindingsPathHolder pathHolder, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                var adapter = string.IsNullOrWhiteSpace(request.Adapter) ? "claude" : request.Adapter.Trim().ToLowerInvariant();
                var sessionId = Guid.NewGuid().ToString("N")[..12];
                var taskDirectoryPath = InteractiveSessionMaterializer.ResolveTaskDirectoryPath(sessionId, request.TaskName, request.DirectoryPath);

                if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
                {
                    await KnownProjectsStore.AddOrUpdateProjectAsync(request.WorkingDirectory).ConfigureAwait(true);
                }

                var effectiveGrant = request.PermissionGrant;
                if (effectiveGrant == null && !string.IsNullOrWhiteSpace(request.WorkingDirectory))
                {
                    // Conservative default for codebase sessions (M24 Phase 3)
                    effectiveGrant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);
                }

                SessionMetadata metadata;
                try
                {
                    metadata = await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                        sessionId,
                        taskDirectoryPath,
                        adapter,
                        request.Model,
                        request.WorkingDirectory,
                        request.InitialMessage,
                        request.SafetyCeiling ?? InteractiveSessionMaterializer.DefaultSafetyCeiling,
                        effectiveGrant).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(ex.Message);
                }

                var bindingsFilePath = Path.Combine(taskDirectoryPath, "bindings.json");

                pathHolder.BindingsFilePath = bindingsFilePath;
                session.SetCurrentTaskDirectory(taskDirectoryPath);
                await session.RecordOpenedAsync(taskDirectoryPath).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(request.InitialMessage))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ExecuteSessionTurnAsync(session, taskDirectoryPath, metadata, request.InitialMessage, adapter, request.Model, isInitial: true, BroadcastStateAsync, adapters, BroadcastSessionProgressAsync).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error running initial session turn: {ex}");
                        }
                    });
                }
                else
                {
                    var outcome = await session.LoadAsync(taskDirectoryPath).ConfigureAwait(true);
                    if (outcome.Projection != null)
                    {
                        await BroadcastStateAsync(outcome.Projection, taskDirectoryPath).ConfigureAwait(true);
                    }
                }

                return Results.Ok(metadata);
            });

            app.MapPost("/api/sessions/send", async ([FromBody] SendSessionMessageRequest request, TaskSession session, BindingsPathHolder pathHolder, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return Results.BadRequest("Message is required.");
                }

                string? directoryPath = request.DirectoryPath;
                if (string.IsNullOrEmpty(directoryPath) && !string.IsNullOrEmpty(request.SessionId))
                {
                    var resolvedBySessionId = await ResolveSessionAsync(request.SessionId);
                    if (resolvedBySessionId != null)
                    {
                        directoryPath = resolvedBySessionId.Value.DirectoryPath;
                    }
                }

                if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
                {
                    return Results.BadRequest("DirectoryPath or valid SessionId is required.");
                }

                var metadataPath = Path.Combine(directoryPath, ".aer", "session.json");
                var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath).ConfigureAwait(true);
                if (metadata == null)
                {
                    return Results.BadRequest("Not a valid interactive session directory.");
                }

                pathHolder.BindingsFilePath = Path.Combine(directoryPath, "bindings.json");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteSessionTurnAsync(session, directoryPath, metadata, request.Message, request.Adapter, request.Model, isInitial: false, BroadcastStateAsync, adapters, BroadcastSessionProgressAsync).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error executing session message turn: {ex}");
                    }
                });

                return Results.Ok(new { SessionId = metadata.SessionId, TaskDirectoryPath = directoryPath });
            });

            app.MapGet("/api/sessions/{sessionId}", async (string sessionId) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }
                return Results.Ok(resolved.Value.Metadata);
            });

            app.MapGet("/api/sessions", async () =>
            {
                var baseSessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer", "sessions");
                if (!Directory.Exists(baseSessionsDir))
                {
                    return Results.Ok(Array.Empty<SessionMetadata>());
                }

                var list = new List<SessionMetadata>();
                foreach (var dir in Directory.GetDirectories(baseSessionsDir))
                {
                    var metadataPath = Path.Combine(dir, ".aer", "session.json");
                    if (File.Exists(metadataPath))
                    {
                        var meta = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath);
                        if (meta != null) list.Add(meta);
                    }
                }

                return Results.Ok(list.OrderByDescending(s => s.UpdatedAt));
            });

            // M24 Phase 2 (#263): Capabilities discovery & Session compact endpoints
            app.MapGet("/api/sessions/{sessionId}/commands", async (string sessionId, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var metadata = resolved.Value.Metadata;
                if (!adapters.TryGetValue(metadata.CurrentAdapter, out var adapter))
                {
                    adapter = adapters["claude"];
                }

                var capabilities = await adapter.DiscoverCapabilitiesAsync(metadata.WorkingDirectory);
                return Results.Ok(capabilities);
            });

            app.MapGet("/api/adapters/capabilities", async (string? adapter, string? workingDirectory, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                var name = string.IsNullOrWhiteSpace(adapter) ? "claude" : adapter.Trim().ToLowerInvariant();
                if (!adapters.TryGetValue(name, out var workerAdapter))
                {
                    workerAdapter = adapters["claude"];
                }

                var capabilities = await workerAdapter.DiscoverCapabilitiesAsync(workingDirectory);
                return Results.Ok(capabilities);
            });

            app.MapPost("/api/sessions/{sessionId}/compact", async (string sessionId, TaskSession session, BindingsPathHolder pathHolder, IReadOnlyDictionary<string, IWorkerAdapter> adapters) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var (directoryPath, metadata) = resolved.Value;
                pathHolder.BindingsFilePath = Path.Combine(directoryPath, "bindings.json");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var compactMsg = "/compact Please provide a concise summary of our conversation so far, including all key requirements, code changes, decisions, and current progress.";
                        await ExecuteSessionTurnAsync(session, directoryPath, metadata, compactMsg, metadata.CurrentAdapter, metadata.Model, isInitial: false, BroadcastStateAsync, adapters, BroadcastSessionProgressAsync, forceHandoff: true).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error executing session compact turn: {ex}");
                    }
                });

                return Results.Ok(new { SessionId = metadata.SessionId, Message = "Compacting session context in background." });
            });

            // M24 Phase 3 (#264): Known Projects Registry endpoints
            app.MapGet("/api/projects", async () =>
            {
                var projects = await KnownProjectsStore.LoadProjectsAsync();
                return Results.Ok(projects);
            });

            app.MapPost("/api/projects", async ([FromBody] RegisterProjectRequest request) =>
            {
                if (string.IsNullOrWhiteSpace(request.Path))
                {
                    return Results.BadRequest("Path is required.");
                }

                await KnownProjectsStore.AddOrUpdateProjectAsync(request.Path, request.FriendlyName);
                var projects = await KnownProjectsStore.LoadProjectsAsync();
                return Results.Ok(projects);
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

                        if (isRemote)
                        {
                            TryStartSidecar(uri.Port);
                        }
                    }
                }
            });

            // Windows doesn't reap child processes when the parent exits -- an orphaned sidecar
            // would keep holding its tsnet node and tailnet port. Covers both real shutdown and the
            // shutdown-then-respawn toggle (RemoteViewModel.ToggleRemoteAsync), since both go
            // through this same graceful-shutdown path.
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                try { sidecarProcess?.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            });

            await app.RunAsync();
            mutex?.Dispose();
        }

        // Session folders are only named "session-{sessionId}" when StartSessionRequest.TaskName is
        // omitted (the fallback name at MapPost "/api/sessions/start" above) -- a caller-supplied
        // TaskName (e.g. a human-readable title) produces a differently-named folder, so any lookup
        // by sessionId alone must not assume the fallback convention holds. Mirrors the scan
        // MapGet "/api/sessions" (list) already does per-directory, keyed by the persisted
        // SessionMetadata.SessionId instead of the folder name.
        private static async Task<(string DirectoryPath, SessionMetadata Metadata)?> ResolveSessionAsync(string sessionId)
        {
            var baseSessionsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer", "sessions");
            if (!Directory.Exists(baseSessionsDir))
            {
                return null;
            }

            foreach (var dir in Directory.GetDirectories(baseSessionsDir))
            {
                var metadataPath = Path.Combine(dir, ".aer", "session.json");
                if (!File.Exists(metadataPath))
                {
                    continue;
                }

                var metadata = await InteractiveSessionMaterializer.LoadMetadataAsync(metadataPath).ConfigureAwait(true);
                if (metadata != null && metadata.SessionId == sessionId)
                {
                    return (dir, metadata);
                }
            }

            return null;
        }

        private static async Task ExecuteSessionTurnAsync(
            TaskSession session,
            string directoryPath,
            SessionMetadata metadata,
            string userMessage,
            string? requestAdapter,
            string? requestModel,
            bool isInitial,
            Func<TaskProjection, string?, Task> broadcastStateAsync,
            IReadOnlyDictionary<string, IWorkerAdapter> adapters,
            Func<string, string, WorkerProgressEvent, Task> broadcastSessionProgressAsync,
            bool forceHandoff = false)
        {
            var targetAdapter = string.IsNullOrWhiteSpace(requestAdapter) ? metadata.CurrentAdapter : requestAdapter.Trim().ToLowerInvariant();
            bool isVendorChange = !string.Equals(targetAdapter, metadata.CurrentAdapter, StringComparison.OrdinalIgnoreCase);
            bool isCeilingReached = metadata.TurnCount >= metadata.SafetyCeiling;
            // Compact (POST /api/sessions/{id}/compact) forces this branch even for a same-vendor,
            // under-ceiling turn -- it must actually synthesize a summary and start a fresh native
            // session, not just forward "/compact" as an ordinary resumed message to the vendor's own
            // (unverified, vendor-owned) slash-command handling. See issue #263's original rationale.
            bool handoff = isVendorChange || isCeilingReached || forceHandoff;

            string promptTemplate;
            bool resumeSession;
            string? vendorSessionId;

            if (handoff)
            {
                promptTemplate = InteractiveSessionMaterializer.SynthesizeContextSummary(metadata.Turns, userMessage);
                resumeSession = false;
                vendorSessionId = string.Equals(targetAdapter, "claude", StringComparison.OrdinalIgnoreCase) ? Guid.NewGuid().ToString() : null;
            }
            else
            {
                promptTemplate = userMessage;
                resumeSession = !isInitial;
                vendorSessionId = metadata.CurrentVendorSessionId ?? (string.Equals(targetAdapter, "claude", StringComparison.OrdinalIgnoreCase) ? Guid.NewGuid().ToString() : null);
            }

            var logFilePath = string.Equals(targetAdapter, "gemini", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(directoryPath, ".aer", "agy-log.txt")
                : null;

            var bindingsFilePath = Path.Combine(directoryPath, "bindings.json");
            var existingBindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath).ConfigureAwait(false);

            WorkerBindingConfigEntry? existingEntry = existingBindings.TryGetValue(InteractiveSessionMaterializer.DefaultWorkerName, out var e) ? e : null;

            var contract = existingEntry?.Contract ?? new WorkerContract(
                WorkerName: InteractiveSessionMaterializer.DefaultWorkerName,
                RequiredInputs: [],
                ProducedOutputs: [new ProducedOutput(InteractiveSessionMaterializer.DefaultOutputFileName)],
                OptionalMetadata: []);

            var grant = existingEntry?.PermissionGrant ?? new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false);

            var updatedEntry = new WorkerBindingConfigEntry(
                Adapter: targetAdapter,
                Contract: contract,
                PromptTemplate: promptTemplate,
                Timeout: TimeSpan.FromMinutes(10),
                Model: requestModel ?? metadata.Model,
                PermissionGrant: grant,
                WorkingDirectory: metadata.WorkingDirectory,
                SessionId: vendorSessionId,
                ResumeSession: resumeSession,
                MinimalOverhead: metadata.MinimalOverhead,
                StreamJson: string.Equals(targetAdapter, "claude", StringComparison.OrdinalIgnoreCase),
                LogFilePath: logFilePath);

            var newBindings = new Dictionary<string, WorkerBindingConfigEntry>
            {
                [InteractiveSessionMaterializer.DefaultWorkerName] = updatedEntry
            };

            await WorkerBindingConfigWriter.SaveToFileAsync(newBindings, bindingsFilePath).ConfigureAwait(false);

            var workflowFilePath = Path.Combine(directoryPath, "workflow.json");

            // M24 Phase 1's live in-turn streaming: only worth the stdout-capture cost (Aer.Flow's
            // CoreDispatcher only captures stdout at all when a target's OnStdoutLine is non-null)
            // when this turn actually requested a structured streaming format. The raw-line callback
            // below runs synchronously on aer-core's native callback thread, under CoreDispatcher's
            // own lock (see CoreDispatcher.cs's StdoutChunk handling) -- it must never block or do
            // real work, so it only enqueues onto a bounded channel and returns immediately. A
            // separate pump task drains that channel, does the (adapter-owned, possibly
            // vendor-specific) parse via TryParseProgressEvent, and awaits the WebSocket broadcast in
            // order, entirely off that native thread.
            Action<string, string>? onWorkerStdoutLine = null;
            Channel<string>? progressLines = null;
            Task? progressPumpTask = null;

            if (updatedEntry.StreamJson && adapters.TryGetValue(targetAdapter, out var streamingAdapter))
            {
                var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(500)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
                progressLines = channel;
                onWorkerStdoutLine = (_, line) => channel.Writer.TryWrite(line);

                progressPumpTask = Task.Run(async () =>
                {
                    await foreach (var line in channel.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        if (streamingAdapter.TryParseProgressEvent(line, out var progressEvent) && progressEvent is not null)
                        {
                            await broadcastSessionProgressAsync(directoryPath, InteractiveSessionMaterializer.DefaultStepId, progressEvent).ConfigureAwait(false);
                        }
                    }
                });
            }

            try
            {
                if (isInitial)
                {
                    await session.RunAsync(directoryPath, workflowFilePath, bindingsFilePath, onWorkerStdoutLine: onWorkerStdoutLine).ConfigureAwait(false);
                }
                else
                {
                    var currentOutcome = await session.LoadAsync(directoryPath).ConfigureAwait(false);
                    if (currentOutcome.Projection is { } proj)
                    {
                        var execution = proj.Lineage.Executions.LastOrDefault(ex => ex.StepId?.Value == InteractiveSessionMaterializer.DefaultStepId);
                        var executionIdStr = execution?.ExecutionId.Value ?? Guid.NewGuid().ToString();
                        await session.DecideAsync(
                            directoryPath,
                            new StepId(InteractiveSessionMaterializer.DefaultStepId),
                            new ExecutionId(executionIdStr),
                            DecisionType.Supersede,
                            new StepId(InteractiveSessionMaterializer.DefaultStepId),
                            revisionFilePath: null,
                            supplementaryWorker: null,
                            supplementaryOutputName: null,
                            onWorkerStdoutLine: onWorkerStdoutLine).ConfigureAwait(false);
                    }
                    else
                    {
                        await session.RunAsync(directoryPath, workflowFilePath, bindingsFilePath, onWorkerStdoutLine: onWorkerStdoutLine).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (progressLines is not null)
                {
                    progressLines.Writer.Complete();
                    await progressPumpTask!.ConfigureAwait(false);
                }
            }

            // Capture Gemini conversation ID if turn 1 for Gemini
            if (string.Equals(targetAdapter, "gemini", StringComparison.OrdinalIgnoreCase) && vendorSessionId == null && logFilePath != null && File.Exists(logFilePath))
            {
                try
                {
                    var logText = await File.ReadAllTextAsync(logFilePath).ConfigureAwait(false);
                    var match = System.Text.RegularExpressions.Regex.Match(logText, @"conversation=([^\s\r\n]+)");
                    if (match.Success)
                    {
                        vendorSessionId = match.Groups[1].Value;
                    }
                }
                catch { }
            }

            // Read assistant response
            string? assistantResponse = null;
            var finalOutcome = await session.LoadAsync(directoryPath).ConfigureAwait(false);
            if (finalOutcome.Projection is { } finalProj)
            {
                var latestExecution = finalProj.Lineage.Executions.LastOrDefault(ex => ex.StepId?.Value == InteractiveSessionMaterializer.DefaultStepId);
                if (latestExecution != null)
                {
                    var outputDir = ArtifactManager.ResolveOutputDirectory(Path.Combine(directoryPath, "artifacts"), latestExecution.ExecutionId);
                    var responseFile = Path.Combine(outputDir, InteractiveSessionMaterializer.DefaultOutputFileName);
                    if (File.Exists(responseFile))
                    {
                        assistantResponse = await File.ReadAllTextAsync(responseFile).ConfigureAwait(false);
                    }
                }
                await broadcastStateAsync(finalProj, directoryPath).ConfigureAwait(false);
            }

            var newTurnIndex = metadata.TurnCount + 1;
            var turn = new SessionTurn(
                TurnIndex: newTurnIndex,
                Vendor: targetAdapter,
                HumanMessage: userMessage,
                AssistantResponse: assistantResponse,
                ExecutedAt: DateTimeOffset.UtcNow,
                NativeSessionResumed: resumeSession,
                VendorHandoffSynthesized: handoff);

            var updatedTurns = new List<SessionTurn>(metadata.Turns) { turn };
            var updatedTurnCount = isCeilingReached ? 1 : newTurnIndex;

            var updatedMetadata = metadata with
            {
                CurrentAdapter = targetAdapter,
                CurrentVendorSessionId = vendorSessionId,
                Model = requestModel ?? metadata.Model,
                TurnCount = updatedTurnCount,
                UpdatedAt = DateTimeOffset.UtcNow,
                Turns = updatedTurns
            };

            await InteractiveSessionMaterializer.SaveMetadataAsync(updatedMetadata, Path.Combine(directoryPath, ".aer", "session.json")).ConfigureAwait(false);
        }
    }

    public class PairRequest
    {
        public string Code { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
    }

    public record RegisterProjectRequest(string Path, string? FriendlyName = null);
}
