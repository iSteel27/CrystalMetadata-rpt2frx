using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Abstractions;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrystalMetadata.Extractor
{
    /// <summary>
    /// Runs extractions one at a time on a single dedicated STA thread (the Crystal runtime is COM underneath).
    /// A request that exceeds its timeout cannot be aborted in-process: the worker is marked <see cref="IsPoisoned"/>
    /// and the host must recycle the process.
    /// </summary>
    public sealed class StaExtractionWorker : IDisposable
    {
        private sealed class WorkItem
        {
            public string Path = string.Empty;
            public ExtractionOptions Options = new ExtractionOptions();
            public TaskCompletionSource<ReportMetadata> Completion = new TaskCompletionSource<ReportMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly IReportMetadataExtractor _extractor;
        private readonly ILogger _logger;
        private readonly BlockingCollection<WorkItem> _queue = new BlockingCollection<WorkItem>();
        private readonly Thread _thread;
        private int _processed;
        private int _poisoned;

        public StaExtractionWorker(IReportMetadataExtractor extractor, ILogger<StaExtractionWorker>? logger = null)
        {
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _logger = logger ?? NullLogger<StaExtractionWorker>.Instance;
            _thread = new Thread(Loop) { Name = "CrystalExtraction-STA", IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>Number of extractions completed (successfully or not) since the worker started.</summary>
        public int Processed => Volatile.Read(ref _processed);

        /// <summary>True once a request timed out; the STA thread may be stuck inside the Crystal runtime.</summary>
        public bool IsPoisoned => Volatile.Read(ref _poisoned) != 0;

        public async Task<ReportMetadata> ExtractAsync(string path, ExtractionOptions options, CancellationToken ct)
        {
            if (IsPoisoned)
            {
                throw new InvalidOperationException("Extraction worker is poisoned (a previous request timed out); recycle the process.");
            }

            var item = new WorkItem { Path = path, Options = options ?? new ExtractionOptions() };
            _queue.Add(item, ct);

            var timeout = item.Options.TimeoutMs > 0 ? item.Options.TimeoutMs : ProtocolConstants.DefaultTimeoutMs;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var delay = Task.Delay(timeout, cts.Token);
                var finished = await Task.WhenAny(item.Completion.Task, delay).ConfigureAwait(false);
                if (finished == item.Completion.Task)
                {
                    cts.Cancel();
                    return await item.Completion.Task.ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref _poisoned, 1);
                _logger.LogError("Extraction of {Path} exceeded {TimeoutMs} ms; worker poisoned, process recycle required.", path, timeout);
                throw new TimeoutException("Extraction of '" + path + "' exceeded " + timeout + " ms.");
            }
        }

        private void Loop()
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var result = _extractor.Extract(item.Path, item.Options);
                    item.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
                finally
                {
                    Interlocked.Increment(ref _processed);
                }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }
    }
}
