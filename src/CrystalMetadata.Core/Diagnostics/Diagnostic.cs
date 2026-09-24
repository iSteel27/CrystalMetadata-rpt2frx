using System;
using System.Collections.Generic;

namespace CrystalMetadata.Core.Diagnostics
{
    /// <summary>Severity of an extraction diagnostic.</summary>
    public enum DiagnosticLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// A non-fatal problem encountered while extracting metadata. Diagnostics are embedded in the JSON so the
    /// consumer (human or LLM) knows exactly which part of the report could not be read.
    /// </summary>
    public sealed class Diagnostic
    {
        public DiagnosticLevel Level { get; set; }

        /// <summary>Stable machine-readable code, see <see cref="DiagnosticCodes"/>.</summary>
        public string Code { get; set; } = DiagnosticCodes.Unknown;

        /// <summary>"Main" for the main report, otherwise the subreport name.</summary>
        public string Scope { get; set; } = "Main";

        /// <summary>JSON path of the element the diagnostic refers to, e.g. <c>sections[2].objects[4]</c>.</summary>
        public string Path { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        /// <summary>Exception type and message (never a stack trace; those go to the log).</summary>
        public string? Exception { get; set; }

        public static Diagnostic FromException(DiagnosticLevel level, string code, string scope, string path, Exception ex, string? message = null)
        {
            var root = ex;
            while (root.InnerException != null && root is AggregateException)
            {
                root = root.InnerException;
            }

            return new Diagnostic
            {
                Level = level,
                Code = code,
                Scope = scope,
                Path = path,
                Message = message ?? root.Message,
                Exception = root.GetType().FullName + ": " + root.Message
            };
        }
    }

    /// <summary>Well-known diagnostic codes.</summary>
    public static class DiagnosticCodes
    {
        public const string Unknown = "UNKNOWN";
        public const string ReportInfoFailed = "REPORT_INFO_FAILED";
        public const string PageSetupFailed = "PAGE_SETUP_FAILED";
        public const string DatabaseInfoFailed = "DATABASE_INFO_FAILED";
        public const string DbDriverMissing = "DB_DRIVER_MISSING";
        public const string TableFailed = "TABLE_FAILED";
        public const string FieldFailed = "FIELD_FAILED";
        public const string LinkFailed = "LINK_FAILED";
        public const string ParameterFailed = "PARAMETER_FAILED";
        public const string FormulaFailed = "FORMULA_FAILED";
        public const string SummaryFailed = "SUMMARY_FAILED";
        public const string RunningTotalFailed = "RUNNING_TOTAL_FAILED";
        public const string GroupFailed = "GROUP_FAILED";
        public const string SortFailed = "SORT_FAILED";
        public const string AreaFailed = "AREA_FAILED";
        public const string SectionFailed = "SECTION_FAILED";
        public const string ObjectFailed = "OBJECT_FAILED";
        public const string ObjectPropertyUnavailable = "OBJECT_PROPERTY_UNAVAILABLE";
        public const string UnknownObjectKind = "UNKNOWN_OBJECT_KIND";
        public const string SubreportOpenFailed = "SUBREPORT_OPEN_FAILED";
        public const string SubreportNotPlaced = "SUBREPORT_NOT_PLACED";
        public const string SubreportLinksFailed = "SUBREPORT_LINKS_FAILED";
        public const string NestedSubreportIgnored = "NESTED_SUBREPORT_IGNORED";
        public const string RasUnavailable = "RAS_UNAVAILABLE";
        public const string RasEnrichmentFailed = "RAS_ENRICHMENT_FAILED";
        public const string GeneratedSqlFailed = "GENERATED_SQL_FAILED";
        public const string CustomFunctionsFailed = "CUSTOM_FUNCTIONS_FAILED";
        public const string ManualReviewRequired = "MANUAL_REVIEW_REQUIRED";
        public const string GroupIndexMismatch = "GROUP_INDEX_MISMATCH";
    }

    /// <summary>Helper for accumulating diagnostics with a fixed scope.</summary>
    public sealed class DiagnosticSink
    {
        private readonly List<Diagnostic> _items = new List<Diagnostic>();

        public DiagnosticSink(string scope)
        {
            Scope = scope;
        }

        public string Scope { get; }

        public IReadOnlyList<Diagnostic> Items => _items;

        public void Add(DiagnosticLevel level, string code, string path, string message)
        {
            _items.Add(new Diagnostic { Level = level, Code = code, Scope = Scope, Path = path, Message = message });
        }

        public void Add(Diagnostic diagnostic)
        {
            _items.Add(diagnostic);
        }

        public void AddRange(IEnumerable<Diagnostic> diagnostics)
        {
            _items.AddRange(diagnostics);
        }
    }
}
