using System.Collections.Generic;

namespace CrystalMetadata.Core.Models
{
    /// <summary>
    /// Optional, client-computed view of the layout in "table" terms (columns paired with headers, band summary).
    /// Produced by <c>LayoutHints</c> in CrystalMetadata.Client; pure post-processing of the extracted twips.
    /// </summary>
    public sealed class DerivedLayout
    {
        public List<DerivedColumn> Columns { get; set; } = new List<DerivedColumn>();
        public List<DerivedBand> Bands { get; set; } = new List<DerivedBand>();
    }

    public sealed class DerivedColumn
    {
        public string? Header { get; set; }
        public string? HeaderObjectName { get; set; }
        public string? HeaderSectionName { get; set; }
        public string FieldObjectName { get; set; } = string.Empty;
        public string? FieldDataSource { get; set; }
        public string DetailSectionName { get; set; } = string.Empty;
        public int LeftTwips { get; set; }
        public int WidthTwips { get; set; }
        public string? Alignment { get; set; }
        public NumericFormatMetadata? NumberFormat { get; set; }
        public FontMetadata? Font { get; set; }
    }

    public sealed class DerivedBand
    {
        public string SectionName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public int? GroupIndex { get; set; }
        public int HeightTwips { get; set; }
        public int ObjectCount { get; set; }
        public bool IsEmpty { get; set; }
        public bool IsSuppressed { get; set; }
    }
}
