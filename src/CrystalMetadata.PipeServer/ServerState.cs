using System;
using System.Diagnostics;
using System.Threading;
using CrystalMetadata.Core.Protocol;
using CrystalMetadata.Extractor;

namespace CrystalMetadata.PipeServer
{
    /// <summary>Mutable process-wide counters exposed through <see cref="ServerInfo"/>.</summary>
    public sealed class ServerState
    {
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private int _processed;
        private int _recyclePending;
        private string _recycleReason = string.Empty;

        public ServerState(ServerOptions options)
        {
            Options = options;
        }

        public ServerOptions Options { get; }

        public int Processed => Volatile.Read(ref _processed);

        public bool RecyclePending => Volatile.Read(ref _recyclePending) != 0;

        public string RecycleReason => _recycleReason;

        public int IncrementProcessed()
        {
            var n = Interlocked.Increment(ref _processed);
            if (Options.MaxReportsPerProcess > 0 && n >= Options.MaxReportsPerProcess)
            {
                RequestRecycle("max reports per process reached (" + n + ")");
            }

            return n;
        }

        public void RequestRecycle(string reason)
        {
            if (Interlocked.Exchange(ref _recyclePending, 1) == 0)
            {
                _recycleReason = reason;
            }
        }

        public ServerInfo ToInfo()
        {
            return new ServerInfo
            {
                ProcessId = Process.GetCurrentProcess().Id,
                Bitness = CrystalRuntime.Bitness,
                CrystalRuntimeVersion = CrystalRuntime.Version,
                ExtractorVersion = typeof(ReportMetadataExtractor).Assembly.GetName().Version?.ToString(),
                PipeName = Options.PipeName,
                ReportsProcessed = Processed,
                UptimeSeconds = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
                RecycleAfter = Options.MaxReportsPerProcess,
                RecyclePending = RecyclePending
            };
        }
    }
}
