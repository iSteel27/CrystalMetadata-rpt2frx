using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using CrystalMetadata.Core;
using CrystalMetadata.Core.Models;

namespace CrystalMetadata.FastReport;

public enum TargetEdition
{
    /// <summary>MIT open source package: no charts, maps or RichText, PDF only through the raster plug-in.</summary>
    OpenSource,

    /// <summary>Commercial FastReport .NET (reserved: currently generates the same as OpenSource).</summary>
    Commercial
}

public sealed class FrxOptions
{
    public TargetEdition Edition { get; set; } = TargetEdition.OpenSource;

    /// <summary>
    /// Image file (PNG/JPG/...) embedded into every Crystal picture object. The SDK does not expose the original image
    /// bytes, so without this the pictures become empty outlined placeholders.
    /// </summary>
    public string? PicturePath { get; set; }

    /// <summary>
    /// Multiplies every font size. 1.0 = the exact point sizes stored in the .rpt. Crystal's own PDF export draws text
    /// noticeably narrower than FastReport at the same nominal size (measured about 0.86 on the Depot Despatch report),
    /// so a value around 0.87 makes the converted report look like the Crystal output; it does not change any layout.
    /// </summary>
    public double FontScale { get; set; } = 1.0;
}

public sealed class FrxResult
{
    public string Xml { get; set; } = string.Empty;

    /// <summary>Everything the converter could not translate or had to approximate. Empty = fully converted.</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Turns a <see cref="ReportMetadata"/> into a FastReport <c>.frx</c> (XML). Deterministic: the same metadata always
/// yields the same template. Layout (page, bands, text, lines, boxes, fields, groups, formats) is translated directly;
/// formulas go through <see cref="CrystalFormulaTranslator"/> and unsupported ones are listed in <see cref="FrxResult.Warnings"/>.
/// Units: FastReport stores object geometry in pixels at 96 dpi (twips / 15) and page size in millimetres (twips / 56.6929).
/// </summary>
public sealed class FrxGenerator
{
    private ReportMetadata _m = null!;
    private FrxResult _result = null!;
    private XmlWriter _w = null!;
    private int _counter;
    private double _bandTop;
    private double _bandWidth;
    private string _dataSourceName = "";
    private FrxOptions _options = new();
    private string? _pictureBase64;

    private readonly Dictionary<string, FormulaMetadata> _formulas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FormulaInfo> _formulaInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TotalDef> _totals = new();
    private readonly Dictionary<string, int> _resetGroup = new(StringComparer.OrdinalIgnoreCase);

    private sealed record TotalDef(string Name, string Expression, string PrintOn, bool ResetAfterPrint, string TotalType);

    private static readonly Dictionary<string, string> SpecialFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PageNumber"] = "[Page#]",
        ["TotalPageCount"] = "[TotalPages#]",
        ["PageNofM"] = "Page [Page#] of [TotalPages#]",
        ["PrintDate"] = "[Date]",
        ["PrintTime"] = "[Time]",
        ["RecordNumber"] = "[Row#]",
        ["ModificationDate"] = "[Date]",
        ["DataDate"] = "[Date]",
        ["DataTime"] = "[Time]"
    };

