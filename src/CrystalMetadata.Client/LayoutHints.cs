using System;
using System.Collections.Generic;
using System.Linq;
using CrystalMetadata.Core.Models;

namespace CrystalMetadata.Client;

/// <summary>
/// Pure post-processing of extracted twips into a table-shaped view: for every Detail section, field objects sorted by
/// left edge and paired with the header (FieldHeading / Text) object whose horizontal span overlaps most, plus a band
/// summary. Written under <c>derived</c> in the JSON. Applied recursively to subreports.
/// </summary>
public static class LayoutHints
{
    public static void Apply(ReportMetadata report)
    {
        var derived = new DerivedLayout();

        // suppressed objects/sections never print: they are typically running-total helper formulas, not visible columns
        var headerCandidates = report.Sections
            .Where(s => !s.Format.IsAlwaysSuppressed())
            .Where(s => s.Kind.Equals("PageHeader", StringComparison.OrdinalIgnoreCase) || s.Kind.Equals("GroupHeader", StringComparison.OrdinalIgnoreCase) || s.Kind.Equals("ReportHeader", StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Objects.Where(o => o.Kind is ObjectKind.FieldHeading or ObjectKind.Text && !o.Format.IsAlwaysSuppressed()).Select(o => (Section: s, Object: o)))
            .ToList();

        foreach (var detail in report.Sections.Where(s => s.Kind.Equals("Detail", StringComparison.OrdinalIgnoreCase) && !s.Format.IsAlwaysSuppressed()))
        {
            foreach (var field in detail.Objects.Where(o => o.Kind == ObjectKind.Field && !o.Format.IsAlwaysSuppressed()).OrderBy(o => o.LeftTwips).ThenBy(o => o.TopTwips))
            {
                var column = new DerivedColumn
                {
                    FieldObjectName = field.Name,
                    FieldDataSource = field.Field?.DataSource,
                    DetailSectionName = detail.Name,
                    LeftTwips = field.LeftTwips,
                    WidthTwips = field.WidthTwips,
                    Alignment = field.Format.HorizontalAlignment,
                    NumberFormat = field.Field?.FieldFormat.Numeric,
                    Font = field.Font
                };

                // explicit FieldHeading link first
                var explicitHeader = headerCandidates.FirstOrDefault(h => h.Object.Kind == ObjectKind.FieldHeading && string.Equals(h.Object.FieldHeading?.ForFieldObjectName, field.Name, StringComparison.OrdinalIgnoreCase));
                var best = explicitHeader.Object != null ? explicitHeader : BestOverlap(headerCandidates, field);
                if (best.Object != null)
                {
                    column.Header = best.Object.Text?.Text ?? best.Object.FieldHeading?.Text;
                    column.HeaderObjectName = best.Object.Name;
                    column.HeaderSectionName = best.Section.Name;
                }

                derived.Columns.Add(column);
            }
        }

        foreach (var s in report.Sections)
        {
            derived.Bands.Add(new DerivedBand
            {
                SectionName = s.Name,
                Kind = s.Kind,
                GroupIndex = s.GroupIndex,
                HeightTwips = s.HeightTwips,
                ObjectCount = s.Objects.Count,
                IsEmpty = s.Objects.Count == 0,
                IsSuppressed = s.Format.IsAlwaysSuppressed()
            });
        }

        report.Derived = derived;

        foreach (var sub in report.Subreports)
        {
            if (sub.Report != null)
            {
                Apply(sub.Report);
            }
        }
    }

    private static (SectionMetadata Section, ObjectMetadata Object) BestOverlap(List<(SectionMetadata Section, ObjectMetadata Object)> candidates, ObjectMetadata field)
    {
        (SectionMetadata Section, ObjectMetadata Object) best = default;
        var bestOverlap = 0;
        foreach (var c in candidates)
        {
            var overlap = Math.Min(field.RightTwips, c.Object.RightTwips) - Math.Max(field.LeftTwips, c.Object.LeftTwips);
            if (overlap <= 0) continue;

            // a report title / banner spanning many columns is not a column header
            if (c.Object.WidthTwips > Math.Max(1, field.WidthTwips) * 4) continue;
            // prefer larger overlap; tie-break on closest left edge
            if (overlap > bestOverlap || (overlap == bestOverlap && best.Object != null && Math.Abs(c.Object.LeftTwips - field.LeftTwips) < Math.Abs(best.Object.LeftTwips - field.LeftTwips)))
            {
                bestOverlap = overlap;
                best = c;
            }
        }

        // require at least 30% overlap of the narrower object to call it a header
        if (best.Object != null)
        {
            var narrower = Math.Max(1, Math.Min(field.WidthTwips, best.Object.WidthTwips));
            if (bestOverlap * 100 / narrower < 30)
            {
                return default;
            }
        }

        return best;
    }
}
