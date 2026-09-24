using System;

namespace CrystalMetadata.Core.Protocol
{
    /// <summary>Protocol constants shared by server and client.</summary>
    public static class ProtocolConstants
    {
        public const int Version = 1;
        public const string DefaultPipeName = "CrystalMetadataPipe";
        public const int DefaultMaxFrameBytes = 256 * 1024 * 1024;
        public const int DefaultTimeoutMs = 120_000;

        /// <summary>Line the server prints on stdout once it accepts connections.</summary>
        public const string ReadyLine = "READY";

        /// <summary>Exit code the server uses when it wants to be restarted (recycle).</summary>
        public const int RecycleExitCode = 10;

        /// <summary>Exit code when the Crystal runtime is missing.</summary>
        public const int RuntimeMissingExitCode = 3;
    }

    /// <summary>Commands understood by the pipe server.</summary>
    public static class PipeCommands
    {
        public const string Extract = "extract";
        public const string Ping = "ping";
        public const string Info = "info";
        public const string Shutdown = "shutdown";
    }

    /// <summary>Options controlling one extraction.</summary>
    public sealed class ExtractionOptions
    {
        public bool IncludeUnusedFields { get; set; } = true;
        public bool IncludeRawBags { get; set; } = true;
        public bool IncludeGeneratedSql { get; set; }
        public int TimeoutMs { get; set; } = ProtocolConstants.DefaultTimeoutMs;

        /// <summary>When set (and allowed by the server) the server writes the JSON here and returns metadata = null.</summary>
        public string? OutputPath { get; set; }

        public ExtractionOptions Clone()
        {
            return (ExtractionOptions)MemberwiseClone();
        }
    }

    /// <summary>
    /// Request envelope. The minimal legacy form <c>{ "reportPath": "…" }</c> is accepted: missing members take defaults
    /// (protocolVersion 1, command "extract", new requestId).
    /// </summary>
    public sealed class PipeRequest
    {
        public int ProtocolVersion { get; set; } = ProtocolConstants.Version;
        public string RequestId { get; set; } = Guid.NewGuid().ToString("D");
        public string Command { get; set; } = PipeCommands.Extract;
        public string? ReportPath { get; set; }
        public ExtractionOptions? Options { get; set; }

        public static PipeRequest ForExtract(string reportPath, ExtractionOptions? options = null)
        {
            return new PipeRequest { Command = PipeCommands.Extract, ReportPath = reportPath, Options = options };
        }

        public static PipeRequest For(string command)
        {
            return new PipeRequest { Command = command };
        }
    }
}
