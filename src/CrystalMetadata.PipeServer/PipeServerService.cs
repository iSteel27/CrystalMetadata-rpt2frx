using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrystalMetadata.PipeServer
{
    /// <summary>
    /// Accept loop: one pipe instance, one client at a time, sequential length-prefixed frames per connection.
    /// Exits the host with <see cref="ProtocolConstants.RecycleExitCode"/> once a recycle is pending.
    /// </summary>
    public sealed class PipeServerService : BackgroundService
    {
        private readonly ServerState _state;
        private readonly RequestDispatcher _dispatcher;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<PipeServerService> _logger;

        public PipeServerService(ServerState state, RequestDispatcher dispatcher, IHostApplicationLifetime lifetime, ILogger<PipeServerService> logger)
        {
            _state = state;
            _dispatcher = dispatcher;
            _lifetime = lifetime;
            _logger = logger;
        }

        /// <summary>Exit code the process should use when the host stops.</summary>
        public int ExitCode { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var options = _state.Options;
            _logger.LogInformation("Pipe server listening on \\\\.\\pipe\\{Pipe} (pid {Pid}, {Bitness}, recycle after {Max})",
                options.PipeName, System.Diagnostics.Process.GetCurrentProcess().Id, Extractor.CrystalRuntime.Bitness, options.MaxReportsPerProcess);

            // The client supervisor waits for this exact line on stdout.
            Console.Out.WriteLine(ProtocolConstants.ReadyLine);
            Console.Out.Flush();

            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = CreatePipe(options);
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                    await ServeConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Accept loop error; continuing");
                    await Task.Delay(250, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    try { pipe?.Dispose(); } catch { /* ignore */ }
                }

                if (_dispatcher.ShutdownRequested)
                {
                    _logger.LogInformation("Shutdown requested by client");
                    ExitCode = 0;
                    _lifetime.StopApplication();
                    break;
                }

                if (_state.RecyclePending)
                {
                    _logger.LogInformation("ServerRecycling reason={Reason} processed={Processed}", _state.RecycleReason, _state.Processed);
                    ExitCode = ProtocolConstants.RecycleExitCode;
                    _lifetime.StopApplication();
                    break;
                }

                if (options.Once)
                {
                    ExitCode = 0;
                    _lifetime.StopApplication();
                    break;
                }
            }
        }

        private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                byte[]? frame;
                try
                {
                    frame = await FrameCodec.ReadFrameAsync(pipe, _state.Options.MaxFrameBytes, ct).ConfigureAwait(false);
                }
                catch (FrameTooLargeException ex)
                {
                    await WriteAsync(pipe, PipeResponse.Failure(string.Empty, ErrorCodes.ProtocolError, ex.Message, null, _state.ToInfo()), ct).ConfigureAwait(false);
                    return;
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
                }

                if (frame == null)
                {
                    return; // client closed
                }

                PipeRequest? request = null;
                string? parseError = null;
                try
                {
                    request = MetadataJson.Deserialize<PipeRequest>(frame);
                }
                catch (Exception ex)
                {
                    parseError = ex.Message;
                }

                PipeResponse response;
                if (request == null)
                {
                    response = PipeResponse.Failure(string.Empty, ErrorCodes.ProtocolError, "Malformed request: " + (parseError ?? "empty payload"), null, _state.ToInfo());
                }
                else
                {
                    response = await _dispatcher.HandleAsync(request, ct).ConfigureAwait(false);
                }

                try
                {
                    await WriteAsync(pipe, response, ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Client went away before the response could be written");
                    return;
                }

                if (_dispatcher.ShutdownRequested || _state.RecyclePending || _state.Options.Once)
                {
                    return;
                }
            }
        }

        private static async Task WriteAsync(PipeStream pipe, PipeResponse response, CancellationToken ct)
        {
            var bytes = MetadataJson.SerializeToBytes(response);
            await FrameCodec.WriteFrameAsync(pipe, bytes, ct).ConfigureAwait(false);
        }

        private NamedPipeServerStream CreatePipe(ServerOptions options)
        {
            var security = new PipeSecurity();
            var current = WindowsIdentity.GetCurrent();
            if (current.User != null)
            {
                security.AddAccessRule(new PipeAccessRule(current.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                security.SetOwner(current.User);
            }

            foreach (var sid in options.AllowedSids)
            {
                if (string.IsNullOrWhiteSpace(sid)) continue;
                try
                {
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ignoring invalid SID {Sid}", sid);
                }
            }

            return new NamedPipeServerStream(
                options.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                64 * 1024,
                64 * 1024,
                security);
        }
    }
}
