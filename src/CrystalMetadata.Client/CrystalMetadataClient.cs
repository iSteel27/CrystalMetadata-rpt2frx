using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CrystalMetadata.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrystalMetadata.Client;

/// <summary>Progress notification for <see cref="ICrystalMetadataClient.ExtractManyAsync"/>.</summary>
public sealed record BatchProgress(int Completed, int Total, int Failed, string LastReport);

/// <summary>High-level API used by the console and by any pipeline that consumes report metadata.</summary>
public interface ICrystalMetadataClient
{
    Task<ExtractionResult> ExtractAsync(string rptPath, ExtractionOptions? options = null, CancellationToken ct = default);

    IAsyncEnumerable<ExtractionResult> ExtractManyAsync(IEnumerable<string> rptPaths, ExtractionOptions? options = null, IProgress<BatchProgress>? progress = null, CancellationToken ct = default);

    Task<ServerInfo> PingAsync(CancellationToken ct = default);
}

/// <summary>
/// Default client: acquires a worker slot from <see cref="ExtractorProcessHost"/>, talks over <see cref="PipeTransport"/>,
/// retries once with a fresh process on transport failure / crash / timeout, and honours recycle notices.
/// </summary>
public sealed class CrystalMetadataClient : ICrystalMetadataClient
{
    private readonly ExtractorProcessHost _host;
    private readonly ClientOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Func<string, PipeTransport> _transportFactory;

    public CrystalMetadataClient(ExtractorProcessHost host, ClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        _host = host;
        _options = options;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<CrystalMetadataClient>();
        _transportFactory = pipe => new PipeTransport(pipe, _options, _loggerFactory.CreateLogger<PipeTransport>());
    }

    public async Task<ExtractionResult> ExtractAsync(string rptPath, ExtractionOptions? options = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var attempts = 0;
        ExtractionResult? last = null;

        while (attempts < 2)
        {
            attempts++;
            WorkerSlot slot;
            try
            {
                slot = await _host.AcquireAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "No extractor available for {Path}", rptPath);
                return ExtractionResult.Failed(rptPath, ErrorCodes.ServerUnavailable, ex.Message, attempts);
            }

            try
            {
                last = await SendExtractAsync(slot, rptPath, options, ct).ConfigureAwait(false);
                last.Attempts = attempts;
                last.DurationMs = sw.ElapsedMilliseconds;

                if (last.Server is { RecyclePending: true })
                {
                    _host.MarkForRestart(slot, "server requested recycle");
                }

                if (last.Success || !IsRetryable(last.ErrorCode))
                {
                    return last;
                }

                _host.MarkForRestart(slot, last.ErrorCode + " on " + rptPath);
            }
            finally
            {
                _host.Release(slot);
            }
        }

        return last ?? ExtractionResult.Failed(rptPath, ErrorCodes.Internal, "No result.", attempts);
    }

    private async Task<ExtractionResult> SendExtractAsync(WorkerSlot slot, string rptPath, ExtractionOptions? options, CancellationToken ct)
    {
        var request = PipeRequest.ForExtract(rptPath, options);
        var timeoutMs = options?.TimeoutMs > 0 ? options.TimeoutMs : _options.RequestTimeoutMs;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // give the server a grace period beyond its own timeout before we kill it
        deadline.CancelAfter(timeoutMs + 15_000);

        try
        {
            var response = await _transportFactory(slot.PipeName).SendAsync(request, deadline.Token).ConfigureAwait(false);
            return ToResult(rptPath, response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _host.Kill(slot, "deadline exceeded for " + rptPath);
            return ExtractionResult.Failed(rptPath, ErrorCodes.Timeout, "No response within " + (timeoutMs + 15_000) + " ms; extractor killed.");
        }
        catch (TransportException ex)
        {
            _logger.LogWarning(ex, "Transport failure on {Pipe} for {Path}", slot.PipeName, rptPath);
            _host.MarkForRestart(slot, ex.Message);
            return ExtractionResult.Failed(rptPath, ErrorCodes.ServerCrashed, ex.Message);
        }
    }

    private static bool IsRetryable(string? code)
    {
        return code is ErrorCodes.ServerCrashed or ErrorCodes.Timeout or ErrorCodes.PrintJobLimit or ErrorCodes.ServerUnavailable;
    }

    private static ExtractionResult ToResult(string rptPath, PipeResponse response)
    {
        var result = new ExtractionResult
        {
            ReportPath = rptPath,
            Success = response.Success,
            ErrorCode = response.Error?.Code,
            ErrorMessage = response.Error?.Message,
            Metadata = response.Metadata,
            Diagnostics = response.Diagnostics ?? new(),
            Server = response.Server,
            ServerOutputPath = response.OutputPath
        };
        if (result.Metadata != null && result.Diagnostics.Count == 0)
        {
            result.Diagnostics = result.Metadata.Diagnostics;
        }

        return result;
    }

    public async IAsyncEnumerable<ExtractionResult> ExtractManyAsync(IEnumerable<string> rptPaths, ExtractionOptions? options = null, IProgress<BatchProgress>? progress = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var paths = new List<string>(rptPaths);
        var results = Channel.CreateUnbounded<ExtractionResult>();
        var queue = Channel.CreateUnbounded<string>();
        foreach (var p in paths) queue.Writer.TryWrite(p);
        queue.Writer.Complete();

        var completed = 0;
        var failed = 0;
        var workers = new List<Task>();
        for (var i = 0; i < _host.WorkerCount; i++)
        {
            workers.Add(Task.Run(async () =>
            {
                await foreach (var path in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var r = await ExtractAsync(path, options, ct).ConfigureAwait(false);
                    var done = Interlocked.Increment(ref completed);
                    if (!r.Success) Interlocked.Increment(ref failed);
                    progress?.Report(new BatchProgress(done, paths.Count, Volatile.Read(ref failed), path));
                    await results.Writer.WriteAsync(r, ct).ConfigureAwait(false);
                }
            }, ct));
        }

        _ = Task.WhenAll(workers).ContinueWith(t => results.Writer.TryComplete(t.Exception), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        await foreach (var r in results.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return r;
        }
    }

    public async Task<ServerInfo> PingAsync(CancellationToken ct = default)
    {
        var slot = await _host.AcquireAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _transportFactory(slot.PipeName).SendAsync(PipeRequest.For(PipeCommands.Ping), ct).ConfigureAwait(false);
            return response.Server ?? new ServerInfo { PipeName = slot.PipeName };
        }
        finally
        {
            _host.Release(slot);
        }
    }
}
