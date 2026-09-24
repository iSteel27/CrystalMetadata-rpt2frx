using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Abstractions;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrystalMetadata.Client;

/// <summary>Thrown when the transport failed before a well-formed response was received (server gone, pipe broken, malformed frame).</summary>
public sealed class TransportException : IOException
{
    public TransportException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>One request / one response over a named pipe. A new connection per request keeps failure handling simple.</summary>
public sealed class PipeTransport : IMetadataTransport
{
    private readonly string _pipeName;
    private readonly ClientOptions _options;
    private readonly ILogger _logger;

    public PipeTransport(string pipeName, ClientOptions options, ILogger<PipeTransport>? logger = null)
    {
        _pipeName = pipeName;
        _options = options;
        _logger = logger ?? NullLogger<PipeTransport>.Instance;
    }

    public string PipeName => _pipeName;

    public async Task<PipeResponse> SendAsync(PipeRequest request, CancellationToken ct)
    {
        var pipeOptions = PipeOptions.Asynchronous;
        if (_options.CurrentUserOnly)
        {
            pipeOptions |= PipeOptions.CurrentUserOnly;
        }

        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, pipeOptions);
        try
        {
            await pipe.ConnectAsync(_options.ConnectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TransportException("Timed out connecting to pipe '" + _pipeName + "'.", ex);
        }
        catch (IOException ex)
        {
            throw new TransportException("Could not connect to pipe '" + _pipeName + "': " + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TransportException("Access denied to pipe '" + _pipeName + "' (server running under a different account? set Client:CurrentUserOnly=false and configure AllowedSids).", ex);
        }

        var payload = MetadataJson.SerializeToBytes(request);
        _logger.LogDebug("→ {Pipe} {Command} {RequestId} ({Bytes} bytes)", _pipeName, request.Command, request.RequestId, payload.Length);

        byte[]? frame;
        try
        {
            await FrameCodec.WriteFrameAsync(pipe, payload, ct).ConfigureAwait(false);
            frame = await FrameCodec.ReadFrameAsync(pipe, _options.MaxFrameBytes, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new TransportException("Pipe '" + _pipeName + "' failed during request " + request.RequestId + ": " + ex.Message, ex);
        }

        if (frame == null)
        {
            throw new TransportException("Server closed pipe '" + _pipeName + "' without responding (crash?).");
        }

        PipeResponse? response;
        try
        {
            response = MetadataJson.Deserialize<PipeResponse>(frame);
        }
        catch (Exception ex)
        {
            throw new TransportException("Malformed response on pipe '" + _pipeName + "': " + ex.Message, ex);
        }

        if (response == null)
        {
            throw new TransportException("Empty response on pipe '" + _pipeName + "'.");
        }

        _logger.LogDebug("← {Pipe} {RequestId} success={Success} {Ms} ms", _pipeName, response.RequestId, response.Success, response.DurationMs);
        return response;
    }
}
