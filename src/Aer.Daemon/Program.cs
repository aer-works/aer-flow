using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
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

        public static async Task RunDaemonAsync(string[] args, IReadOnlyDictionary<string, IWorkerAdapter>? adapters = null, Action<WebApplication>? onBuilt = null)
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
            var aerDir = AerPaths.Root;
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
                else if (activePort == 0)
                {
                    // ListenLocalhost(0) throws InvalidOperationException ("Dynamic port binding is
                    // not supported when binding to localhost") -- it binds both the IPv4 and IPv6
                    // loopback interfaces, and a truly dynamic port can't be guaranteed identical on
                    // both (each bind(0) gets its own independently OS-assigned ephemeral port), so
                    // Kestrel refuses outright rather than silently pick one. This path is reachable
                    // both from an explicit `--port 0` (issue #296's test fixtures, so two daemon
                    // instances in concurrent test runs never fight over the same fixed port) and
                    // from the port-collision fallback just above (`activePort = 0` when the
                    // default/requested fixed port is already taken) -- loopback-only (IPv4) keeps
                    // that fallback actually usable instead of trading one crash for another.
                    options.Listen(System.Net.IPAddress.Loopback, 0);
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
            onBuilt?.Invoke(app);

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
            // see docs/runbooks/tailscale-cross-network-proof.md), so Kestrel keeps listening on
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

                var baseTasksDir = AerPaths.Tasks;
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

                // #330: unlike /api/tasks/open and /api/templates/run, this endpoint -- the one the
                // desktop's own TaskSession.RunAsync HTTP branch posts to -- never gave already-
                // connected clients (a paired phone) any immediate sign that a run just started here.
                // Best-effort and may no-op for a brand-new task (no snapshot.json until the pump
                // below binds one): the guaranteed broadcast is still the one RunAsync's own
                // reopenTaskAsync hook fires on completion. This closes the gap for the common case
                // this projection already exists -- a resumed/re-run task -- immediately instead of
                // only once the whole pump finishes.
                session.SetCurrentTaskDirectory(request.DirectoryPath);
                var immediateOutcome = await session.LoadAsync(request.DirectoryPath);
                if (immediateOutcome.Projection != null)
                {
                    await BroadcastStateAsync(immediateOutcome.Projection, request.DirectoryPath);
                }

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

            // M24 Phase 5 (#278): the fleet list — every known task/session directory's lightweight
            // status, scanning both ~/.aer/tasks (DAG workflow runs) and ~/.aer/sessions
            // (interactive chat/codebase sessions), the same two roots /api/templates/run and
            // /api/sessions already materialize into. Archived items are filtered out by default
            // (the everyday view); includeArchived=true surfaces them for the management screen.
            // A directory that fails to load (corrupt snapshot/log) is skipped rather than failing
            // the whole list, since one bad item shouldn't hide every other task/session.
            app.MapGet("/api/tasks", async (bool? includeArchived) =>
            {
                var baseTasksDir = AerPaths.Tasks;
                var baseSessionsDir = AerPaths.Sessions;

                var directories = new List<string>();
                if (Directory.Exists(baseTasksDir))
                {
                    directories.AddRange(Directory.GetDirectories(baseTasksDir));
                }
                if (Directory.Exists(baseSessionsDir))
                {
                    directories.AddRange(Directory.GetDirectories(baseSessionsDir));
                }

                var items = new List<TaskFleetItem>();
                foreach (var directory in directories)
                {
                    try
                    {
                        var item = await TaskProjectionLoader.LoadFleetStatusAsync(directory).ConfigureAwait(true);
                        if (item.IsArchived && includeArchived != true)
                        {
                            continue;
                        }
                        items.Add(item);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error loading fleet status for '{directory}': {ex}");
                    }
                }

                return Results.Ok(items);
            });

            app.MapPost("/api/tasks/archive", async ([FromBody] TaskDirectoryRequest request) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return Results.BadRequest("DirectoryPath is required.");
                }
                if (!TryResolveManagedTaskDirectory(request.DirectoryPath, out var resolvedPath))
                {
                    return Results.BadRequest("DirectoryPath must be inside ~/.aer/tasks or ~/.aer/sessions.");
                }

                await TaskLifecycle.ArchiveAsync(resolvedPath).ConfigureAwait(true);
                return Results.Ok();
            });

            app.MapPost("/api/tasks/unarchive", async ([FromBody] TaskDirectoryRequest request) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return Results.BadRequest("DirectoryPath is required.");
                }
                if (!TryResolveManagedTaskDirectory(request.DirectoryPath, out var resolvedPath))
                {
                    return Results.BadRequest("DirectoryPath must be inside ~/.aer/tasks or ~/.aer/sessions.");
                }

                await TaskLifecycle.UnarchiveAsync(resolvedPath).ConfigureAwait(true);
                return Results.Ok();
            });

            // A real delete frees the directory's name for reuse (TaskDirectoryAlreadyExistsException's
            // collision guard checks File.Exists on workflow.json, which archiving alone never
            // clears — see TaskLifecycle's remarks) and also strips the stale recent so a later
            // /api/tasks/recent-driven open doesn't 404 on a directory that no longer exists.
            app.MapPost("/api/tasks/delete", async ([FromBody] TaskDirectoryRequest request, LocalUiConfigurationStore configStore) =>
            {
                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return Results.BadRequest("DirectoryPath is required.");
                }
                if (!TryResolveManagedTaskDirectory(request.DirectoryPath, out var resolvedPath))
                {
                    return Results.BadRequest("DirectoryPath must be inside ~/.aer/tasks or ~/.aer/sessions.");
                }

                if (!Directory.Exists(resolvedPath))
                {
                    return Results.NotFound();
                }

                Directory.Delete(resolvedPath, recursive: true);
                await configStore.RemoveRecentTaskDirectoryAsync(resolvedPath).ConfigureAwait(true);
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
                        // #341: this turn runs fire-and-forget behind an already-returned 200, so a
                        // throw here reached Console.Error and nowhere else -- the client saw success
                        // and then silence forever, which is exactly how a stalled chat presents.
                        // Persist it next to the session so the failure survives the daemon and can
                        // be read after the fact; the console line alone is gone the moment CI's
                        // process exits, which is why this took a day to characterize.
                        Console.Error.WriteLine($"Error executing session message turn: {ex}");
                        await AppendTurnErrorAsync(directoryPath, request.Message, ex).ConfigureAwait(false);
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
                var baseSessionsDir = AerPaths.Sessions;
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
            app.MapGet("/api/sessions/{sessionId}/commands", async (string sessionId, IReadOnlyDictionary<string, IWorkerAdapter> adapters, LocalUiConfigurationStore configStore) =>
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
                var recentlyUsed = await configStore.LoadRecentCommandsAsync(metadata.CurrentAdapter);

                // RecentlyUsed is an additive sibling property, same idiom as the WS payload's
                // SessionId/DirectoryPath (PR #276) -- existing callers deserializing straight into
                // WorkerCapabilities are unaffected (unmapped JSON members are ignored by default).
                return Results.Ok(new
                {
                    capabilities.Vendor,
                    capabilities.Items,
                    capabilities.Models,
                    RecentlyUsed = recentlyUsed,
                });
            });

            app.MapPost("/api/sessions/{sessionId}/commands/record", async (string sessionId, [FromBody] RecordCommandUsedRequest request, LocalUiConfigurationStore configStore) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest("Name is required.");
                }

                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                await configStore.RecordCommandUsedAsync(resolved.Value.Metadata.CurrentAdapter, request.Name.Trim());
                return Results.Ok();
            });

            // Session-level mode (M24 Phase 2 follow-up): PermissionGrant already persists across
            // turns via bindings.json (ExecuteSessionTurnAsync reads the existing entry's grant each
            // turn), but nothing let a user change it mid-session -- it was fixed at whatever
            // /api/sessions/start set. This updates bindings.json directly so the *next* turn (any
            // vendor) picks up the new grant, translated per-vendor by that adapter's own existing
            // PermissionGrant translation.
            app.MapPost("/api/sessions/{sessionId}/mode", async (string sessionId, [FromBody] SetSessionModeRequest request) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var directoryPath = resolved.Value.DirectoryPath;
                var grant = request.Mode?.Trim().ToLowerInvariant() switch
                {
                    "auto" => new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, ShellCommandPatterns: [], NetworkAccess: true),
                    "plan" => new PermissionGrant(ReadFiles: true, WriteFiles: false, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false),
                    "default" => new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: false, ShellCommandPatterns: [], NetworkAccess: false),
                    _ => (PermissionGrant?)null,
                };

                if (grant == null)
                {
                    return Results.BadRequest("Mode must be one of: auto, default, plan.");
                }

                var bindingsFilePath = Path.Combine(directoryPath, "bindings.json");
                var existingBindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath).ConfigureAwait(true);
                if (!existingBindings.TryGetValue(InteractiveSessionMaterializer.DefaultWorkerName, out var existingEntry))
                {
                    return Results.NotFound();
                }

                var updatedBindings = new Dictionary<string, WorkerBindingConfigEntry>(existingBindings)
                {
                    [InteractiveSessionMaterializer.DefaultWorkerName] = existingEntry with { PermissionGrant = grant }
                };
                await WorkerBindingConfigWriter.SaveToFileAsync(updatedBindings, bindingsFilePath).ConfigureAwait(true);

                return Results.Ok();
            });

            // #286: the POST above changes the mode but nothing let a client learn what's
            // currently active -- mode itself lives only in bindings.json's PermissionGrant (there's
            // no separate "CurrentMode" field), so this reverse-maps the persisted grant back to one
            // of the three canonical mode names the POST above can produce, or "custom" for a grant
            // that doesn't match any of them (e.g. one set directly via /api/sessions/start's own
            // PermissionGrant parameter, bypassing this mode vocabulary entirely).
            app.MapGet("/api/sessions/{sessionId}/mode", async (string sessionId) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var bindingsFilePath = Path.Combine(resolved.Value.DirectoryPath, "bindings.json");
                var existingBindings = await WorkerBindingConfigParser.LoadFromFileAsync(bindingsFilePath).ConfigureAwait(true);
                if (!existingBindings.TryGetValue(InteractiveSessionMaterializer.DefaultWorkerName, out var existingEntry))
                {
                    return Results.NotFound();
                }

                var mode = existingEntry.PermissionGrant is { } grant
                    ? grant switch
                    {
                        { ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true } => "auto",
                        { ReadFiles: true, WriteFiles: false, RunShellCommands: false, NetworkAccess: false } => "plan",
                        { ReadFiles: true, WriteFiles: true, RunShellCommands: false, NetworkAccess: false } => "default",
                        _ => "custom",
                    }
                    : "custom";

                return Results.Ok(new { Mode = mode });
            });

            // #286: "clear" (unlike compact) never talks to the vendor -- it's a purely local reset
            // so the *next* turn starts a genuinely fresh native session, mirroring exactly what
            // /api/sessions/start's own materialization does for a brand new session (same
            // fresh-GUID-per-adapter minting, VendorSessionEstablished reset to false so
            // ExecuteSessionTurnAsync's #285 resume-gating correctly picks `--session-id` over
            // `--resume` on that next turn instead of trying to resume an id the vendor never
            // established). Turns are cleared immediately so the UI reflects "cleared" without
            // waiting on any background work.
            app.MapPost("/api/sessions/{sessionId}/clear", async (string sessionId) =>
            {
                var resolved = await ResolveSessionAsync(sessionId);
                if (resolved == null)
                {
                    return Results.NotFound();
                }

                var (directoryPath, metadata) = resolved.Value;
                var freshVendorSessionId = string.Equals(metadata.CurrentAdapter, "claude", StringComparison.OrdinalIgnoreCase)
                    ? Guid.NewGuid().ToString()
                    : null;

                var cleared = metadata with
                {
                    Turns = [],
                    TurnCount = 0,
                    CurrentVendorSessionId = freshVendorSessionId,
                    VendorSessionEstablished = false,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                var metadataPath = Path.Combine(directoryPath, ".aer", "session.json");
                await InteractiveSessionMaterializer.SaveMetadataAsync(cleared, metadataPath).ConfigureAwait(true);

                return Results.Ok(cleared);
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
        // Review follow-up (issue #250's containment fix, applied here too): DirectoryPath is a
        // caller-supplied path reaching real filesystem mutation (archive/unarchive marker writes,
        // and delete's recursive Directory.Delete) via remote-reachable endpoints (mobile's
        // DaemonClient.deleteTask() included) -- an unchecked path here is a strictly worse version
        // of #250's RunTemplate TaskName traversal, since delete needs no traversal trick at all,
        // just any absolute path. Every fleet item this API surfaces is itself a direct child of
        // one of these two roots (Directory.GetDirectories in the /api/tasks handler above), so
        // requiring the resolved path be contained within one of them costs nothing legitimate.
        private static bool TryResolveManagedTaskDirectory(string directoryPath, out string resolvedPath)
        {
            resolvedPath = Path.GetFullPath(directoryPath);

            var baseTasksDir = Path.GetFullPath(AerPaths.Tasks);
            var baseSessionsDir = Path.GetFullPath(AerPaths.Sessions);

            return resolvedPath.StartsWith(baseTasksDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || resolvedPath.StartsWith(baseSessionsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static async Task<(string DirectoryPath, SessionMetadata Metadata)?> ResolveSessionAsync(string sessionId)
        {
            var baseSessionsDir = AerPaths.Sessions;
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

        /// <summary>
        /// #341: appends a background turn failure to <c>.aer/turn-errors.log</c> in the session
        /// directory. <c>POST /api/sessions/send</c> answers before the turn runs, so nothing it
        /// returns can carry a later failure, and <c>Console.Error</c> dies with the process --
        /// leaving a stalled chat with no recoverable evidence anywhere. Best-effort by
        /// construction: this runs inside a catch, so it must never throw over the top of the
        /// original error.
        /// </summary>
        private static async Task AppendTurnErrorAsync(string directoryPath, string userMessage, Exception error)
        {
            try
            {
                var aerDir = Path.Combine(directoryPath, ".aer");
                Directory.CreateDirectory(aerDir);
                var line = $"{DateTimeOffset.UtcNow:O}\tmessage={userMessage.ReplaceLineEndings(" ")}\t{error}";
                await File.AppendAllTextAsync(Path.Combine(aerDir, "turn-errors.log"), line + Environment.NewLine).ConfigureAwait(false);
            }
            catch (Exception recordError)
            {
                Console.Error.WriteLine($"Could not persist session turn error: {recordError}");
            }
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
                // #285: CurrentVendorSessionId is minted client-side at materialization time, before
                // the vendor CLI has ever heard of it -- it's non-null from turn zero, so "isInitial"
                // was standing in for "has the vendor actually established this id" and is wrong
                // whenever a session starts with no InitialMessage (the normal chat-page flow): the
                // very first /api/sessions/send call had isInitial=false and went straight to
                // `--resume <unestablished-guid>`, which claude rejects outright ("No conversation
                // found"), permanently wedging every later turn on the same dead id. Gate on whether a
                // turn has actually succeeded against this id instead.
                resumeSession = !isInitial && metadata.VendorSessionEstablished;
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

            // #285: must start from the existing bindings, not a fresh dictionary containing only
            // "chat-worker" -- a full replacement here silently dropped the anchor step's own
            // binding entry (written once at materialization, never touched by any per-turn
            // rewrite) after the very first turn, leaving "turn-anchor-worker" unresolvable and the
            // anchor step's dispatch throwing UnresolvedWorkerException deep inside the pump. That
            // exception was itself silently swallowed (TaskSession.RunAsync's in-process fallback
            // catches AerFlowException into an unchecked MutationOutcome, and neither call site
            // below checked it) -- chat would still succeed before the pump ever reached the
            // now-unresolvable anchor, so the turn looked like it worked right up until the anchor
            // never dispatched and never paused, wedging every later turn's Supersede target.
            var newBindings = new Dictionary<string, WorkerBindingConfigEntry>(existingBindings)
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
            // #285: a failed execution's stderr never reaches this process (aer-core's P/Invoke
            // boundary doesn't surface it), but a failed `claude --output-format stream-json` call
            // still prints its error as the final stdout line (`{"type":"result",...,"errors":[...]}`)
            // before exiting non-zero -- captured here so a failure stops looking like silence.
            var rawStdoutCapture = new StringBuilder();

            if (updatedEntry.StreamJson && adapters.TryGetValue(targetAdapter, out var streamingAdapter))
            {
                var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(500)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
                progressLines = channel;
                onWorkerStdoutLine = (_, line) =>
                {
                    channel.Writer.TryWrite(line);
                    rawStdoutCapture.AppendLine(line);
                };

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
                    var runOutcome = await session.RunAsync(directoryPath, workflowFilePath, bindingsFilePath, onWorkerStdoutLine: onWorkerStdoutLine).ConfigureAwait(false);
                    if (runOutcome.ErrorMessage is { } runError)
                    {
                        // #285: RunAsync's in-process fallback catches AerFlowException into an
                        // unchecked MutationOutcome -- an unresolvable binding or similar dispatch
                        // failure would otherwise vanish silently here exactly as it did before this
                        // check existed, leaving whatever the pump had already dispatched (e.g. a
                        // successful "chat" execution reached before the pump hit the failure) looking
                        // like a complete, healthy turn.
                        throw new InvalidOperationException($"Chat turn run failed: {runError}");
                    }
                }
                else
                {
                    // #285: "chat" superseding itself is spec-illegal (§17.1 -- a Supersede target
                    // must be a distinct transitive ancestor; a single self-referencing step has
                    // none), which is why every turn after the first silently no-opped -- the
                    // validator's rejection was swallowed by DecideAsync's in-process fallback, and
                    // ExecuteSessionTurnAsync fell through to re-read the *previous* turn's stale
                    // response.md as if it were a fresh answer. InteractiveSessionMaterializer now
                    // builds a two-step DAG: "chat" itself declares no PausePoint (so a successful
                    // turn flows straight through, uninterrupted), and a downstream "turn-anchor" step
                    // (DependsOn: [chat]) declares the PausePoint with SupersedeTargets: [chat] --
                    // exactly the shape spec §17.5's own Architect/Critic example uses. Anchor sitting
                    // Paused is what makes a Supersede against "chat" legal; its own successful rerun
                    // (triggered automatically by §11.3 condition 2 once chat's new execution
                    // succeeds) lands it paused again, ready for the next turn.
                    var currentOutcome = await session.LoadAsync(directoryPath).ConfigureAwait(false);
                    var anchorState = currentOutcome.Projection?.State.Steps
                        .SingleOrDefault(s => s.StepId.Value == InteractiveSessionMaterializer.AnchorStepId);

                    if (anchorState is { Status: StepStatus.Paused, LatestExecutionId: { } anchorExecutionId })
                    {
                        // Ordinary continuation (including handoff turns, which just carry a
                        // different promptTemplate/vendorSessionId computed above): supply this
                        // turn's message as the mandatory supplementary human-tier artifact (§17.3)
                        // and Supersede "chat" via anchor's currently-paused execution.
                        var messageFilePath = Path.Combine(directoryPath, ".aer", "pending-turn-message.txt");
                        Directory.CreateDirectory(Path.GetDirectoryName(messageFilePath)!);
                        await File.WriteAllTextAsync(messageFilePath, userMessage).ConfigureAwait(false);

                        var decideOutcome = await session.DecideAsync(
                            directoryPath,
                            new StepId(InteractiveSessionMaterializer.AnchorStepId),
                            anchorExecutionId,
                            DecisionType.Supersede,
                            targetStepId: new StepId(InteractiveSessionMaterializer.DefaultStepId),
                            revisionFilePath: messageFilePath,
                            supplementaryWorker: "human",
                            supplementaryOutputName: "message.txt",
                            onWorkerStdoutLine: onWorkerStdoutLine).ConfigureAwait(false);

                        if (decideOutcome.ErrorMessage is { } decideError)
                        {
                            throw new InvalidOperationException($"Chat turn decision (Supersede) was rejected: {decideError}");
                        }
                    }
                    else
                    {
                        // #354: reaching here means the anchor is not observably Paused -- but the old
                        // code treated that as an unconditional "nothing of value exists, delete and
                        // re-run", a two-way test over a multi-state DAG. Re-materializing wipes this
                        // task's snapshot.json / flow.jsonl / artifacts, so it is only safe when the
                        // flow genuinely has no live state to lose or corrupt (see
                        // IsSessionSafeToReMaterialize): nothing Running (the anchor's own rerun is
                        // auto-triggered by §11.3 condition 2 once "chat" succeeds, so there is a
                        // Running window), nothing Paused (a continuation a stale projection can
                        // momentarily hide), and no already-succeeded "chat" still awaiting its anchor.
                        // When that holds it is the very first turn of a no-InitialMessage session, a
                        // first turn that failed outright, or the documented mid-conversation-failure
                        // recovery -- all cases where only Flow's internal snapshot/log/artifacts are
                        // replaced while SessionMetadata's own Turns transcript and
                        // VendorSessionEstablished (which carry real continuity) stay untouched.
                        // Otherwise a live session's entire event log would be destroyed: refuse and
                        // surface it rather than betting the wrong way.
                        if (!IsSessionSafeToReMaterialize(currentOutcome.Projection?.State.Steps, metadata.Turns.Count))
                        {
                            throw new InvalidOperationException(
                                "Chat turn found the session anchor not resolved to a paused state, but " +
                                "the flow still holds live state (a step is Running or Paused, or a " +
                                "succeeded \"chat\" step is awaiting its anchor). Refusing to " +
                                "re-materialize, which would delete this session's live flow log, " +
                                "snapshot and artifacts (#354). Retry once the current turn has settled.");
                        }

                        var snapshotPath = Path.Combine(directoryPath, "snapshot.json");
                        var flowLogPath = Path.Combine(directoryPath, "flow.jsonl");
                        var artifactsPath = Path.Combine(directoryPath, "artifacts");
                        if (File.Exists(snapshotPath))
                        {
                            File.Delete(snapshotPath);
                        }

                        if (File.Exists(flowLogPath))
                        {
                            File.Delete(flowLogPath);
                        }

                        if (Directory.Exists(artifactsPath))
                        {
                            Directory.Delete(artifactsPath, recursive: true);
                        }

                        var runOutcome = await session.RunAsync(directoryPath, workflowFilePath, bindingsFilePath, onWorkerStdoutLine: onWorkerStdoutLine).ConfigureAwait(false);
                        if (runOutcome.ErrorMessage is { } runError)
                        {
                            // #285: RunAsync's in-process fallback catches AerFlowException into an
                            // unchecked MutationOutcome -- an unresolvable binding or similar dispatch
                            // failure would otherwise vanish silently here exactly as it did before
                            // this check existed, leaving whatever the pump had already dispatched
                            // (e.g. a successful "chat" execution reached before the pump hit the
                            // failure) looking like a complete, healthy turn.
                            throw new InvalidOperationException($"Chat turn run failed: {runError}");
                        }
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

            var establishedThisTurn = assistantResponse != null;
            var errorMessage = establishedThisTurn ? null : TryExtractVendorErrorMessage(rawStdoutCapture.ToString());

            var newTurnIndex = metadata.TurnCount + 1;
            var turn = new SessionTurn(
                TurnIndex: newTurnIndex,
                Vendor: targetAdapter,
                HumanMessage: userMessage,
                AssistantResponse: assistantResponse,
                ExecutedAt: DateTimeOffset.UtcNow,
                NativeSessionResumed: resumeSession,
                VendorHandoffSynthesized: handoff,
                ErrorMessage: errorMessage);

            var updatedTurns = new List<SessionTurn>(metadata.Turns) { turn };
            var updatedTurnCount = isCeilingReached ? 1 : newTurnIndex;

            var updatedMetadata = metadata with
            {
                CurrentAdapter = targetAdapter,
                CurrentVendorSessionId = vendorSessionId,
                Model = requestModel ?? metadata.Model,
                TurnCount = updatedTurnCount,
                UpdatedAt = DateTimeOffset.UtcNow,
                Turns = updatedTurns,
                // #285: a handoff mints a brand-new vendorSessionId, so prior establishment doesn't
                // carry over -- only this turn's own outcome counts for it. Otherwise, once
                // established stays established even if a later turn fails for an unrelated reason
                // (rate limit, transient network blip) -- the id itself is still real and resumable.
                VendorSessionEstablished = handoff ? establishedThisTurn : (metadata.VendorSessionEstablished || establishedThisTurn)
            };

            await InteractiveSessionMaterializer.SaveMetadataAsync(updatedMetadata, Path.Combine(directoryPath, ".aer", "session.json")).ConfigureAwait(false);
        }

        /// <summary>
        /// #354: decides whether a chat turn that could not resolve its <c>turn-anchor</c> to a Paused
        /// state may safely re-materialize the session -- delete <c>flow.jsonl</c>/<c>snapshot.json</c>/
        /// <c>artifacts</c> and re-run from scratch. Only true when the flow has no live state to lose
        /// or corrupt:
        /// <list type="bullet">
        /// <item>no step is <see cref="StepStatus.Running"/> -- the anchor's own rerun (auto-triggered
        /// by spec §11.3 condition 2 once <c>chat</c> succeeds) may be in flight, and deleting races a
        /// live write;</item>
        /// <item>no step is <see cref="StepStatus.Paused"/> -- a paused step is a continuation that
        /// should be Superseded, not wiped, and a stale projection can momentarily hide a real paused
        /// anchor;</item>
        /// <item>the <c>chat</c> step is not <see cref="StepStatus.Succeeded"/> -- a succeeded chat
        /// whose anchor rerun simply hasn't fired yet is a healthy turn, not a stuck one.</item>
        /// </list>
        /// A null/empty projection is "nothing of value" only for a brand-new session (no recorded
        /// turns); for an established one it means a lagging or failed read, where deleting is data
        /// loss. This is a decision over a single state snapshot: it makes the delete refuse in the
        /// unsafe states, but it cannot by itself close the underlying read-then-delete race (that
        /// needs per-session turn serialisation, tracked separately) -- the guarantee it gives is by
        /// construction, not by a test that could deterministically reproduce a live Running anchor
        /// against a synchronous stub.
        /// </summary>
        internal static bool IsSessionSafeToReMaterialize(IReadOnlyList<StepState>? steps, int recordedTurnCount)
        {
            if (steps is null || steps.Count == 0)
            {
                return recordedTurnCount == 0;
            }

            if (steps.Any(s => s.Status is StepStatus.Running or StepStatus.Paused))
            {
                return false;
            }

            var chatStep = steps.SingleOrDefault(s => s.StepId.Value == InteractiveSessionMaterializer.DefaultStepId);
            return chatStep?.Status is not StepStatus.Succeeded;
        }

        /// <summary>
        /// Best-effort extraction of a human-readable failure reason from a failed
        /// <c>claude --output-format stream-json</c> turn's captured stdout (#285). A failed turn's
        /// final line is a <c>{"type":"result","is_error":true,"errors":[...]}</c> object (confirmed
        /// live: <c>--resume</c> of an unestablished session id prints exactly
        /// <c>"No conversation found with session ID: &lt;guid&gt;"</c> this way, on stdout, not
        /// stderr) -- scanned from the end since it's always the last line the CLI writes before
        /// exiting. Falls back to the raw last non-empty line, then a generic message, so a caller
        /// never has to null-check this to render *something*.
        /// </summary>
        internal static string TryExtractVendorErrorMessage(string rawStdout)
        {
            var lines = rawStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                try
                {
                    var node = JsonNode.Parse(line);
                    if (node?["type"]?.GetValue<string>() != "result")
                    {
                        continue;
                    }

                    if (node["errors"] is JsonArray errors && errors.Count > 0)
                    {
                        return string.Join("; ", errors.Select(e => e?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)));
                    }

                    if (node["is_error"]?.GetValue<bool>() == true && node["result"] is { } resultText)
                    {
                        return resultText.ToString();
                    }
                }
                catch (JsonException)
                {
                    // Not a JSON result line -- keep scanning backward.
                }
            }

            var lastLine = lines.Length > 0 ? lines[^1] : null;
            return string.IsNullOrWhiteSpace(lastLine)
                ? "The vendor process exited without producing a response."
                : lastLine;
        }
    }

    public class PairRequest
    {
        public string Code { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
    }

    public record RegisterProjectRequest(string Path, string? FriendlyName = null);

    /// <summary>M24 Phase 2 follow-up: records a picked skill/command/agent as this vendor's most-recently-used, via <see cref="LocalUiConfigurationStore.RecordCommandUsedAsync"/>.</summary>
    public record RecordCommandUsedRequest(string Name);

    /// <summary>
    /// Session-level mode (M24 Phase 2 follow-up, per discussion with the owner): a vendor-neutral
    /// permission mode settable mid-session, applying to whichever vendor is currently active --
    /// distinct from <see cref="StartSessionRequest.PermissionGrant"/>, which only ever applies at
    /// session creation. <paramref name="Mode"/> is one of "auto" (maximally permissive -- Claude's
    /// full <c>Read,Edit,Write,Bash,WebFetch,WebSearch</c> grant, Gemini's <c>accept-edits</c>),
    /// "default" (this session's original grant), or "plan" (read-only -- <see cref="PermissionGrant.WriteFiles"/>/<see cref="PermissionGrant.RunShellCommands"/> both off).
    /// </summary>
    public record SetSessionModeRequest(string Mode);
}
