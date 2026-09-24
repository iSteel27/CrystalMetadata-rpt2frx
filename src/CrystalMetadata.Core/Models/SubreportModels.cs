using System.Collections.Generic;

namespace CrystalMetadata.Core.Models
{
    /// <summary>A subreport: where it is placed in the parent, how it is linked, and its full metadata.</summary>
    public sealed class SubreportMetadata
    {
        public string Name { get; set; } = string.Empty;
        public SubreportPlacement? Placement { get; set; }
        public bool? IsOnDemand { get; set; }
        public List<SubreportLinkMetadata> Links { get; set; } = new List<SubreportLinkMetadata>();

        /// <summary>Full metadata of the subreport. Its own <c>subreports</c> list is always empty (Crystal forbids nesting).</summary>
        public ReportMetadata? Report { get; set; }
    }

    public sealed class SubreportPlacement
    {
        public string SectionName { get; set; } = string.Empty;
        public string ObjectName { get; set; } = string.Empty;
        public int LeftTwips { get; set; }
        public int TopTwips { get; set; }
        public int WidthTwips { get; set; }
        public int HeightTwips { get; set; }
        public int ZIndex { get; set; }
    }

    /// <summary>Parent → child link. Without these a migrated subreport receives no data.</summary>
    public sealed class SubreportLinkMetadata
    {
        public string? MainReportField { get; set; }
        public string? SubreportParameter { get; set; }
        public string? SubreportField { get; set; }
        public string? LinkedParameterName { get; set; }
    }
}
