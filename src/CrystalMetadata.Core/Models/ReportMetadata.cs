using System;
using System.Collections.Generic;
using CrystalMetadata.Core.Diagnostics;
using Newtonsoft.Json;

namespace CrystalMetadata.Core.Models
{
    /// <summary>
    /// Root of the metadata document for one report (main report or subreport). Every property exists in the JSON;
    /// an explicit <c>null</c> means "not obtainable from Crystal".
    /// </summary>
    public sealed class ReportMetadata
    {
        public const string CurrentSchemaVersion = "1.0";

        public string SchemaVersion { get; set; } = CurrentSchemaVersion;
        public ExtractorInfo Extractor { get; set; } = new ExtractorInfo();
        public UnitsInfo Units { get; set; } = new UnitsInfo();
        public ReportInfo ReportInfo { get; set; } = new ReportInfo();
        public PageSetup PageSetup { get; set; } = new PageSetup();
        public DataSourceMetadata DataSource { get; set; } = new DataSourceMetadata();
        public List<ParameterMetadata> Parameters { get; set; } = new List<ParameterMetadata>();
        public List<FormulaMetadata> Formulas { get; set; } = new List<FormulaMetadata>();
        public List<SqlExpressionMetadata> SqlExpressions { get; set; } = new List<SqlExpressionMetadata>();
        public List<CustomFunctionMetadata> CustomFunctions { get; set; } = new List<CustomFunctionMetadata>();
        public List<SummaryFieldMetadata> SummaryFields { get; set; } = new List<SummaryFieldMetadata>();
        public List<RunningTotalMetadata> RunningTotals { get; set; } = new List<RunningTotalMetadata>();
        public List<GroupMetadata> Groups { get; set; } = new List<GroupMetadata>();
        public List<SortMetadata> SortFields { get; set; } = new List<SortMetadata>();
        public List<AreaMetadata> Areas { get; set; } = new List<AreaMetadata>();

        /// <summary>Sections in print order. This is the canonical home of every report object.</summary>
        public List<SectionMetadata> Sections { get; set; } = new List<SectionMetadata>();

        public List<SubreportMetadata> Subreports { get; set; } = new List<SubreportMetadata>();

        /// <summary>Client-side layout hints (see LayoutHints in CrystalMetadata.Client). Omitted when not computed.</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public DerivedLayout? Derived { get; set; }

        public List<Diagnostic> Diagnostics { get; set; } = new List<Diagnostic>();
        public ExtractionStats Stats { get; set; } = new ExtractionStats();
    }

    /// <summary>Information about the extractor that produced the document.</summary>
    public sealed class ExtractorInfo
    {
        public string Version { get; set; } = string.Empty;
        public string? CrystalRuntimeVersion { get; set; }
        public string Bitness { get; set; } = IntPtr.Size == 8 ? "x64" : "x86";
        public DateTime ExtractedAtUtc { get; set; } = DateTime.UtcNow;
        public long DurationMs { get; set; }
    }

    /// <summary>Unit declaration so consumers never have to guess.</summary>
    public sealed class UnitsInfo
    {
        public string Position { get; set; } = "twips";
        public string Font { get; set; } = "points";
        public int TwipsPerInch { get; set; } = Core.Units.TwipsPerInch;
        public int TwipsPerPoint { get; set; } = Core.Units.TwipsPerPoint;
        public double TwipsPerMm { get; set; } = Core.Units.TwipsPerMm;
    }

    /// <summary>File-level and document-level information.</summary>
    public sealed class ReportInfo
    {
        public string ReportName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long? FileSizeBytes { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        public string? ReportFileVersion { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Subject { get; set; }
        public string? Comments { get; set; }
        public string? Keywords { get; set; }
        public bool? HasSavedData { get; set; }
        public bool? SelectDistinctRecords { get; set; }
        public string? RecordSelectionFormula { get; set; }
        public string? GroupSelectionFormula { get; set; }
        public bool IsSubreport { get; set; }
    }

    /// <summary>Page/paper settings.</summary>
    public sealed class PageSetup
    {
        public string? PaperOrientation { get; set; }
        public string? PaperSize { get; set; }
        public string? PrinterName { get; set; }
        public int? PageWidthTwips { get; set; }
        public int? PageHeightTwips { get; set; }
        public PageMargins Margins { get; set; } = new PageMargins();
        public int? PrintableWidthTwips { get; set; }
        public int? PrintableHeightTwips { get; set; }
        public bool? DissociatePageSizeFromPrinter { get; set; }
    }

    public sealed class PageMargins
    {
        public int? LeftTwips { get; set; }
        public int? RightTwips { get; set; }
        public int? TopTwips { get; set; }
        public int? BottomTwips { get; set; }
    }

    /// <summary>Counts computed after extraction.</summary>
    public sealed class ExtractionStats
    {
        public int SectionCount { get; set; }
        public int ObjectCount { get; set; }
        public int FormulaCount { get; set; }
        public int ParameterCount { get; set; }
        public int TableCount { get; set; }
        public int SubreportCount { get; set; }
        public int WarningCount { get; set; }
        public int ErrorCount { get; set; }
    }
}
