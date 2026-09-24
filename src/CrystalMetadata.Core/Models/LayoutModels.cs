using System.Collections.Generic;
using Newtonsoft.Json;

namespace CrystalMetadata.Core.Models
{
    /// <summary>A conditional-format formula attached to a section or object (e.g. Suppress, FontColor).</summary>
    public sealed class ConditionFormulaMetadata
    {
        public string Type { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>An area (band type). Group areas map to <see cref="GroupIndex"/>. Areas can hold several sections (Details a/b/c).</summary>
    public sealed class AreaMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public int? GroupIndex { get; set; }
        public List<string> SectionNames { get; set; } = new List<string>();
        public AreaFormatMetadata Format { get; set; } = new AreaFormatMetadata();
    }

    public sealed class AreaFormatMetadata
    {
        public bool? Suppress { get; set; }
        public bool? KeepTogether { get; set; }
        public bool? NewPageBefore { get; set; }
        public bool? NewPageAfter { get; set; }
        public bool? PrintAtBottomOfPage { get; set; }
        public bool? ResetPageNumberAfter { get; set; }
        public bool? HideForDrillDown { get; set; }
        public List<ConditionFormulaMetadata> ConditionFormulas { get; set; } = new List<ConditionFormulaMetadata>();
    }

    /// <summary>A section (band instance) in print order, owning its objects.</summary>
    public sealed class SectionMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string? AreaName { get; set; }
        public int? GroupIndex { get; set; }
        public int IndexInArea { get; set; }
        public int HeightTwips { get; set; }
        public double HeightPoints { get; set; }
        public SectionFormatMetadata Format { get; set; } = new SectionFormatMetadata();
        public List<ObjectMetadata> Objects { get; set; } = new List<ObjectMetadata>();
    }

    public sealed class SectionFormatMetadata
    {
        public bool? Suppress { get; set; }
        public bool? SuppressIfBlank { get; set; }
        public bool? NewPageBefore { get; set; }
        public bool? NewPageAfter { get; set; }
        public bool? KeepTogether { get; set; }
        public bool? PrintAtBottomOfPage { get; set; }
        public bool? ResetPageNumberAfter { get; set; }
        public bool? UnderlayFollowingSections { get; set; }
        public string? BackgroundColor { get; set; }
        public string? CssClass { get; set; }
        public List<ConditionFormulaMetadata> ConditionFormulas { get; set; } = new List<ConditionFormulaMetadata>();
    }

    public enum ObjectKind
    {
        Unknown,
        Field,
        FieldHeading,
        Text,
        Line,
        Box,
        Picture,
        Blob,
        Subreport,
        Chart,
        CrossTab,
        Map,
        OlapGrid
    }

