using System;
using System.Collections.Generic;
using System.Drawing;
using CrystalDecisions.CrystalReports.Engine;
using CrystalMetadata.Core;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Extractor.Helpers;
using CrystalMetadata.Extractor.Ras;

namespace CrystalMetadata.Extractor.Walkers
{
    /// <summary>Areas, sections and report objects (Engine API), enriched by RAS where available.</summary>
    internal static class LayoutWalker
    {
        internal sealed class SectionPlacement
        {
            public string AreaName = string.Empty;
            public string Kind = string.Empty;
            public int? GroupIndex;
            public int IndexInArea;
        }

        private static readonly HashSet<string> ObjectExclude = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "Kind", "Left", "Top", "Width", "Height", "Border", "ObjectFormat", "Font", "Color", "FieldFormat", "DataSource",
            "Text", "SubreportName", "EnableOnDemand", "FieldObjectName", "LineColor", "LineStyle", "LineThickness", "FillColor",
            "CornerEllipseHeight", "CornerEllipseWidth", "EnableExtendToBottomOfSection", "EndSectionName", "ReportObjects", "Section"
        };

        public static List<AreaMetadata> ReadAreas(ReportDocument doc, int groupCount, RasEnricher? ras, ExtractionContext ctx, out Dictionary<string, SectionPlacement> placements)
        {
            var areas = new List<AreaMetadata>();
            placements = new Dictionary<string, SectionPlacement>(StringComparer.OrdinalIgnoreCase);
            var map = placements;

            var headerCount = 0;
            var footerSeen = 0;
            var totalGroupHeaders = 0;

            // first pass: count group header areas (used when DataDefinition.Groups is unavailable)
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.ReportDefinition.Areas)))
            {
                var a = item as Area;
                if (a != null && string.Equals(Text.Of(ctx.Quiet(() => a.Kind)), "GroupHeader", StringComparison.OrdinalIgnoreCase))
                {
                    totalGroupHeaders++;
                }
            }

            var n = groupCount > 0 ? groupCount : totalGroupHeaders;
            if (groupCount > 0 && totalGroupHeaders != groupCount)
            {
                ctx.Warn(DiagnosticCodes.GroupIndexMismatch, "areas", "DataDefinition has " + groupCount + " groups but " + totalGroupHeaders + " GroupHeader areas were found.");
            }

            var i = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.ReportDefinition.Areas)))
            {
                var path = "areas[" + i++ + "]";
                var area = item as Area;
                if (area == null) continue;

                var am = ctx.Try(path, DiagnosticCodes.AreaFailed, () =>
                {
                    var kind = Text.Of(area.Kind) ?? "Unknown";
                    var m = new AreaMetadata { Name = area.Name ?? string.Empty, Kind = kind };

                    if (kind.Equals("GroupHeader", StringComparison.OrdinalIgnoreCase))
                    {
                        m.GroupIndex = ++headerCount;
                    }
                    else if (kind.Equals("GroupFooter", StringComparison.OrdinalIgnoreCase))
                    {
                        m.GroupIndex = n - footerSeen;
                        footerSeen++;
                    }

                    ctx.Try(path + ".format", DiagnosticCodes.AreaFailed, () =>
                    {
                        var f = area.AreaFormat;
                        m.Format.Suppress = f.EnableSuppress;
                        m.Format.KeepTogether = f.EnableKeepTogether;
                        m.Format.NewPageBefore = f.EnableNewPageBefore;
                        m.Format.NewPageAfter = f.EnableNewPageAfter;
                        m.Format.PrintAtBottomOfPage = f.EnablePrintAtBottomOfPage;
                        m.Format.ResetPageNumberAfter = f.EnableResetPageNumberAfter;
                        m.Format.HideForDrillDown = f.EnableHideForDrillDown;
                    }, DiagnosticLevel.Info);

                    if (ras != null)
                    {
                        m.Format.ConditionFormulas = ras.GetAreaConditionFormulas(m.Name);
                    }

                    var idx = 0;
                    foreach (var s in Reflect.Items(ctx.QuietRef(() => (object)area.Sections)))
                    {
                        var sec = s as Section;
                        if (sec == null) continue;
                        var sname = sec.Name ?? string.Empty;
                        m.SectionNames.Add(sname);
                        map[sname] = new SectionPlacement { AreaName = m.Name, Kind = kind, GroupIndex = m.GroupIndex, IndexInArea = idx++ };
                    }

                    return m;
                });
                if (am != null) areas.Add(am);
            }

            return areas;
        }

        public static List<SectionMetadata> ReadSections(ReportDocument doc, Dictionary<string, SectionPlacement> placements, RasEnricher? ras, ExtractionContext ctx)
        {
            var sections = new List<SectionMetadata>();
            var i = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.ReportDefinition.Sections)))
            {
                var path = "sections[" + i++ + "]";
                var section = item as Section;
                if (section == null) continue;

                var sm = ctx.Try(path, DiagnosticCodes.SectionFailed, () => ReadSection(section, path, placements, ras, ctx));
                if (sm != null) sections.Add(sm);
            }

            return sections;
        }

        private static SectionMetadata ReadSection(Section section, string path, Dictionary<string, SectionPlacement> placements, RasEnricher? ras, ExtractionContext ctx)
        {
            var name = section.Name ?? string.Empty;
            var height = ctx.TryValue(path + ".height", DiagnosticCodes.SectionFailed, () => section.Height, 0);
            var sm = new SectionMetadata
            {
                Name = name,
                Kind = Text.Of(ctx.Quiet(() => section.Kind)) ?? "Unknown",
                HeightTwips = height,
                HeightPoints = Units.ToPoints(height)
            };

            if (placements.TryGetValue(name, out var placement))
            {
                sm.AreaName = placement.AreaName;
                sm.GroupIndex = placement.GroupIndex;
                sm.IndexInArea = placement.IndexInArea;
            }

            ctx.Try(path + ".format", DiagnosticCodes.SectionFailed, () =>
            {
                var f = section.SectionFormat;
                sm.Format.Suppress = f.EnableSuppress;
                sm.Format.SuppressIfBlank = f.EnableSuppressIfBlank;
                sm.Format.NewPageBefore = f.EnableNewPageBefore;
                sm.Format.NewPageAfter = f.EnableNewPageAfter;
                sm.Format.KeepTogether = f.EnableKeepTogether;
                sm.Format.PrintAtBottomOfPage = f.EnablePrintAtBottomOfPage;
                sm.Format.ResetPageNumberAfter = f.EnableResetPageNumberAfter;
                sm.Format.UnderlayFollowingSections = f.EnableUnderlaySection;
                sm.Format.BackgroundColor = Text.Color(f.BackgroundColor);
                sm.Format.CssClass = Text.NullIfEmpty(ctx.QuietRef(() => f.CssClass));
            }, DiagnosticLevel.Info);

            if (ras != null)
            {
                sm.Format.ConditionFormulas = ras.GetSectionConditionFormulas(name);
            }

            var z = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)section.ReportObjects)))
            {
                var opath = path + ".objects[" + z + "]";
                var ro = item as ReportObject;
                var zIndex = z++;
                if (ro == null) continue;

                var om = ctx.Try(opath, DiagnosticCodes.ObjectFailed, () => ReadObject(ro, name, zIndex, opath, ctx));
                if (om == null) continue;

                if (ras != null)
                {
                    ctx.Try(opath + ".ras", DiagnosticCodes.RasEnrichmentFailed, () => ras.EnrichObject(om), DiagnosticLevel.Info);
                }

                sm.Objects.Add(om);
            }

            return sm;
        }

        private static ObjectMetadata ReadObject(ReportObject ro, string sectionName, int zIndex, string path, ExtractionContext ctx)
        {
            var kindText = Text.Of(ctx.Quiet(() => ro.Kind)) ?? "Unknown";
            var om = new ObjectMetadata
            {
                Name = ro.Name ?? string.Empty,
                Kind = MapKind(kindText),
                SectionName = sectionName,
                ZIndex = zIndex,
                LeftTwips = ctx.TryValue(path + ".left", DiagnosticCodes.ObjectPropertyUnavailable, () => ro.Left, 0),
                TopTwips = ctx.TryValue(path + ".top", DiagnosticCodes.ObjectPropertyUnavailable, () => ro.Top, 0),
                WidthTwips = ctx.TryValue(path + ".width", DiagnosticCodes.ObjectPropertyUnavailable, () => ro.Width, 0),
                HeightTwips = ctx.TryValue(path + ".height", DiagnosticCodes.ObjectPropertyUnavailable, () => ro.Height, 0)
            };
            om.ComputeDerivedGeometry();

            if (om.Kind == ObjectKind.Unknown)
            {
                ctx.Warn(DiagnosticCodes.UnknownObjectKind, path, "Unknown report object kind '" + kindText + "' for object '" + om.Name + "'.");
                om.Raw["Kind"] = kindText;
            }

            ctx.Try(path + ".format", DiagnosticCodes.ObjectPropertyUnavailable, () =>
            {
                var f = ro.ObjectFormat;
                om.Format.Suppress = f.EnableSuppress;
                om.Format.CanGrow = f.EnableCanGrow;
                om.Format.KeepTogether = f.EnableKeepTogether;
                om.Format.CloseAtPageBreak = f.EnableCloseAtPageBreak;
                om.Format.HorizontalAlignment = Text.Of(f.HorizontalAlignment);
                om.Format.CssClass = Text.NullIfEmpty(ctx.QuietRef(() => f.CssClass));
                om.Format.HyperlinkType = Reflect.GetString(f, "HyperlinkType");
                om.Format.HyperlinkText = Text.NullIfEmpty(Reflect.GetString(f, "HyperlinkText"));
            }, DiagnosticLevel.Info);

            ctx.Try(path + ".border", DiagnosticCodes.ObjectPropertyUnavailable, () =>
            {
                var b = ro.Border;
                om.Border = new BorderMetadata
                {
                    Left = Text.Of(b.LeftLineStyle),
                    Right = Text.Of(b.RightLineStyle),
                    Top = Text.Of(b.TopLineStyle),
                    Bottom = Text.Of(b.BottomLineStyle),
                    BorderColor = Text.Color(b.BorderColor),
                    BackgroundColor = Text.Color(b.BackgroundColor),
                    HasDropShadow = b.HasDropShadow
                };
            }, DiagnosticLevel.Info);

            switch (ro)
            {
                case FieldHeadingObject fh:
                    om.Font = Text.Font(ctx.QuietRef(() => fh.Font), ctx.Quiet(() => fh.Color) ?? Color.Empty);
                    om.FieldHeading = new FieldHeadingMetadata
                    {
                        ForFieldObjectName = ctx.QuietRef(() => fh.FieldObjectName),
                        Text = ctx.QuietRef(() => fh.Text)
                    };
                    om.Text = new TextObjectMetadata { Text = om.FieldHeading.Text };
                    break;

                case FieldObject fo:
                    om.Font = Text.Font(ctx.QuietRef(() => fo.Font), ctx.Quiet(() => fo.Color) ?? Color.Empty);
                    om.Field = ReadFieldObject(fo, path, ctx);
                    break;

                case TextObject to:
                    om.Font = Text.Font(ctx.QuietRef(() => to.Font), ctx.Quiet(() => to.Color) ?? Color.Empty);
                    om.Text = new TextObjectMetadata { Text = ctx.QuietRef(() => to.Text) };
                    break;

                case LineObject lo:
                    om.Line = new LineObjectMetadata
                    {
                        X1Twips = om.LeftTwips,
                        Y1Twips = om.TopTwips,
                        X2Twips = om.RightTwips,
                        Y2Twips = om.BottomTwips,
                        Orientation = om.WidthTwips >= om.HeightTwips ? "Horizontal" : "Vertical",
                        LineStyle = Text.Of(ctx.Quiet(() => lo.LineStyle)),
                        ThicknessTwips = ctx.Quiet(() => lo.LineThickness),
                        Color = Text.Color(ctx.Quiet(() => lo.LineColor) ?? Color.Empty),
                        ExtendToBottomOfSection = ctx.Quiet(() => lo.EnableExtendToBottomOfSection),
                        EndSectionName = Text.NullIfEmpty(Reflect.GetString(lo, "EndSectionName"))
                    };
                    break;

                case BoxObject bo:
                    om.Box = new BoxObjectMetadata
                    {
                        LineStyle = Text.Of(ctx.Quiet(() => bo.LineStyle)),
                        ThicknessTwips = ctx.Quiet(() => bo.LineThickness),
                        LineColor = Text.Color(ctx.Quiet(() => bo.LineColor) ?? Color.Empty),
                        FillColor = Text.Color(ctx.Quiet(() => bo.FillColor) ?? Color.Empty),
                        CornerEllipseWidthTwips = Reflect.GetInt(bo, "CornerEllipseWidth"),
                        CornerEllipseHeightTwips = Reflect.GetInt(bo, "CornerEllipseHeight"),
                        ExtendToBottomOfSection = ctx.Quiet(() => bo.EnableExtendToBottomOfSection),
                        EndSectionName = Text.NullIfEmpty(Reflect.GetString(bo, "EndSectionName"))
                    };
                    break;

                case PictureObject _:
                    om.Picture = new PictureObjectMetadata();
                    break;

                case BlobFieldObject blob:
                    om.Blob = new BlobObjectMetadata { DataSource = ctx.QuietRef(() => blob.DataSource?.FormulaName) };
                    break;

                case SubreportObject so:
                    om.Subreport = new SubreportObjectMetadata
                    {
                        SubreportName = ctx.QuietRef(() => so.SubreportName) ?? string.Empty,
                        IsOnDemand = ctx.Quiet(() => so.EnableOnDemand)
                    };
                    break;

                case ChartObject _:
                    om.Chart = new ChartObjectMetadata();
                    ctx.Info(DiagnosticCodes.ManualReviewRequired, path, "Chart object '" + om.Name + "' requires manual review.");
                    break;

                case CrossTabObject _:
                    om.CrossTab = new CrossTabObjectMetadata();
                    ctx.Info(DiagnosticCodes.ManualReviewRequired, path, "Cross-tab object '" + om.Name + "' requires manual review.");
                    break;
            }

            if (ctx.Options.IncludeRawBags)
            {
                foreach (var kv in Reflect.ScalarBag(ro, ObjectExclude))
                {
                    om.Raw[kv.Key] = kv.Value;
                }
            }

            return om;
        }

        private static FieldObjectMetadata ReadFieldObject(FieldObject fo, string path, ExtractionContext ctx)
        {
            var fm = new FieldObjectMetadata();
            ctx.Try(path + ".dataSource", DiagnosticCodes.ObjectPropertyUnavailable, () =>
            {
                var ds = fo.DataSource;
                if (ds == null) return;
                fm.DataSource = ds.FormulaName;
                fm.FieldKind = Text.Of(ds.Kind);
                fm.FieldName = ds.Name;
                fm.ValueType = Text.Of(ds.ValueType);
                if (ds is DatabaseFieldDefinition dbf)
                {
                    fm.TableAlias = ctx.QuietRef(() => dbf.TableName);
                }
            }, DiagnosticLevel.Info);

            ctx.Try(path + ".fieldFormat", DiagnosticCodes.ObjectPropertyUnavailable, () =>
            {
                var ff = fo.FieldFormat;
                fm.FieldFormat.SuppressIfDuplicated = ctx.Quiet(() => ff.CommonFormat.EnableSuppressIfDuplicated);

                var valueType = fm.ValueType ?? string.Empty;
                if (valueType.IndexOf("Number", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    valueType.IndexOf("Currency", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    valueType.IndexOf("Int", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    valueType.IndexOf("Decimal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fm.FieldKind == "SummaryField" || fm.FieldKind == "RunningTotalField")
                {
                    var nf = ctx.QuietRef(() => (object)ff.NumericFormat);
                    if (nf != null)
                    {
                        fm.FieldFormat.Numeric = new NumericFormatMetadata
                        {
                            DecimalPlaces = Reflect.GetInt(nf, "DecimalPlaces"),
                            ThousandsSeparator = Reflect.GetBool(nf, "EnableThousandsSeparator") ?? Reflect.GetBool(nf, "ThousandsSeparator"),
                            ThousandsSeparatorSymbol = Reflect.GetString(nf, "ThousandSymbol") ?? Reflect.GetString(nf, "ThousandsSeparatorSymbol"),
                            DecimalSymbol = Reflect.GetString(nf, "DecimalSymbol"),
                            CurrencySymbol = Text.NullIfEmpty(Reflect.GetString(nf, "CurrencySymbol")),
                            CurrencySymbolFormat = Reflect.GetString(nf, "CurrencySymbolFormat"),
                            CurrencyPositionFormat = Reflect.GetString(nf, "CurrencyPositionFormat"),
                            NegativeFormat = Reflect.GetString(nf, "NegativeFormat"),
                            UseLeadingZero = Reflect.GetBool(nf, "EnableUseLeadingZero"),
                            RoundingFormat = Reflect.GetString(nf, "RoundingFormat"),
                            Raw = ctx.Options.IncludeRawBags ? Reflect.ScalarBag(nf, null) : new Dictionary<string, object?>()
                        };
                    }
                }

                if (valueType.IndexOf("Date", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var df = ctx.QuietRef(() => (object)ff.DateFormat);
                    if (df != null)
                    {
                        fm.FieldFormat.Date = new DateFormatMetadata
                        {
                            DayFormat = Reflect.GetString(df, "DayFormat"),
                            MonthFormat = Reflect.GetString(df, "MonthFormat"),
                            YearFormat = Reflect.GetString(df, "YearFormat"),
                            DateOrder = Reflect.GetString(df, "DateOrder"),
                            Separator = Reflect.GetString(df, "DateSeparator") ?? Reflect.GetString(df, "Separator"),
                            Raw = ctx.Options.IncludeRawBags ? Reflect.ScalarBag(df, null) : new Dictionary<string, object?>()
                        };
                    }

                    if (valueType.IndexOf("DateTime", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var dtf = ctx.QuietRef(() => (object)ff.DateTimeFormat);
                        if (dtf != null)
                        {
                            fm.FieldFormat.DateTime = new DateTimeFormatMetadata
                            {
                                Order = Reflect.GetString(dtf, "DateTimeOrder"),
                                Raw = ctx.Options.IncludeRawBags ? Reflect.ScalarBag(dtf, null) : new Dictionary<string, object?>()
                            };
                        }
                    }
                }

                if (valueType.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0 && valueType.IndexOf("DateTime", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    var tf = ctx.QuietRef(() => (object)ff.TimeFormat);
                    if (tf != null)
                    {
                        fm.FieldFormat.Time = Reflect.ScalarBag(tf, null);
                    }
                }

                if (valueType.IndexOf("Boolean", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var bf = ctx.QuietRef(() => (object)ff.BooleanFormat);
                    if (bf != null)
                    {
                        fm.FieldFormat.Boolean = new BooleanFormatMetadata { OutputType = Reflect.GetString(bf, "OutputType") };
                    }
                }
            }, DiagnosticLevel.Info);

            return fm;
        }

        internal static ObjectKind MapKind(string kind)
        {
            switch (kind)
            {
                case "FieldObject": return ObjectKind.Field;
                case "FieldHeadingObject": return ObjectKind.FieldHeading;
                case "TextObject": return ObjectKind.Text;
                case "LineObject": return ObjectKind.Line;
                case "BoxObject": return ObjectKind.Box;
                case "PictureObject": return ObjectKind.Picture;
                case "BlobFieldObject": return ObjectKind.Blob;
                case "SubreportObject": return ObjectKind.Subreport;
                case "ChartObject": return ObjectKind.Chart;
                case "CrossTabObject": return ObjectKind.CrossTab;
                case "MapObject": return ObjectKind.Map;
                case "OlapGridObject": return ObjectKind.OlapGrid;
                default: return ObjectKind.Unknown;
            }
        }
    }
}
