using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Violens.ProxyHost.Client
{
    /// <summary>
    /// Runs VioLens's proxy engine in its own .NET 10 process
    /// (Violens.ProxyHost) and talks to it over a named pipe, so an app on
    /// .NET Framework can use it. See docs/Embedding ProxyCore.md.
    ///
    /// <code>
    /// using var proxy = await ProxyHostClient.LaunchAsync(@"ProxyHost\Violens.ProxyHost.exe");
    /// proxy.SessionCaptured += (s, e) =&gt; BeginInvoke(() =&gt; AddRow(e.Session));
    /// proxy.OnBeforeRequest = ctx =&gt; ctx.Request.SetHeader("X-Debugged-By", "My App");
    /// var started = await proxy.StartAsync(new AppIdentity("My App"),
    ///     new StartOptions { UseSystemProxy = true, HookRequests = true });
    /// </code>
    ///
    /// <b>Threads.</b> <see cref="SessionCaptured"/>, <see cref="Log"/>,
    /// <see cref="BreakpointHit"/> and <see cref="Disconnected"/> are raised
    /// on the client's own reader thread, in order: keep handlers quick and
    /// marshal to the UI thread (<c>Control.BeginInvoke</c>), never
    /// <c>Invoke</c>. Hooks run on thread-pool threads, several at once.
    ///
    /// <b>Lifetime.</b> The host stops capturing, puts the system proxy back
    /// and exits when this client is disposed, when the pipe breaks, or when
    /// the app's process ends.
    /// </summary>
    public sealed class ProxyHostClient : IDisposable
    {
        private readonly Stream _stream;
        private readonly Process? _process;
        private readonly object _writeLock = new object();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<Reply>> _pending = new ConcurrentDictionary<int, TaskCompletionSource<Reply>>();
        private readonly TaskCompletionSource<string> _hello = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _reader;
        private int _nextRequestId;
        private int _disposed;

        private ProxyHostClient(Stream stream, Process? process)
        {
            _stream = stream;
            _process = process;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Violens.ProxyHost reader" };
        }

        /// <summary>Raised for each request and response once it's finished.</summary>
        public event EventHandler<SessionCapturedEventArgs>? SessionCaptured;

        /// <summary>Raised for each message from the engine's log.</summary>
        public event EventHandler<ProxyLogEventArgs>? Log;

        /// <summary>Raised when a request or response matches the <see cref="SetBreakpointsAsync"/> settings and is paused.</summary>
        public event EventHandler<BreakpointEventArgs>? BreakpointHit;

        /// <summary>Raised once, when the host goes away (it exited, crashed, or this client was disposed).</summary>
        public event EventHandler? Disconnected;

        /// <summary>
        /// Like FiddlerScript's <c>OnBeforeRequest</c>: sees, and can change
        /// or answer, each request before it's sent. Only called when the
        /// capture was started with <see cref="StartOptions.HookRequests"/>.
        /// An exception is logged and the request goes on unchanged.
        /// </summary>
        public Action<HookContext>? OnBeforeRequest { get; set; }

        /// <summary>
        /// Like FiddlerScript's <c>OnBeforeResponse</c>: sees, and can change,
        /// each response before it's returned. Only called when the capture
        /// was started with <see cref="StartOptions.HookResponses"/>.
        /// </summary>
        public Action<HookContext>? OnBeforeResponse { get; set; }

        /// <summary>The host process, when this client launched it.</summary>
        public Process? HostProcess => _process;

        /// <summary>
        /// Starts the host and connects to it. <paramref name="hostPath"/> is
        /// <c>Violens.ProxyHost.exe</c> (or <c>Violens.ProxyHost.dll</c>, run
        /// with <c>dotnet</c>). The host needs the .NET 10 runtime unless it
        /// was published self-contained.
        /// </summary>
        /// <exception cref="TimeoutException">The host didn't connect in time (30 seconds by default).</exception>
        public static async Task<ProxyHostClient> LaunchAsync(string hostPath, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(hostPath))
            {
                throw new FileNotFoundException("The proxy host wasn't found.", hostPath);
            }

            // Short: on macOS and Linux a pipe is a socket file under the temp
            // folder, and those paths are limited to about 100 characters.
            var pipeName = "vlph-" + Guid.NewGuid().ToString("N").Substring(0, 16);
            var token = NewToken();
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var arguments = "--pipe " + pipeName + " --token " + token + " --parent " + Process.GetCurrentProcess().Id;
            var startInfo = hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("dotnet", "\"" + hostPath + "\" " + arguments)
                : new ProcessStartInfo(hostPath, arguments);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            Process? process = null;
            try
            {
                process = Process.Start(startInfo) ?? throw new InvalidOperationException("The proxy host didn't start.");

                var connect = pipe.WaitForConnectionAsync(cancellationToken);
                var finished = await Task.WhenAny(connect, Task.Delay(timeout ?? TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
                if (finished != connect)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(process.HasExited
                        ? $"The proxy host exited (code {process.ExitCode}) before connecting."
                        : "The proxy host didn't connect in time.");
                }

                await connect.ConfigureAwait(false);
                return await ConnectAsync(pipe, token, process, timeout ?? TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                TryKill(process);
                throw;
            }
        }

        /// <summary>
        /// 128 random bits from the cryptographic generator, as hex. The host
        /// sends it back, proving to this app that it's the process it
        /// started. A GUID's randomness isn't a documented guarantee.
        /// </summary>
        private static string NewToken()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        /// <summary>
        /// Connects to a host over a stream that's already open, for tests or
        /// for starting the host some other way. <paramref name="token"/> must
        /// be the one the host was given (<c>--token</c>).
        /// </summary>
        public static Task<ProxyHostClient> ConnectAsync(Stream stream, string token, TimeSpan? timeout = null) =>
            ConnectAsync(stream, token, null, timeout ?? TimeSpan.FromSeconds(30));

        private static async Task<ProxyHostClient> ConnectAsync(Stream stream, string token, Process? process, TimeSpan timeout)
        {
            var client = new ProxyHostClient(stream, process);
            client._reader.Start();

            var hello = client._hello.Task;
            if (await Task.WhenAny(hello, Task.Delay(timeout)).ConfigureAwait(false) != hello)
            {
                client.Dispose();
                throw new TimeoutException("The proxy host didn't say hello in time.");
            }

            string received;
            try
            {
                received = await hello.ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            if (!string.Equals(received, token, StringComparison.Ordinal))
            {
                client.Dispose();
                throw new InvalidOperationException("Something other than the proxy host this app started answered on its pipe.");
            }

            return client;
        }

        /// <summary>
        /// Starts capturing: sets the app's identity, creates (and, by
        /// default, trusts) the root certificate, starts listening and, if
        /// asked, becomes the system proxy. On Windows, trusting the
        /// certificate the first time shows the system's prompt, so this can
        /// take as long as the user does.
        /// </summary>
        public async Task<StartResult> StartAsync(AppIdentity identity, StartOptions options, CancellationToken cancellationToken = default)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var reply = await SendAsync(MessageType.Start, writer =>
            {
                writer.Write(identity.ProductName);
                writer.Write(identity.ProductVersion ?? string.Empty);
                writer.WriteNullableString(identity.RootCertificateName);
                writer.WriteNullableString(identity.DataFolder);
                writer.Write(options.PreferredPort);
                writer.Write(options.AllowRemoteClients);
                writer.Write(options.UseSystemProxy);
                writer.Write(options.TrustRootCertificate);
                writer.Write(options.HookRequests);
                writer.Write(options.HookResponses);
                writer.Write((int)Math.Max(0, Math.Min(int.MaxValue, options.HookTimeout.TotalMilliseconds)));
                writer.Write(options.IncludeBodies);
            }, cancellationToken).ConfigureAwait(false);

            return new StartResult(reply.Port, reply.RootCertificate ?? new byte[0], reply.Upstream);
        }

        /// <summary>Stops capturing and puts the system proxy back. Paused messages go on unchanged.</summary>
        public Task StopAsync(CancellationToken cancellationToken = default) =>
            SendAsync(MessageType.Stop, _ => { }, cancellationToken);

        /// <summary>Sets when to pause requests and responses. Takes effect at once, captured or not.</summary>
        public Task SetBreakpointsAsync(BreakpointSettings settings, CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return SendAsync(MessageType.SetBreakpoints, writer =>
            {
                writer.Write(settings.BreakOnAllRequests);
                writer.Write(settings.BreakOnAllResponses);
                writer.WriteNullableString(settings.RequestUrlContains);
                writer.WriteNullableString(settings.ResponseUrlContains);
                writer.WriteNullableString(settings.RequestMethodEquals);
                writer.Write(settings.ResponseStatusCodeEquals.HasValue);
                writer.Write(settings.ResponseStatusCodeEquals ?? 0);
            }, cancellationToken);
        }

        /// <summary>Saves every captured session to a SAZ file that Fiddler Classic and VioLens open. Returns how many.</summary>
        public async Task<int> SaveSazAsync(string path, CancellationToken cancellationToken = default) =>
            (await SendAsync(MessageType.SaveSaz, writer => writer.Write(path), cancellationToken).ConfigureAwait(false)).Count;

        /// <summary>
        /// Opens a SAZ file. Its sessions arrive through <see cref="SessionCaptured"/>
        /// before this completes. Returns how many were read.
        /// </summary>
        public async Task<int> LoadSazAsync(string path, CancellationToken cancellationToken = default) =>
            (await SendAsync(MessageType.LoadSaz, writer => writer.Write(path), cancellationToken).ConfigureAwait(false)).Count;

        /// <summary>Forgets every captured session, so the next SAZ save starts afresh.</summary>
        public Task ClearSessionsAsync(CancellationToken cancellationToken = default) =>
            SendAsync(MessageType.ClearSessions, _ => { }, cancellationToken);

        /// <summary>Removes the root certificate from the user's certificate stores, for a "Remove certificate" command or an uninstaller. Start must have run.</summary>
        public Task UninstallRootCertificateAsync(CancellationToken cancellationToken = default) =>
            SendAsync(MessageType.UninstallRootCertificate, _ => { }, cancellationToken);

        /// <summary>Closes the pipe, which makes the host stop capturing, put the system proxy back and exit.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _stream.Dispose();
            }
            catch (IOException)
            {
            }

            if (_process != null)
            {
                // The host exits once the pipe closes; give it a moment to
                // put the system proxy back before making sure it's gone.
                try
                {
                    if (!_process.WaitForExit(5000))
                    {
                        TryKill(_process);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                _process.Dispose();
            }

            FailPending(new ObjectDisposedException(nameof(ProxyHostClient)));
        }

        private async Task<Reply> SendAsync(MessageType type, Action<BinaryWriter> writeFields, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(ProxyHostClient));
            }

            var id = Interlocked.Increment(ref _nextRequestId);
            var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                Protocol.WriteFrame(_stream, _writeLock, type, writer =>
                {
                    writer.Write(id);
                    writeFields(writer);
                });
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                _pending.TryRemove(id, out _);
                throw new IOException("The proxy host isn't connected.", ex);
            }

            using (cancellationToken.Register(() => completion.TrySetCanceled()))
            {
                try
                {
                    var reply = await completion.Task.ConfigureAwait(false);
                    if (reply.Error != null)
                    {
                        throw new ProxyHostException(reply.Error);
                    }

                    return reply;
                }
                finally
                {
                    _pending.TryRemove(id, out _);
                }
            }
        }

        private void ReadLoop()
        {
            try
            {
                while (Protocol.TryReadFrame(_stream, out var type, out var fields))
                {
                    Dispatch(type, fields);
                }
            }
            catch (Exception)
            {
                // The pipe broke or closed (on Windows, closing it can also
                // surface as OperationCanceledException), or a message
                // didn't make sense. Either way this background thread must
                // not throw: the connection is over, handled below.
            }

            _hello.TrySetException(new IOException("The proxy host disconnected before saying hello."));
            FailPending(new IOException("The proxy host disconnected."));
            try
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A handler's own problem; nothing more to tell.
            }
        }

        private void Dispatch(MessageType type, BinaryReader fields)
        {
            switch (type)
            {
                case MessageType.HostHello:
                {
                    var version = fields.ReadInt32();
                    var token = fields.ReadString();
                    fields.ReadString(); // host version, for logs
                    if (version != Protocol.Version)
                    {
                        _hello.TrySetException(new InvalidOperationException(
                            $"The proxy host speaks protocol version {version}, and this app version {Protocol.Version}. Use the host that came with this version of the client library."));
                    }
                    else
                    {
                        _hello.TrySetResult(token);
                    }

                    break;
                }

                case MessageType.Ok:
                    Complete(fields.ReadInt32(), new Reply());
                    break;

                case MessageType.Error:
                {
                    var id = fields.ReadInt32();
                    Complete(id, new Reply { Error = fields.ReadString() });
                    break;
                }

                case MessageType.Started:
                {
                    var id = fields.ReadInt32();
                    var reply = new Reply { Port = fields.ReadInt32(), RootCertificate = fields.ReadBytes(), Upstream = fields.ReadNullableString() };
                    Complete(id, reply);
                    break;
                }

                case MessageType.Count:
                {
                    var id = fields.ReadInt32();
                    Complete(id, new Reply { Count = fields.ReadInt32() });
                    break;
                }

                case MessageType.SessionCaptured:
                    Raise(SessionCaptured, new SessionCapturedEventArgs(fields.ReadSession()));
                    break;

                case MessageType.Log:
                {
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(fields.ReadInt64());
                    Raise(Log, new ProxyLogEventArgs(time, fields.ReadString(), fields.ReadString()));
                    break;
                }

                case MessageType.HookRequest:
                    HandleHook(fields);
                    break;

                case MessageType.BreakpointHit:
                {
                    var id = fields.ReadInt64();
                    var ordinal = fields.ReadInt32();
                    var stage = (ExchangeStage)fields.ReadByte();
                    var host = fields.ReadString();
                    var rawText = fields.ReadString();
                    // The reply is sent from the thread pool, never this reader
                    // thread: if both sides were writing at once with full
                    // pipe buffers, neither would be reading.
                    var args = new BreakpointEventArgs(ordinal, stage, host, rawText,
                        (resume, edited) => ThreadPool.QueueUserWorkItem(_ => SendBreakpointReply(id, resume, edited)));
                    var handler = BreakpointHit;
                    if (handler == null)
                    {
                        args.Resume();
                        break;
                    }

                    try
                    {
                        handler(this, args);
                    }
                    catch (Exception)
                    {
                        // A failing handler would leave the message paused
                        // for good; let it go instead (a no-op if the
                        // handler already answered).
                        args.Resume();
                    }

                    break;
                }

                default:
                    // A newer host's message this client doesn't know. The
                    // protocol versions match, so this shouldn't happen.
                    break;
            }
        }

        private void HandleHook(BinaryReader fields)
        {
            var hookId = fields.ReadInt64();
            var stage = (ExchangeStage)fields.ReadByte();
            var sessionId = fields.ReadInt32();
            var host = fields.ReadString();
            var scheme = fields.ReadString();
            var request = fields.ReadRequest();
            var response = fields.ReadNullableResponse();
            var flags = fields.ReadFlags();

            var hook = stage == ExchangeStage.Request ? OnBeforeRequest : OnBeforeResponse;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var original = new { Request = request.Clone(), Response = response?.Clone(), Flags = new Dictionary<string, string>(flags, StringComparer.OrdinalIgnoreCase) };
                var context = new HookContext(stage, sessionId, host, scheme, request, response, flags);
                try
                {
                    hook?.Invoke(context);
                }
                catch (Exception ex)
                {
                    Raise(Log, new ProxyLogEventArgs(DateTimeOffset.Now, "host",
                        $"The {(stage == ExchangeStage.Request ? nameof(OnBeforeRequest) : nameof(OnBeforeResponse))} hook failed for #{sessionId}, so it went on unchanged: {ex.Message}"));
                    context = new HookContext(stage, sessionId, host, scheme, original.Request, original.Response, original.Flags);
                }

                try
                {
                    Protocol.WriteFrame(_stream, _writeLock, MessageType.HookReply, writer =>
                    {
                        writer.Write(hookId);
                        writer.WriteRequest(context.Request);
                        writer.WriteNullableResponse(context.Response);
                        writer.WriteNullableResponse(context.LocalResponse);
                        writer.WriteFlags(context.Flags);
                    });
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    // Disconnected; the host carries on without the reply.
                }
            });
        }

        private void SendBreakpointReply(long breakpointId, bool resume, string? editedRawText)
        {
            try
            {
                Protocol.WriteFrame(_stream, _writeLock, MessageType.BreakpointReply, writer =>
                {
                    writer.Write(breakpointId);
                    writer.Write(resume);
                    writer.WriteNullableString(editedRawText);
                });
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // Disconnected; the host lets paused messages go when the pipe closes.
            }
        }

        private void Raise<T>(EventHandler<T>? handler, T args)
            where T : EventArgs
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, args);
            }
            catch (Exception)
            {
                // An app's handler failing mustn't stop the reader thread,
                // or every later session, hook and reply would be lost.
            }
        }

        private void Complete(int id, Reply reply)
        {
            if (_pending.TryGetValue(id, out var completion))
            {
                completion.TrySetResult(reply);
            }
        }

        private void FailPending(Exception error)
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(error);
            }
        }

        private static void TryKill(Process? process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
                // Already gone, or not ours to kill.
            }
        }

        private sealed class Reply
        {
            public string? Error;
            public int Port;
            public byte[]? RootCertificate;
            public string? Upstream;
            public int Count;
        }
    }

    /// <summary>A command the proxy host couldn't carry out. The message says why.</summary>
    public sealed class ProxyHostException : Exception
    {
        public ProxyHostException(string message)
            : base(message)
        {
        }
    }
}