    /// <summary>
    /// A visual object. Common members are always present; kind-specific members (<see cref="Field"/>, <see cref="Text"/>, …)
    /// are only emitted for the matching <see cref="Kind"/>.
    /// </summary>
    public sealed class ObjectMetadata
    {
        public string Name { get; set; } = string.Empty;
        public ObjectKind Kind { get; set; }
        public string SectionName { get; set; } = string.Empty;
        public int ZIndex { get; set; }
        public int LeftTwips { get; set; }
        public int TopTwips { get; set; }
        public int WidthTwips { get; set; }
        public int HeightTwips { get; set; }
        public int RightTwips { get; set; }
        public int BottomTwips { get; set; }
        public double LeftPoints { get; set; }
        public double TopPoints { get; set; }
        public double WidthPoints { get; set; }
        public double HeightPoints { get; set; }
        public ObjectFormatMetadata Format { get; set; } = new ObjectFormatMetadata();
        public BorderMetadata? Border { get; set; }
        public FontMetadata? Font { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public FieldObjectMetadata? Field { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public FieldHeadingMetadata? FieldHeading { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public TextObjectMetadata? Text { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public LineObjectMetadata? Line { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public BoxObjectMetadata? Box { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public PictureObjectMetadata? Picture { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public BlobObjectMetadata? Blob { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public SubreportObjectMetadata? Subreport { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public ChartObjectMetadata? Chart { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public CrossTabObjectMetadata? CrossTab { get; set; }

        /// <summary>Extension bag for scalar SDK properties that are not modelled explicitly.</summary>
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();

        /// <summary>Recomputes right/bottom and point values from the twip values.</summary>
        public void ComputeDerivedGeometry()
        {
            RightTwips = LeftTwips + WidthTwips;
            BottomTwips = TopTwips + HeightTwips;
            LeftPoints = Core.Units.ToPoints(LeftTwips);
            TopPoints = Core.Units.ToPoints(TopTwips);
            WidthPoints = Core.Units.ToPoints(WidthTwips);
            HeightPoints = Core.Units.ToPoints(HeightTwips);
        }
    }

    public sealed class ObjectFormatMetadata
    {
        public bool? Suppress { get; set; }
        public bool? CanGrow { get; set; }
        public bool? KeepTogether { get; set; }
        public bool? CloseAtPageBreak { get; set; }
        public string? HorizontalAlignment { get; set; }
        public string? CssClass { get; set; }
        public string? HyperlinkType { get; set; }
        public string? HyperlinkText { get; set; }
        public List<ConditionFormulaMetadata> ConditionFormulas { get; set; } = new List<ConditionFormulaMetadata>();
    }

    public sealed class BorderMetadata
    {
        public string? Left { get; set; }
        public string? Right { get; set; }
        public string? Top { get; set; }
        public string? Bottom { get; set; }
        public string? BorderColor { get; set; }
        public string? BackgroundColor { get; set; }
        public bool? HasDropShadow { get; set; }
        public List<ConditionFormulaMetadata> ConditionFormulas { get; set; } = new List<ConditionFormulaMetadata>();
    }

    public sealed class FontMetadata
    {
        public string? Name { get; set; }
        public double? SizePoints { get; set; }
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }
        public bool? Underline { get; set; }
        public bool? Strikeout { get; set; }
        public string? Color { get; set; }
        public List<ConditionFormulaMetadata> ConditionFormulas { get; set; } = new List<ConditionFormulaMetadata>();
    }

    public sealed class FieldObjectMetadata
    {
        /// <summary>Crystal formula-name of the bound field, e.g. <c>{Table.Field}</c>, <c>{@Formula}</c>, <c>{?Param}</c>, <c>{PageNumber}</c>.</summary>
        public string? DataSource { get; set; }
        public string? FieldKind { get; set; }
        public string? FieldName { get; set; }
        public string? TableAlias { get; set; }
        public string? ValueType { get; set; }
        public FieldFormatMetadata FieldFormat { get; set; } = new FieldFormatMetadata();
    }

    public sealed class FieldFormatMetadata
    {
        public bool? SuppressIfDuplicated { get; set; }
        public NumericFormatMetadata? Numeric { get; set; }
        public DateFormatMetadata? Date { get; set; }
        public Dictionary<string, object?>? Time { get; set; }
        public DateTimeFormatMetadata? DateTime { get; set; }
        public BooleanFormatMetadata? Boolean { get; set; }
        public List<ConditionFormulaMetadata> ConditionFormulas { get; set; } = new List<ConditionFormulaMetadata>();
    }

    public sealed class NumericFormatMetadata
    {
        public int? DecimalPlaces { get; set; }
        public bool? ThousandsSeparator { get; set; }
        public string? ThousandsSeparatorSymbol { get; set; }
        public string? DecimalSymbol { get; set; }
        public string? CurrencySymbol { get; set; }
        public string? CurrencySymbolFormat { get; set; }
        public string? CurrencyPositionFormat { get; set; }
        public string? NegativeFormat { get; set; }
        public bool? UseLeadingZero { get; set; }
        public string? RoundingFormat { get; set; }
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class DateFormatMetadata
    {
        public string? DayFormat { get; set; }
        public string? MonthFormat { get; set; }
        public string? YearFormat { get; set; }
        public string? DateOrder { get; set; }
        public string? Separator { get; set; }
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class DateTimeFormatMetadata
    {
        public string? Order { get; set; }
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class BooleanFormatMetadata
    {
        public string? OutputType { get; set; }
    }

    public sealed class FieldHeadingMetadata
    {
        public string? ForFieldObjectName { get; set; }
        public string? Text { get; set; }
    }

    public sealed class TextObjectMetadata
    {
        /// <summary>Plain text with embedded fields rendered as their formula name, e.g. "Page {PageNumber} of {TotalPageCount}".</summary>
        public string? Text { get; set; }

        /// <summary>Rich structure (RAS). Empty when RAS enrichment was unavailable.</summary>
        public List<ParagraphMetadata> Paragraphs { get; set; } = new List<ParagraphMetadata>();
    }

    public sealed class ParagraphMetadata
    {
        public string? Alignment { get; set; }
        public List<ParagraphElementMetadata> Elements { get; set; } = new List<ParagraphElementMetadata>();
    }

    public sealed class ParagraphElementMetadata
    {
        public string Kind { get; set; } = "Text";
        public string? Text { get; set; }
        public string? DataSource { get; set; }
        public FontMetadata? Font { get; set; }
    }

    public sealed class LineObjectMetadata
    {
        public int X1Twips { get; set; }
        public int Y1Twips { get; set; }
        public int X2Twips { get; set; }
        public int Y2Twips { get; set; }
        public string? Orientation { get; set; }
        public string? LineStyle { get; set; }
        public int? ThicknessTwips { get; set; }
        public string? Color { get; set; }
        public bool? ExtendToBottomOfSection { get; set; }
        public string? EndSectionName { get; set; }
    }

    public sealed class BoxObjectMetadata
    {
        public string? LineStyle { get; set; }
        public int? ThicknessTwips { get; set; }
        public string? LineColor { get; set; }
        public string? FillColor { get; set; }
        public int? CornerEllipseWidthTwips { get; set; }
        public int? CornerEllipseHeightTwips { get; set; }
        public bool? ExtendToBottomOfSection { get; set; }
        public string? EndSectionName { get; set; }
    }

    public sealed class PictureObjectMetadata
    {
        public int? OriginalWidthTwips { get; set; }
        public int? OriginalHeightTwips { get; set; }
        public double? XScaling { get; set; }
        public double? YScaling { get; set; }
        public CropMetadata CropTwips { get; set; } = new CropMetadata();

        /// <summary>The SDK does not expose the embedded image bytes; always false.</summary>
        public bool ImageBytesAvailable { get; set; }
    }

    public sealed class CropMetadata
    {
        public int? Left { get; set; }
        public int? Top { get; set; }
        public int? Right { get; set; }
        public int? Bottom { get; set; }
    }

    public sealed class BlobObjectMetadata
    {
        public string? DataSource { get; set; }
    }

    public sealed class SubreportObjectMetadata
    {
        public string SubreportName { get; set; } = string.Empty;
        public bool? IsOnDemand { get; set; }
    }

    public sealed class ChartObjectMetadata
    {
        public string? ChartType { get; set; }
        public bool RequiresManualReview { get; set; } = true;
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class CrossTabObjectMetadata
    {
        public List<string> Rows { get; set; } = new List<string>();
        public List<string> Columns { get; set; } = new List<string>();
        public List<string> Summaries { get; set; } = new List<string>();
        public bool RequiresManualReview { get; set; } = true;
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }
}
