using System;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Core.Protocol;

namespace CrystalMetadata.Core.Abstractions
{
    /// <summary>
    /// Extracts metadata from one report file. Implementations never throw for problems inside the report
    /// (those become diagnostics); they throw <see cref="ReportLoadException"/> only when the file cannot be opened at all.
    /// </summary>
    public interface IReportMetadataExtractor
    {
        ReportMetadata Extract(string reportPath, ExtractionOptions options);
    }

    /// <summary>The report could not be loaded; <see cref="Code"/> is one of <see cref="ErrorCodes"/>.</summary>
    public sealed class ReportLoadException : Exception
    {
        public ReportLoadException(string code, string message, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
