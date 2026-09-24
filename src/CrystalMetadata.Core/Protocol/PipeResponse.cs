using System.Collections.Generic;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Models;

namespace CrystalMetadata.Core.Protocol
{
    /// <summary>Error codes returned in <see cref="ErrorInfo.Code"/> and recorded in batch manifests.</summary>
    public static class ErrorCodes
    {
        public const string FileNotFound = "FILE_NOT_FOUND";
        public const string AccessDenied = "ACCESS_DENIED";
        public const string LoadFailed = "LOAD_FAILED";
        public const string JrcReport = "JRC_REPORT";
        public const string DbDriverMissing = "DB_DRIVER_MISSING";
        public const string PrintJobLimit = "PRINT_JOB_LIMIT";
        public const string SubreportOpenFailed = "SUBREPORT_OPEN_FAILED";
        public const string Timeout = "TIMEOUT";
        public const string ProtocolError = "PROTOCOL_ERROR";
        public const string Internal = "INTERNAL";
        public const string ServerCrashed = "SERVER_CRASHED";
        public const string ServerUnavailable = "SERVER_UNAVAILABLE";
        public const string OutputPathNotAllowed = "OUTPUT_PATH_NOT_ALLOWED";
        public const string UnknownCommand = "UNKNOWN_COMMAND";
    }

    public sealed class ErrorInfo
    {
        public string Code { get; set; } = ErrorCodes.Internal;
        public string Message { get; set; } = string.Empty;
        public string? Detail { get; set; }

        public static ErrorInfo Of(string code, string message, string? detail = null)
        {
            return new ErrorInfo { Code = code, Message = message, Detail = detail };
        }
    }

    /// <summary>Health/identity information of the extractor process.</summary>
    public sealed class ServerInfo
    {
        public int ProcessId { get; set; }
        public string Bitness { get; set; } = string.Empty;
        public string? CrystalRuntimeVersion { get; set; }
        public string? ExtractorVersion { get; set; }
        public string? PipeName { get; set; }
        public int ReportsProcessed { get; set; }
        public long UptimeSeconds { get; set; }
        public int RecycleAfter { get; set; }
        public bool RecyclePending { get; set; }
    }

    /// <summary>Response envelope. <c>success=true</c> with diagnostics = partial extraction; <c>success=false</c> = no metadata.</summary>
    public sealed class PipeResponse
    {
        public int ProtocolVersion { get; set; } = ProtocolConstants.Version;
        public string RequestId { get; set; } = string.Empty;
        public bool Success { get; set; }
        public ErrorInfo? Error { get; set; }
        public ReportMetadata? Metadata { get; set; }
        public List<Diagnostic> Diagnostics { get; set; } = new List<Diagnostic>();
        public ServerInfo? Server { get; set; }
        public long DurationMs { get; set; }

        /// <summary>Echoed when the server wrote the JSON to disk instead of returning it inline.</summary>
        public string? OutputPath { get; set; }

        public static PipeResponse Failure(string requestId, string code, string message, string? detail = null, ServerInfo? server = null)
        {
            return new PipeResponse
            {
                RequestId = requestId,
                Success = false,
                Error = ErrorInfo.Of(code, message, detail),
                Server = server
            };
        }
    }
}
