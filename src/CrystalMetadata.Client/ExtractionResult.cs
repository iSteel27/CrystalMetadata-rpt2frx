using System.Collections.Generic;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Core.Protocol;

namespace CrystalMetadata.Client;

/// <summary>Outcome of extracting one report through the client.</summary>
public sealed class ExtractionResult
{
    public string ReportPath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public ReportMetadata? Metadata { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public long DurationMs { get; set; }
    public int Attempts { get; set; } = 1;
    public ServerInfo? Server { get; set; }

    /// <summary>Set when the server wrote the file itself (outputPath mode).</summary>
    public string? ServerOutputPath { get; set; }

    public static ExtractionResult Failed(string path, string code, string message, int attempts = 1)
    {
        return new ExtractionResult { ReportPath = path, Success = false, ErrorCode = code, ErrorMessage = message, Attempts = attempts };
    }
}
