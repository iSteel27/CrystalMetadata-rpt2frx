using System.Threading;
using System.Threading.Tasks;
using CrystalMetadata.Core.Models;

namespace CrystalMetadata.Client;

/// <summary>Target engines the AI conversion pipeline can produce.</summary>
public enum ConversionTarget
{
    FastReport,
    MigraDoc,
    PdfSharp,
    QuestPdf,
    Html,
    Custom
}

public sealed class ConversionResult
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Boundary to the AI conversion pipeline. The extractor's job ends at <see cref="ReportMetadata"/>; an implementation of
/// this interface (LLM-backed, template-based, …) turns it into a target report definition.
/// </summary>
public interface IReportConverter
{
    Task<ConversionResult> ConvertAsync(ReportMetadata metadata, ConversionTarget target, CancellationToken ct = default);
}

/// <summary>Placeholder implementation so the pipeline is wired end-to-end without an LLM dependency.</summary>
public sealed class NullReportConverter : IReportConverter
{
    public Task<ConversionResult> ConvertAsync(ReportMetadata metadata, ConversionTarget target, CancellationToken ct = default)
    {
        return Task.FromResult(new ConversionResult { Success = false, Error = "No IReportConverter is registered; metadata for '" + metadata.ReportInfo.ReportName + "' was not converted." });
    }
}