    public FrxResult Generate(ReportMetadata metadata, FrxOptions? options = null)
    {
        _m = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _result = new FrxResult();
        _counter = 0;
        _totals.Clear();
        _resetGroup.Clear();
        _formulas.Clear();
        _formulaInfo.Clear();
        options ??= new FrxOptions();
        _options = options;
        _pictureBase64 = null;
        if (!string.IsNullOrEmpty(options.PicturePath))
        {
            if (File.Exists(options.PicturePath)) _pictureBase64 = Convert.ToBase64String(File.ReadAllBytes(options.PicturePath));
            else Warn("Picture file not found: " + options.PicturePath);
        }

        foreach (var f in _m.Formulas)
        {
            var bare = f.FormulaName.TrimStart('{', '@').TrimEnd('}');
            _formulas[bare] = f;
        }

        foreach (var kv in _formulas)
        {
            _formulaInfo[kv.Key] = CrystalFormulaTranslator.Analyze(kv.Value.Text, Resolve);
        }

        if (_m.DataSource.Tables.Count > 1)
        {
            Warn("The report uses " + _m.DataSource.Tables.Count + " tables; the template binds only the first (" + _m.DataSource.Tables[0].Alias + "). Joins must be done in the data source.");
        }

        _dataSourceName = _m.DataSource.Tables.FirstOrDefault()?.Alias ?? "Data";
        PlanTotals();

        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false }))
        {
            _w = xw;
            WriteReport();
        }

        _result.Xml = sb.ToString();
        return _result;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private string? Resolve(string name) => _formulas.TryGetValue(name, out var f) ? f.Text : null;

    private void Warn(string message) => _result.Warnings.Add(message);

    private static string Px(double twips) => (twips / 15.0).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Mm(double twips) => (twips / Units.TwipsPerMm).ToString("0.##", CultureInfo.InvariantCulture);

    private string NextName(string prefix) => prefix + (++_counter).ToString(CultureInfo.InvariantCulture);

    private static string BareFormulaName(string dataSource) => dataSource.TrimStart('{', '@').TrimEnd('}');

    private static string Color(string? hex) => hex ?? "#000000";

    // ------------------------------------------------------------------------------------------------ totals

    /// <summary>
    /// Crystal builds running sums with three helper formulas (accumulate / print / reset). FastReport has a native
    /// <c>Total</c> for that, so the helpers are folded into Totals and the helper objects are not emitted.
    /// </summary>
    private void PlanTotals()
    {
        // where is each accumulator reset? (the reset formula sits in the group footer where the sum starts over)
        foreach (var section in _m.Sections)
        {
            foreach (var o in section.Objects.Where(o => o.Kind == ObjectKind.Field && o.Field?.DataSource != null))
            {
                if (TryFormula(o.Field!.DataSource!, out var info) && info.Kind == FormulaKind.Reset && info.Variable != null && section.GroupIndex.HasValue)
                {
                    _resetGroup[info.Variable] = section.GroupIndex.Value;
                }
            }
        }

        // every place an accumulator is printed needs a Total scoped to that band
        foreach (var section in _m.Sections)
        {
            foreach (var o in section.Objects.Where(o => o.Kind == ObjectKind.Field && o.Field?.DataSource != null))
            {
                if (TryFormula(o.Field!.DataSource!, out var agg) && agg.Kind == FormulaKind.Aggregate && agg.Expression != null)
                {
                    // Sum({..}) printed in a footer/summary band: a Total scoped to that band
                    var aggName = AggregateTotalName(o.Field.DataSource!, section);
                    if (_totals.All(t => t.Name != aggName))
                    {
                        _totals.Add(new TotalDef(aggName, agg.Expression, BandNameFor(section), section.Kind == "GroupFooter", AggregateType(agg.Variable)));
                    }

                    continue;
                }

                if (!TryFormula(o.Field!.DataSource!, out var info) || info.Kind != FormulaKind.Show || info.Variable == null) continue;
                var operand = FindAccumulatorExpression(info.Variable);
                if (operand == null)
                {
                    Warn("Total '" + info.Variable + "' is printed but no accumulator formula was found for it.");
                    continue;
                }

                EnsureTotal(info.Variable, operand, section);
            }
        }

        foreach (var s in _m.SummaryFields)
        {
            if (s.SummarizedField == null) continue;
            var summarised = SummarizedExpression(s.SummarizedField);
            if (summarised == null)
            {
                Warn("Summary " + s.FormulaName + " sums " + s.SummarizedField + ", which is not a plain field or simple formula; needs manual conversion.");
                continue;
            }

            var section = _m.Sections.FirstOrDefault(x => x.Kind == (s.GroupIndex.HasValue ? "GroupFooter" : "ReportFooter") && (!s.GroupIndex.HasValue || x.GroupIndex == s.GroupIndex));
            var printOn = section == null ? "ReportSummary1" : BandNameFor(section);
            var type = s.Operation switch
            {
                "Sum" => "Sum",
                "Count" or "DistinctCount" => "Count",
                "Average" => "Avg",
                "Maximum" => "Max",
                "Minimum" => "Min",
                _ => "Sum"
            };
            _totals.Add(new TotalDef(SummaryTotalName(s), summarised, printOn, s.GroupIndex.HasValue, type));
        }
    }

    /// <summary>Expression a summary field sums: a database field, or a formula that reduces to a plain expression.</summary>
    private string? SummarizedExpression(string summarizedField)
    {
        if (!summarizedField.StartsWith("{@", StringComparison.Ordinal)) return "[" + summarizedField.Trim('{', '}') + "]";
        return TryFormula(summarizedField, out var info) && info.Kind == FormulaKind.Pure ? info.Expression : null;
    }

    private static string AggregateTotalName(string formulaName, SectionMetadata section) => "Agg_" + Regex.Replace(formulaName, @"\W+", "_").Trim('_') + "_" + BandNameFor(section);

    private static string AggregateType(string? function) => function switch
    {
        "count" => "Count",
        "distinctcount" => "CountDistinct",
        "average" => "Avg",
        "maximum" => "Max",
        "minimum" => "Min",
        _ => "Sum"
    };

    private static string SummaryTotalName(SummaryFieldMetadata s) => "Sum_" + Regex.Replace(s.FormulaName, @"\W+", "_").Trim('_');

    private bool TryFormula(string dataSource, out FormulaInfo info)
    {
        info = null!;
        if (!dataSource.StartsWith("{@", StringComparison.Ordinal)) return false;
        return _formulaInfo.TryGetValue(BareFormulaName(dataSource), out info!);
    }

    private string? FindAccumulatorExpression(string variable)
    {
        foreach (var kv in _formulaInfo)
        {
            if (kv.Value.Kind == FormulaKind.Accumulator && string.Equals(kv.Value.Variable, variable, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value.Expression;
            }
        }

        return null;
    }

    private void EnsureTotal(string variable, string expression, SectionMetadata printedIn)
    {
        var printOn = BandNameFor(printedIn);
        var name = TotalName(variable, printOn);
        if (_totals.Any(t => t.Name == name)) return;

        var resets = _resetGroup.TryGetValue(variable, out var g) && printedIn.Kind == "GroupFooter" && printedIn.GroupIndex == g;
        _totals.Add(new TotalDef(name, expression, printOn, resets, "Sum"));
    }

    private static string TotalName(string variable, string band) => "Tot_" + variable + "_" + band;

    private static string BandNameFor(SectionMetadata s) => s.Kind switch
    {
        "GroupHeader" => "GroupHeader" + (s.GroupIndex ?? 1),
        "GroupFooter" => "GroupFooter" + (s.GroupIndex ?? 1),
        "ReportFooter" => "ReportSummary1",
        "PageFooter" => "PageFooter1",
        "PageHeader" => "PageHeader1",
        "ReportHeader" => "ReportTitle1",
        _ => "Data1"
    };

    // ------------------------------------------------------------------------------------------------ document

    private void WriteReport()
    {
        _w.WriteStartDocument();
        _w.WriteStartElement("Report");
        _w.WriteAttributeString("ScriptLanguage", "CSharp");
        _w.WriteAttributeString("ReportInfo.Description", "Generated by CrystalMetadata from " + _m.ReportInfo.FileName + " (schema " + _m.SchemaVersion + ")");
        _w.WriteAttributeString("ReportInfo.Name", _m.ReportInfo.ReportName);

        WriteDictionary();
        WritePage();

        _w.WriteEndElement();
        _w.WriteEndDocument();
    }

    private void WriteDictionary()
    {
        _w.WriteStartElement("Dictionary");

        foreach (var t in _m.DataSource.Tables.Take(1))
        {
            _w.WriteStartElement("TableDataSource");
            _w.WriteAttributeString("Name", t.Alias);
            _w.WriteAttributeString("ReferenceName", "Data." + t.Alias);
            _w.WriteAttributeString("DataType", "System.Int32");
            _w.WriteAttributeString("Enabled", "true");
            foreach (var f in t.Fields)
            {
                _w.WriteStartElement("Column");
                _w.WriteAttributeString("Name", f.Name);
                _w.WriteAttributeString("DataType", ClrType(f.ValueType));
                _w.WriteEndElement();
            }

            _w.WriteEndElement();
        }

        foreach (var p in _m.Parameters.Where(p => !p.IsLinkedFromParentReport))
        {
            _w.WriteStartElement("Parameter");
            _w.WriteAttributeString("Name", p.Name);
            _w.WriteAttributeString("DataType", ClrType(p.ValueType));
            _w.WriteEndElement();
        }

        foreach (var t in _totals)
        {
            _w.WriteStartElement("Total");
            _w.WriteAttributeString("Name", t.Name);
            if (t.TotalType != "Sum") _w.WriteAttributeString("TotalType", t.TotalType);
            _w.WriteAttributeString("Expression", t.Expression);
            _w.WriteAttributeString("Evaluator", "Data1");
            _w.WriteAttributeString("PrintOn", t.PrintOn);
            if (t.ResetAfterPrint) _w.WriteAttributeString("ResetAfterPrint", "true");
            _w.WriteEndElement();
        }

        _w.WriteEndElement();
    }

    private static string ClrType(string? crystalType)
    {
        var t = crystalType ?? string.Empty;
        if (t.Contains("Currency", StringComparison.OrdinalIgnoreCase)) return "System.Decimal";
        if (t.Contains("Number", StringComparison.OrdinalIgnoreCase)) return "System.Double";
        if (t.Contains("Int", StringComparison.OrdinalIgnoreCase)) return "System.Int32";
        if (t.Contains("DateTime", StringComparison.OrdinalIgnoreCase) || t.Contains("Date", StringComparison.OrdinalIgnoreCase)) return "System.DateTime";
        if (t.Contains("Time", StringComparison.OrdinalIgnoreCase)) return "System.TimeSpan";
        if (t.Contains("Boolean", StringComparison.OrdinalIgnoreCase)) return "System.Boolean";
        return "System.String";
    }

    // ------------------------------------------------------------------------------------------------ page and bands

    private void WritePage()
    {
        var ps = _m.PageSetup;
        var pageW = ps.PageWidthTwips ?? 11906;
        var pageH = ps.PageHeightTwips ?? 16838;
        var left = ps.Margins.LeftTwips ?? 0;
        var right = ps.Margins.RightTwips ?? 0;
        var top = ps.Margins.TopTwips ?? 0;
        var bottom = ps.Margins.BottomTwips ?? 0;
        _bandWidth = (pageW - left - right) / 15.0;

        _w.WriteStartElement("ReportPage");
        _w.WriteAttributeString("Name", "Page1");
        if (string.Equals(ps.PaperOrientation, "Landscape", StringComparison.OrdinalIgnoreCase)) _w.WriteAttributeString("Landscape", "true");
        _w.WriteAttributeString("PaperWidth", Mm(pageW));
        _w.WriteAttributeString("PaperHeight", Mm(pageH));
        _w.WriteAttributeString("LeftMargin", Mm(left));
        _w.WriteAttributeString("TopMargin", Mm(top));
        _w.WriteAttributeString("RightMargin", Mm(right));
        _w.WriteAttributeString("BottomMargin", Mm(bottom));

        _bandTop = 0;
        var byKind = _m.Sections.Where(s => !s.Format.IsAlwaysSuppressed()).ToLookup(s => s.Kind);

        WriteSimpleBand("ReportTitleBand", "ReportTitle1", byKind["ReportHeader"].ToList());
        WriteSimpleBand("PageHeaderBand", "PageHeader1", byKind["PageHeader"].ToList());

        var groupCount = _m.Groups.Count;
        if (groupCount > 0)
        {
            WriteGroup(1, byKind);
        }
        else
        {
            WriteDataBand(byKind["Detail"].ToList());
        }

        WriteSimpleBand("ReportSummaryBand", "ReportSummary1", byKind["ReportFooter"].ToList());
        WriteSimpleBand("PageFooterBand", "PageFooter1", byKind["PageFooter"].ToList());

        _w.WriteEndElement();
    }

    /// <summary>Header / footer / summary bands: all Crystal sections of that kind stacked into one FastReport band.</summary>
    private void WriteSimpleBand(string element, string name, List<SectionMetadata> sections)
    {
        if (sections.Count == 0 || sections.All(s => s.Objects.Count == 0)) return;
        var height = sections.Sum(s => s.HeightTwips);
        StartBand(element, name, height);
        WriteObjects(sections);
        _w.WriteEndElement();
        _bandTop += height / 15.0;
    }

    private void StartBand(string element, string name, double heightTwips)
    {
        _w.WriteStartElement(element);
        _w.WriteAttributeString("Name", name);
        _w.WriteAttributeString("Top", _bandTop.ToString("0.##", CultureInfo.InvariantCulture));
        _w.WriteAttributeString("Width", _bandWidth.ToString("0.##", CultureInfo.InvariantCulture));
        _w.WriteAttributeString("Height", Px(heightTwips));
    }

    private void WriteGroup(int level, ILookup<string, SectionMetadata> byKind)
    {
        var group = _m.Groups.FirstOrDefault(g => g.Index == level);
        var headers = byKind["GroupHeader"].Where(s => s.GroupIndex == level).ToList();
        var footers = byKind["GroupFooter"].Where(s => s.GroupIndex == level).ToList();

        var headerHeight = headers.Sum(s => s.HeightTwips);
        StartBand("GroupHeaderBand", "GroupHeader" + level, headerHeight);
        _w.WriteAttributeString("Condition", "[" + (group?.ConditionField ?? "").Trim('{', '}') + "]");
        if (string.Equals(group?.SortDirection, "DescendingOrder", StringComparison.OrdinalIgnoreCase)) _w.WriteAttributeString("SortOrder", "Descending");
        else if (string.Equals(group?.SortDirection, "NoSortOrder", StringComparison.OrdinalIgnoreCase) || string.Equals(group?.SortDirection, "OriginalOrder", StringComparison.OrdinalIgnoreCase)) _w.WriteAttributeString("SortOrder", "None");
        if (group?.KeepGroupTogether == true) _w.WriteAttributeString("KeepTogether", "true");
        WriteObjects(headers);
        _bandTop += headerHeight / 15.0;

        if (level < _m.Groups.Count)
        {
            WriteGroup(level + 1, byKind);
        }
        else
        {
            WriteDataBand(byKind["Detail"].ToList());
        }

        var footerHeight = footers.Sum(s => s.HeightTwips);
        StartBand("GroupFooterBand", "GroupFooter" + level, footerHeight);
        WriteObjects(footers);
        _w.WriteEndElement();
        _bandTop += footerHeight / 15.0;

        _w.WriteEndElement();
    }

    private void WriteDataBand(List<SectionMetadata> details)
    {
        var height = details.Sum(s => s.HeightTwips);
        StartBand("DataBand", "Data1", height);
        _w.WriteAttributeString("DataSource", _dataSourceName);
        WriteObjects(details);
        WriteSpanningLines(details, height);
        _w.WriteEndElement();
        _bandTop += height / 15.0;
    }

    // ------------------------------------------------------------------------------------------------ objects

    /// <summary>Writes the objects of several stacked sections; later sections are offset by the heights before them.</summary>
    private void WriteObjects(List<SectionMetadata> sections)
    {
        double offset = 0;
        foreach (var section in sections)
        {
            // Crystal paints filled boxes behind the text they frame even when the SDK lists them last, so filled boxes
            // are emitted first (FastReport paints in document order).
            var ordered = section.Objects.Where(IsFilledBox).Concat(section.Objects.Where(o => !IsFilledBox(o)));
            foreach (var o in ordered)
            {
                WriteObject(o, section, offset);
            }

            offset += section.HeightTwips;
        }
    }

    private static bool IsFilledBox(ObjectMetadata o) => o.Kind == ObjectKind.Box && !string.IsNullOrEmpty(o.Box?.FillColor);

    private void WriteObject(ObjectMetadata o, SectionMetadata section, double offsetTwips)
    {
        switch (o.Kind)
        {
            case ObjectKind.Text:
                WriteTextObject(o, o.Text?.Text ?? string.Empty, section, offsetTwips, isField: false);
                break;
            case ObjectKind.FieldHeading:
                WriteTextObject(o, o.FieldHeading?.Text ?? o.Text?.Text ?? string.Empty, section, offsetTwips, isField: false);
                break;
            case ObjectKind.Field:
                WriteFieldObject(o, section, offsetTwips);
                break;
            case ObjectKind.Line:
                WriteLine(o, section, offsetTwips);
                break;
            case ObjectKind.Box:
                WriteBox(o, section, offsetTwips);
                break;
            case ObjectKind.Picture:
                WritePicturePlaceholder(o, section, offsetTwips);
                break;
            case ObjectKind.Chart:
                Warn("Chart '" + o.Name + "' is not supported by FastReport Open Source (commercial edition only); skipped.");
                break;
            case ObjectKind.CrossTab:
                Warn("Cross-tab '" + o.Name + "' needs manual conversion to a FastReport Matrix; skipped.");
                break;
            case ObjectKind.Subreport:
                Warn("Subreport '" + (o.Subreport?.SubreportName ?? o.Name) + "' is not converted yet; skipped.");
                break;
            default:
                Warn("Object '" + o.Name + "' of kind " + o.Kind + " is not converted; skipped.");
                break;
        }
    }

    private void StartObject(string element, ObjectMetadata o, double offsetTwips, string prefix)
    {
        _w.WriteStartElement(element);
        _w.WriteAttributeString("Name", NextName(prefix));
        _w.WriteAttributeString("Left", Px(o.LeftTwips));
        _w.WriteAttributeString("Top", Px(o.TopTwips + offsetTwips));
        _w.WriteAttributeString("Width", Px(o.WidthTwips));
        _w.WriteAttributeString("Height", Px(o.HeightTwips));
        WriteVisibility(o);
    }

    /// <summary>
    /// Always-suppressed objects become <c>Visible="false"</c>. Objects with a suppress FORMULA stay visible and get a
    /// <c>VisibleExpression</c> (Crystal suppresses when the formula is true, FastReport shows when the expression is true).
    /// </summary>
    private void WriteVisibility(ObjectMetadata o)
    {
        if (o.Format.IsAlwaysSuppressed())
        {
            _w.WriteAttributeString("Visible", "false");
            return;
        }

        var formula = o.Format.SuppressFormula();
        if (formula == null) return;

        var expression = CrystalFormulaTranslator.Translate(formula, Resolve, 0, out var reason);
        if (expression == null)
        {
            Warn("Suppress condition of '" + o.Name + "' could not be translated (" + reason + "); the object is always shown. Original: " + OneLine(formula));
            return;
        }

        _w.WriteAttributeString("VisibleExpression", "!(" + expression + ")");
    }

    private void WriteBorderAndFill(ObjectMetadata o)
    {
        var b = o.Border;
        if (b == null) return;

        var lines = new List<string>();
        if (IsLine(b.Left)) lines.Add("Left");
        if (IsLine(b.Right)) lines.Add("Right");
        if (IsLine(b.Top)) lines.Add("Top");
        if (IsLine(b.Bottom)) lines.Add("Bottom");
        if (lines.Count == 4) _w.WriteAttributeString("Border.Lines", "All");
        else if (lines.Count > 0) _w.WriteAttributeString("Border.Lines", string.Join(", ", lines));
        if (lines.Count > 0 && b.BorderColor != null) _w.WriteAttributeString("Border.Color", b.BorderColor);
        // null = transparent (RAS). White is treated as transparent too: older metadata only had the Engine value, which
        // reports "no background" as white, and a white fill would wrongly hide the shaded box behind the text.
        if (!string.IsNullOrEmpty(b.BackgroundColor) && !b.BackgroundColor.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase))
        {
            _w.WriteAttributeString("Fill.Color", b.BackgroundColor);
        }
    }

    private static bool IsLine(string? style) => !string.IsNullOrEmpty(style) && !style.Equals("NoLine", StringComparison.OrdinalIgnoreCase);

    private void WriteFont(ObjectMetadata o)
    {
        var f = o.Font;
        if (f == null) return;

        var sb = new StringBuilder();
        var size = Math.Round((f.SizePoints ?? 9) * (_options.FontScale > 0 ? _options.FontScale : 1.0), 1);
        sb.Append(f.Name ?? "Arial").Append(", ").Append(size.ToString("0.##", CultureInfo.InvariantCulture)).Append("pt");
        var styles = new List<string>();
        if (f.Bold == true) styles.Add("Bold");
        if (f.Italic == true) styles.Add("Italic");
        if (f.Underline == true) styles.Add("Underline");
        if (f.Strikeout == true) styles.Add("Strikeout");
        if (styles.Count > 0) sb.Append(", style=").Append(string.Join(", ", styles));
        _w.WriteAttributeString("Font", sb.ToString());
        if (!string.IsNullOrEmpty(f.Color) && f.Color != "#000000") _w.WriteAttributeString("TextFill.Color", f.Color);
    }

    private void WriteAlignment(ObjectMetadata o, bool numeric)
    {
        var horz = o.Format.HorizontalAlignment switch
        {
            "LeftAlign" => "Left",
            "HorizontalCenterAlign" => "Center",
            "RightAlign" => "Right",
            "Justified" => "Justify",
            _ => numeric ? "Right" : "Left"
        };
        if (horz != "Left") _w.WriteAttributeString("HorzAlign", horz);
    }

    private void WriteTextObject(ObjectMetadata o, string text, SectionMetadata section, double offsetTwips, bool isField)
    {
        var (frText, hasExpressions) = ConvertText(text);
        StartObject("TextObject", o, offsetTwips, "Text");
        _w.WriteAttributeString("Text", frText);
        _w.WriteAttributeString("Padding", "0, 0, 0, 0"); // Crystal has no inner padding; FastReport defaults to 2 px each side
        if (!hasExpressions) _w.WriteAttributeString("AllowExpressions", "false");
        WriteBorderAndFill(o);
        WriteFont(o);
        WriteAlignment(o, numeric: false);
        if (o.Format.CanGrow == true) _w.WriteAttributeString("CanGrow", "true");
        _w.WriteEndElement();
    }

    private (string Text, bool HasExpressions) ConvertText(string text)
    {
        var hasExpr = false;
        var result = Regex.Replace(text, @"\{([^{}]+)\}", m =>
        {
            var inner = m.Groups[1].Value;
            if (SpecialFields.TryGetValue(inner, out var special))
            {
                hasExpr = true;
                return special;
            }

            if (inner.StartsWith("?", StringComparison.Ordinal) || !inner.StartsWith("@", StringComparison.Ordinal) && !inner.StartsWith("#", StringComparison.Ordinal))
            {
                hasExpr = true;
                return "[" + inner.TrimStart('?') + "]";
            }

            Warn("Embedded reference " + m.Value + " in a text object was left as-is.");
            return m.Value;
        });
        return (result, hasExpr);
    }

    private void WriteFieldObject(ObjectMetadata o, SectionMetadata section, double offsetTwips)
    {
        var ds = o.Field?.DataSource ?? string.Empty;
        var numeric = IsNumeric(o.Field?.ValueType);

        // helper formulas of a Crystal running sum are folded into Totals
        if (TryFormula(ds, out var info) && (info.Kind == FormulaKind.Accumulator || info.Kind == FormulaKind.Reset))
        {
            return;
        }

        string? text;
        if (SpecialFields.TryGetValue(ds, out var special))
        {
            text = special;
        }
        else if (ds.StartsWith("{@", StringComparison.Ordinal))
        {
            text = FormulaText(ds, section, o);
        }
        else if (ds.StartsWith("{?", StringComparison.Ordinal))
        {
            text = "[" + ds.Trim('{', '}').TrimStart('?') + "]";
        }
        else if (ds.StartsWith("{#", StringComparison.Ordinal))
        {
            text = RunningTotalText(ds);
        }
        else if (ds.StartsWith("{%", StringComparison.Ordinal))
        {
            Warn("SQL expression field " + ds + " in '" + o.Name + "' is not converted.");
            text = null;
        }
        else if (ds.StartsWith("{", StringComparison.Ordinal))
        {
            text = "[" + ds.Trim('{', '}') + "]";
        }
        else
        {
            var summary = _m.SummaryFields.FirstOrDefault(s => s.FormulaName == ds);
            if (summary != null)
            {
                text = "[" + SummaryTotalName(summary) + "]";
                numeric = true;
            }
            else
            {
                Warn("Field '" + o.Name + "' has an unrecognised data source '" + ds + "'.");
                text = null;
            }
        }

        StartObject("TextObject", o, offsetTwips, "Text");
        _w.WriteAttributeString("Text", text ?? string.Empty);
        _w.WriteAttributeString("Padding", "0, 0, 0, 0");
        WriteBorderAndFill(o);
        WriteFont(o);
        WriteAlignment(o, numeric);
        WriteNumberFormat(o, numeric);
        var isDate = text is "[Date]" || (o.Field?.ValueType ?? "").Contains("Date", StringComparison.OrdinalIgnoreCase);
        if (isDate || text is "[Time]")
        {
            // date/time values must be formatted explicitly; use Crystal's own date pattern when the metadata has it
            _w.WriteAttributeString("Format", isDate ? "Date" : "Time");
            _w.WriteAttributeString("Format.Format", isDate ? DatePattern(o.Field?.FieldFormat.Date) : "t");
        }

        if (o.Format.CanGrow == true) _w.WriteAttributeString("CanGrow", "true");
        _w.WriteAttributeString("WordWrap", o.Format.CanGrow == true ? "true" : "false");
        // Crystal lets a non-growing field overflow its box; FastReport clips by default
        if (o.Format.CanGrow != true) _w.WriteAttributeString("Clip", "false");
        _w.WriteEndElement();
    }

    private string? FormulaText(string dataSource, SectionMetadata section, ObjectMetadata o)
    {
        if (!TryFormula(dataSource, out var info))
        {
            Warn("Field '" + o.Name + "' refers to unknown formula " + dataSource + ".");
            return null;
        }

        switch (info.Kind)
        {
            case FormulaKind.Pure:
                return "[" + info.Expression + "]";
            case FormulaKind.Show:
            {
                var total = TotalName(info.Variable!, BandNameFor(section));
                // "numbervar v; v/1000": keep the arithmetic around the Total reference
                // FastReport needs data items bracketed inside an expression: [[Total]/1000]
                return info.Expression == null
                    ? "[" + total + "]"
                    : "[" + Regex.Replace(info.Expression, @"\b" + Regex.Escape(info.Variable!) + @"\b", "[" + total + "]", RegexOptions.IgnoreCase) + "]";
            }
            case FormulaKind.Aggregate:
                return "[" + AggregateTotalName(dataSource, section) + "]";
            default:
                Warn("Formula " + dataSource + " (used by '" + o.Name + "') needs manual translation: " + (info.Reason ?? "unsupported construct") + ". Original: " + OneLine(_formulas[BareFormulaName(dataSource)].Text));
                return null;
        }
    }

    private string? RunningTotalText(string dataSource)
    {
        var rt = _m.RunningTotals.FirstOrDefault(r => r.FormulaName == dataSource);
        if (rt == null)
        {
            Warn("Running total " + dataSource + " not found in the metadata.");
            return null;
        }

        // a per-record Count that restarts on each group is a row counter
        if (rt.Operation == "Count" && rt.EvaluationConditionType == "NoCondition" && rt.ResetConditionType == "OnChangeOfGroup")
        {
            return "[Row#]";
        }

        // resetting on change of the field the innermost group is built on is the same thing
        var innermost = _m.Groups.OrderBy(g => g.Index).LastOrDefault()?.ConditionField;
        if (rt.Operation == "Count" && rt.EvaluationConditionType == "NoCondition" && rt.ResetConditionType == "OnChangeOfField" && innermost != null && string.Equals(rt.ResetCondition, innermost, StringComparison.OrdinalIgnoreCase))
        {
            return "[Row#]";
        }

        Warn("Running total " + dataSource + " (" + rt.Operation + ", reset " + rt.ResetConditionType + ") is not converted automatically.");
        return null;
    }

    private static string OneLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Builds a .NET date pattern from Crystal's day/month/year formats (defaults to dd/MM/yyyy).</summary>
    private static string DatePattern(DateFormatMetadata? f)
    {
        if (f == null) return "dd'/'MM'/'yyyy";

        var day = f.DayFormat switch { "NumericDay" => "d", "NoDay" => "", _ => "dd" };
        var month = f.MonthFormat switch { "NumericMonth" => "M", "ShortMonth" => "MMM", "LongMonth" => "MMMM", "NoMonth" => "", _ => "MM" };
        var year = f.YearFormat switch { "ShortYear" => "yy", "NoYear" => "", _ => "yyyy" };
        var sep = string.IsNullOrEmpty(f.Separator) ? "/" : f.Separator;

        var parts = (f.DateOrder ?? "DayMonthYear") switch
        {
            var s when s.StartsWith("MonthDay", StringComparison.OrdinalIgnoreCase) => new[] { month, day, year },
            var s when s.StartsWith("Year", StringComparison.OrdinalIgnoreCase) => new[] { year, month, day },
            _ => new[] { day, month, year }
        };
        // quote the separator so it prints literally instead of becoming the machine's date separator ("-" on some cultures)
        return string.Join("'" + sep + "'", parts.Where(p => p.Length > 0));
    }

    private static bool IsNumeric(string? valueType) =>
        valueType != null && (valueType.Contains("Number", StringComparison.OrdinalIgnoreCase) || valueType.Contains("Currency", StringComparison.OrdinalIgnoreCase) || valueType.Contains("Int", StringComparison.OrdinalIgnoreCase));

    private void WriteNumberFormat(ObjectMetadata o, bool numeric)
    {
        var n = o.Field?.FieldFormat.Numeric;
        if (!numeric || n == null) return;

        _w.WriteAttributeString("Format", (o.Field?.ValueType ?? "").Contains("Currency", StringComparison.OrdinalIgnoreCase) ? "Currency" : "Number");
        _w.WriteAttributeString("Format.UseLocale", "false");
        _w.WriteAttributeString("Format.DecimalDigits", (n.DecimalPlaces ?? 0).ToString(CultureInfo.InvariantCulture));
        _w.WriteAttributeString("Format.DecimalSeparator", n.DecimalSymbol ?? ".");
        _w.WriteAttributeString("Format.GroupSeparator", n.ThousandsSeparator == false ? "" : (n.ThousandsSeparatorSymbol ?? ","));
        _w.WriteAttributeString("Format.NegativePattern", "1");
    }

    private void WriteLine(ObjectMetadata o, SectionMetadata section, double offsetTwips)
    {
        const int AxisTolerance = 30; // Crystal stores "horizontal" lines with a 1-15 twip skew; FastReport would draw them slanted
        double width = o.WidthTwips;
        double height = o.HeightTwips;
        if (o.Line?.ExtendToBottomOfSection == true && width <= AxisTolerance)
        {
            height = Math.Max(0, section.HeightTwips - o.TopTwips);
        }

        if (height <= AxisTolerance) height = 0;
        if (width <= AxisTolerance) width = 0;
        if (width == 0 && height == 0) return;

        // a vertical line that starts in this section but ends in a later one is clipped to this band; the other bands it crosses get
        // their own segment (see WriteSpanningLines)
        if (width == 0 && IsSpanning(o, section))
        {
            height = Math.Max(0, section.HeightTwips - o.TopTwips);
        }

        WriteLineElement(o, o.LeftTwips, o.TopTwips + offsetTwips, width, height);
    }

    private static bool IsSpanning(ObjectMetadata o, SectionMetadata section) =>
        o.Kind == ObjectKind.Line && !string.IsNullOrEmpty(o.Line?.EndSectionName) &&
        !string.Equals(o.Line!.EndSectionName, section.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Crystal lets a vertical line start in a group header and end in a later section; it is drawn through every detail row in
    /// between (table borders). FastReport lines cannot leave their band, so each detail band gets a full-height copy.
    /// </summary>
    private void WriteSpanningLines(List<SectionMetadata> details, double bandHeightTwips)
    {
        foreach (var s in _m.Sections.Where(x => x.Kind == "GroupHeader"))
        {
            foreach (var o in s.Objects.Where(x => x.Kind == ObjectKind.Line && x.WidthTwips <= 30 && IsSpanning(x, s)))
            {
                var end = _m.Sections.FirstOrDefault(x => string.Equals(x.Name, o.Line!.EndSectionName, StringComparison.OrdinalIgnoreCase));
                if (end == null || end.Kind is "GroupHeader" or "PageHeader" or "ReportHeader") continue;
                WriteLineElement(o, o.LeftTwips, 0, 0, bandHeightTwips);
            }
        }
    }

    private void WriteLineElement(ObjectMetadata o, double leftTwips, double topTwips, double width, double height)
    {
        _w.WriteStartElement("LineObject");
        _w.WriteAttributeString("Name", NextName("Line"));
        _w.WriteAttributeString("Left", Px(leftTwips));
        _w.WriteAttributeString("Top", Px(topTwips));
        _w.WriteAttributeString("Width", Px(width));
        _w.WriteAttributeString("Height", Px(height));
        WriteVisibility(o);
        if (width > 0 && height > 0) _w.WriteAttributeString("Diagonal", "true");
        _w.WriteAttributeString("Border.Color", Color(o.Line?.Color));
        _w.WriteAttributeString("Border.Width", Math.Max(1, (o.Line?.ThicknessTwips ?? 15) / 15.0).ToString("0.##", CultureInfo.InvariantCulture));
        WriteLineStyle(o.Line?.LineStyle);
        _w.WriteEndElement();
    }

    private void WriteLineStyle(string? style)
    {
        var mapped = style switch
        {
            "DashLine" => "Dash",
            "DotLine" => "Dot",
            "DoubleLine" => "Double",
            _ => null
        };
        if (mapped != null) _w.WriteAttributeString("Border.Style", mapped);
    }

    private void WriteBox(ObjectMetadata o, SectionMetadata section, double offsetTwips)
    {
        var height = o.HeightTwips;
        if (o.Box?.ExtendToBottomOfSection == true) height = Math.Max(0, section.HeightTwips - o.TopTwips);

        _w.WriteStartElement("ShapeObject");
        _w.WriteAttributeString("Name", NextName("Shape"));
        _w.WriteAttributeString("Left", Px(o.LeftTwips));
        _w.WriteAttributeString("Top", Px(o.TopTwips + offsetTwips));
        _w.WriteAttributeString("Width", Px(o.WidthTwips));
        _w.WriteAttributeString("Height", Px(height));
        WriteVisibility(o);
        if ((o.Box?.CornerEllipseWidthTwips ?? 0) > 0)
        {
            _w.WriteAttributeString("Shape", "RoundRectangle");
            // FastReport's Curve is a relative value, not a pixel radius: 7 px produced a capsule where Crystal's 105-twip
            // ellipse is a slight rounding, so it is scaled down to a comparable radius
            _w.WriteAttributeString("Curve", Math.Max(0.1, o.Box!.CornerEllipseWidthTwips!.Value / 420.0).ToString("0.##", CultureInfo.InvariantCulture));
        }
        _w.WriteAttributeString("Border.Color", Color(o.Box?.LineColor));
        _w.WriteAttributeString("Border.Width", Math.Max(1, (o.Box?.ThicknessTwips ?? 15) / 15.0).ToString("0.##", CultureInfo.InvariantCulture));
        WriteLineStyle(o.Box?.LineStyle);
        if (!string.IsNullOrEmpty(o.Box?.FillColor)) _w.WriteAttributeString("Fill.Color", o.Box!.FillColor);
        _w.WriteEndElement();
    }

    /// <summary>The Crystal SDK does not expose embedded image bytes, so pictures become empty, outlined placeholders.</summary>
    private void WritePicturePlaceholder(ObjectMetadata o, SectionMetadata section, double offsetTwips)
    {
        if (_pictureBase64 == null)
        {
            Warn("Picture '" + o.Name + "' (" + Px(o.WidthTwips) + "x" + Px(o.HeightTwips) + " px) was replaced by an empty placeholder: the image data is not available from Crystal metadata. Supply the image file (convert --picture logo.png).");
        }

        _w.WriteStartElement("PictureObject");
        _w.WriteAttributeString("Name", NextName("Picture"));
        _w.WriteAttributeString("Left", Px(o.LeftTwips));
        _w.WriteAttributeString("Top", Px(o.TopTwips + offsetTwips));
        _w.WriteAttributeString("Width", Px(o.WidthTwips));
        _w.WriteAttributeString("Height", Px(o.HeightTwips));
        if (_pictureBase64 == null)
        {
            _w.WriteAttributeString("Border.Lines", "All");
            _w.WriteAttributeString("Border.Color", "Silver");
        }

        _w.WriteAttributeString("SizeMode", "Zoom");
        if (_pictureBase64 != null) _w.WriteAttributeString("Image", _pictureBase64);
        _w.WriteEndElement();
    }
}
