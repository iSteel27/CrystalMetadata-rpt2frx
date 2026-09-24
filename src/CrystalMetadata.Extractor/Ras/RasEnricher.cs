using System;
using System.Collections.Generic;
using System.Text;
using CrystalDecisions.ReportAppServer.ClientDoc;
using CrystalDecisions.ReportAppServer.Controllers;
using CrystalDecisions.ReportAppServer.DataDefModel;
using CrystalDecisions.ReportAppServer.ReportDefModel;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Extractor.Helpers;
using ReportDocument = CrystalDecisions.CrystalReports.Engine.ReportDocument;

namespace CrystalMetadata.Extractor.Ras
{
    /// <summary>
    /// Enrichment through the in-process Report Application Server object model (<c>ReportDocument.ReportClientDocument</c>).
    /// RAS exposes what the Engine API hides: conditional-format formulas, text paragraphs with embedded fields, command SQL,
    /// stored-procedure tables, connection attribute bags, formula syntax, custom functions, subreport links and the file version.
    ///
    /// The main document is an <see cref="ISCDReportClientDocument"/>; a subreport document is an
    /// <see cref="ISCRSubreportClientDocument"/> (same controllers, no Subreport/CustomFunction/PrintOutput controllers).
    /// Both are handled through one <c>object</c> with interface-aware reflection. Every call is defensive: the RAS objects
    /// are COM RCWs whose getters throw for unsupported combinations.
    /// </summary>
    internal sealed class RasEnricher
    {
        private static readonly Type[] DocInterfaces = { typeof(ISCDReportClientDocument), typeof(ISCRSubreportClientDocument) };

        private readonly object _doc;
        private readonly ISCDReportClientDocument? _main;
        private readonly ExtractionContext _ctx;
        private ISCRReportDefinition? _reportDefinition;

        private RasEnricher(object doc, ExtractionContext ctx)
        {
            _doc = doc;
            _main = doc as ISCDReportClientDocument;
            _ctx = ctx;
        }

        /// <summary>Obtains the in-process RAS document for an Engine document; null (with diagnostic) when unavailable.</summary>
        public static RasEnricher? TryCreate(ReportDocument doc, ExtractionContext ctx)
        {
            try
            {
                var rcd = doc.ReportClientDocument;
                return rcd == null ? null : new RasEnricher(rcd, ctx);
            }
            catch (Exception ex)
            {
                ctx.Diagnostics.Add(Diagnostic.FromException(DiagnosticLevel.Warning, DiagnosticCodes.RasUnavailable, ctx.Scope, "$", ex));
                return null;
            }
        }

        /// <summary>RAS view of a subreport (only available from the main document).</summary>
        public RasEnricher? ForSubreport(string subreportName, ExtractionContext subCtx)
        {
            if (_main == null)
            {
                return null;
            }

            try
            {
                object? sub = _main.SubreportController.GetSubreport(subreportName);
                if (sub == null)
                {
                    subCtx.Warn(DiagnosticCodes.RasUnavailable, "$", "RAS returned no document for subreport '" + subreportName + "'.");
                    return null;
                }

                return new RasEnricher(sub, subCtx);
            }
            catch (Exception ex)
            {
                subCtx.Diagnostics.Add(Diagnostic.FromException(DiagnosticLevel.Warning, DiagnosticCodes.RasUnavailable, subCtx.Scope, "$", ex));
                return null;
            }
        }

        private object? Controller(string name) => Reflect.Get(_doc, name, DocInterfaces);

        private object? DataDefinition => Reflect.Get(Controller("DataDefController"), "DataDefinition", typeof(ISCRDataDefController));

        private object? Database => Reflect.Get(Controller("DatabaseController"), "Database", typeof(ISCRDatabaseController));

        private ISCRReportDefinition? ReportDefinition
        {
            get
            {
                if (_reportDefinition == null)
                {
                    _reportDefinition = Reflect.Get(Controller("ReportDefController"), "ReportDefinition", typeof(ISCRReportDefController)) as ISCRReportDefinition;
                }

                return _reportDefinition;
            }
        }

        public string? GetFileVersion()
        {
            if (_main == null) return null;
            return _ctx.QuietRef(() => _main.MajorVersion + "." + _main.MinorVersion);
        }

        public void EnrichPageSetup(PageSetup ps)
        {
            if (_main == null) return;
            _ctx.Try("pageSetup.ras", DiagnosticCodes.RasEnrichmentFailed, () =>
            {
                var controller = (object?)_main.PrintOutputController;
                var po = InvokeNoArg(controller, "GetPrintOptions");
                if (po == null) return;

                ps.PrintableWidthTwips = ps.PrintableWidthTwips ?? Reflect.GetInt(po, "PageContentWidth");
                ps.PrintableHeightTwips = ps.PrintableHeightTwips ?? Reflect.GetInt(po, "PageContentHeight");
                ps.PaperSize = ps.PaperSize ?? Reflect.GetString(po, "PaperSize");
                ps.PaperOrientation = ps.PaperOrientation ?? Reflect.GetString(po, "PaperOrientation");
                var pm = Reflect.Get(po, "PageMargins");
                if (pm != null)
                {
                    ps.Margins.LeftTwips = ps.Margins.LeftTwips ?? Reflect.GetInt(pm, "Left");
                    ps.Margins.RightTwips = ps.Margins.RightTwips ?? Reflect.GetInt(pm, "Right");
                    ps.Margins.TopTwips = ps.Margins.TopTwips ?? Reflect.GetInt(pm, "Top");
                    ps.Margins.BottomTwips = ps.Margins.BottomTwips ?? Reflect.GetInt(pm, "Bottom");
                }
            }, DiagnosticLevel.Info);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Data source
        // ---------------------------------------------------------------------------------------------------------

        public void EnrichDataSource(DataSourceMetadata ds)
        {
            _ctx.Try("dataSource.ras", DiagnosticCodes.RasEnrichmentFailed, () =>
            {
                var tables = Reflect.Get(Database, "Tables", typeof(ISCRDatabase));
                foreach (var item in Reflect.Items(tables))
                {
                    var alias = Reflect.GetString(item, "Alias", typeof(ISCRTable)) ?? Reflect.GetString(item, "Name", typeof(ISCRTable));
                    if (alias == null) continue;

                    var tm = FindTable(ds, alias);
                    if (tm == null) continue;

                    tm.QualifiedName = Reflect.GetString(item, "QualifiedName", typeof(ISCRTable)) ?? tm.QualifiedName;
                    tm.Name = tm.Name ?? Reflect.GetString(item, "Name", typeof(ISCRTable));

                    var commandText = Reflect.GetString(item, "CommandText", typeof(ISCRCommandTable));
                    if (!string.IsNullOrEmpty(commandText))
                    {
                        tm.Kind = TableKind.Command;
                        tm.CommandText = commandText;
                        ReadProcedureParameters(Reflect.Get(item, "Parameters", typeof(ISCRCommandTable)), tm);
                    }
                    else
                    {
                        var parameters = Reflect.Get(item, "Parameters", typeof(ISCRProcedure));
                        var className = Reflect.GetString(item, "ClassName", typeof(ISCRTable));
                        if (parameters != null || (className != null && className.IndexOf("Procedure", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            tm.Kind = TableKind.StoredProcedure;
                            ReadProcedureParameters(parameters, tm);
                        }
                        else if (tm.Kind == TableKind.Unknown)
                        {
                            tm.Kind = TableKind.Table;
                        }

                        if (className != null) tm.Raw["RasClassName"] = className;
                    }

                    // connection attribute bag (QE_*) → connection metadata
                    var ci = Reflect.Get(item, "ConnectionInfo", typeof(ISCRTable));
                    var attrs = Reflect.Get(ci, "Attributes", typeof(ISCRConnectionInfo));
                    var conn = FindConnection(ds, tm.ConnectionId);
                    if (conn != null && attrs != null)
                    {
                        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
                        ReadPropertyBag(attrs, bag, 0);
                        foreach (var kv in bag)
                        {
                            if (!conn.Attributes.ContainsKey(kv.Key))
                            {
                                conn.Attributes[kv.Key] = kv.Value;
                            }
                        }

                        conn.DatabaseDll = conn.DatabaseDll ?? AsString(bag, "Database DLL");
                        conn.ServerName = conn.ServerName ?? AsString(bag, "QE_ServerDescription");
                        conn.DatabaseName = conn.DatabaseName ?? AsString(bag, "QE_DatabaseName");
                        if (bag.TryGetValue("QE_LogonProperties", out var lp) && lp is Dictionary<string, object?> lpd)
                        {
                            foreach (var kv in lpd)
                            {
                                if (!conn.LogonProperties.ContainsKey(kv.Key))
                                {
                                    conn.LogonProperties[kv.Key] = Reflect.ToText(kv.Value);
                                }
                            }

                            conn.Provider = conn.Provider ?? AsString(lpd, "Provider");
                        }
                    }

                    // field-level RAS extras (declared length/type/heading)
                    var fields = Reflect.Get(item, "DataFields", typeof(ISCRTable));
                    foreach (var f in Reflect.Items(fields))
                    {
                        var fname = Reflect.GetString(f, "Name", typeof(ISCRField));
                        if (fname == null) continue;
                        var fm = tm.Fields.Find(x => string.Equals(x.Name, fname, StringComparison.OrdinalIgnoreCase));
                        if (fm == null) continue;
                        fm.HeadingText = fm.HeadingText ?? Text.NullIfEmpty(Reflect.GetString(f, "HeadingText", typeof(ISCRField)));
                        if (_ctx.Options.IncludeRawBags)
                        {
                            fm.Raw["RasType"] = Reflect.GetString(f, "Type", typeof(ISCRField));
                            fm.Raw["RasLength"] = Reflect.GetInt(f, "Length", typeof(ISCRField));
                        }
                    }
                }

                // richer join info
                var links = Reflect.Get(Database, "TableLinks", typeof(ISCRDatabase));
                var i = 0;
                foreach (var link in Reflect.Items(links))
                {
                    if (i < ds.Links.Count)
                    {
                        var lm = ds.Links[i];
                        lm.LinkOperator = lm.LinkOperator ?? Reflect.GetString(link, "LinkOperator", typeof(ISCRTableLink));
                        lm.JoinType = lm.JoinType ?? Reflect.GetString(link, "JoinType", typeof(ISCRTableLink));
                        lm.SourceTable = lm.SourceTable ?? Reflect.GetString(link, "SourceTableAlias", typeof(ISCRTableLink));
                        lm.DestinationTable = lm.DestinationTable ?? Reflect.GetString(link, "TargetTableAlias", typeof(ISCRTableLink));
                    }

                    i++;
                }
            }, DiagnosticLevel.Info);
        }

        private static void ReadProcedureParameters(object? parameters, TableMetadata tm)
        {
            foreach (var p in Reflect.Items(parameters))
            {
                tm.CommandParameters.Add(new CommandParameterMetadata
                {
                    Name = Reflect.GetString(p, "Name", typeof(ISCRField)) ?? Reflect.GetString(p, "Name") ?? string.Empty,
                    ValueType = Reflect.GetString(p, "Type", typeof(ISCRField)) ?? Reflect.GetString(p, "ValueType"),
                    DefaultValue = Reflect.ToText(Reflect.Get(p, "DefaultValue"))
                });
            }
        }

        private static TableMetadata? FindTable(DataSourceMetadata ds, string alias)
        {
            return ds.Tables.Find(t => string.Equals(t.Alias, alias, StringComparison.OrdinalIgnoreCase))
                   ?? ds.Tables.Find(t => string.Equals(t.Name, alias, StringComparison.OrdinalIgnoreCase));
        }

        private static ConnectionMetadata? FindConnection(DataSourceMetadata ds, string? id)
        {
            return id == null ? null : ds.Connections.Find(c => c.Id == id);
        }

        private static string? AsString(Dictionary<string, object?> bag, string key)
        {
            return bag.TryGetValue(key, out var v) ? Reflect.ToText(v) : null;
        }

        /// <summary>Walks a RAS PropertyBag (PropertyIDs + indexer) recursively; nested bags become nested dictionaries.</summary>
        private static void ReadPropertyBag(object? bag, Dictionary<string, object?> target, int depth)
        {
            if (bag == null || depth > 3) return;

            var ids = Reflect.Get(bag, "PropertyIDs", typeof(ISCRPropertyBag));
            var getItem = FindMethod(bag, "get_Item", typeof(ISCRPropertyBag), typeof(string));

            foreach (var idObj in Reflect.Items(ids))
            {
                var id = Reflect.ToText(idObj);
                if (string.IsNullOrEmpty(id) || Walkers.DataSourceWalker.IsSecret(id!)) continue;

                object? value;
                try
                {
                    value = getItem?.Invoke(bag, new object[] { id! });
                }
                catch
                {
                    continue;
                }

                if (value == null || Reflect.IsScalar(value))
                {
                    target[id!] = Reflect.ToJsonScalar(value);
                }
                else
                {
                    var nested = new Dictionary<string, object?>(StringComparer.Ordinal);
                    ReadPropertyBag(value, nested, depth + 1);
                    target[id!] = nested.Count > 0 ? nested : (object?)Reflect.ToText(value);
                }
            }
        }

        public string? GetGeneratedSql()
        {
            string? sql = null;
            _ctx.Try("dataSource.generatedSql", DiagnosticCodes.GeneratedSqlFailed, () =>
            {
                var rc = Controller("RowsetController");
                var mi = FindMethod(rc, "GetSQLStatement", typeof(ISCRRowsetController));
                if (rc == null || mi == null) return;
                var args = new object?[mi.GetParameters().Length];
                args[0] = new GroupPath();
                var result = mi.Invoke(rc, args);
                sql = Reflect.ToText(result);
            }, DiagnosticLevel.Info);
            return sql;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Data definition
        // ---------------------------------------------------------------------------------------------------------

        public void EnrichFormulas(List<FormulaMetadata> formulas)
        {
            _ctx.Try("formulas.ras", DiagnosticCodes.RasEnrichmentFailed, () =>
            {
                foreach (var item in Reflect.Items(Reflect.Get(DataDefinition, "FormulaFields", typeof(ISCRDataDefinition))))
                {
                    var name = Reflect.GetString(item, "Name", typeof(ISCRFormulaField));
                    if (name == null) continue;
                    var fm = formulas.Find(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (fm == null) continue;
                    fm.Syntax = NormalizeSyntax(Reflect.GetString(item, "Syntax", typeof(ISCRFormulaField)));
                }
            }, DiagnosticLevel.Info);
        }

        private static string? NormalizeSyntax(string? syntax)
        {
            if (syntax == null) return null;
            if (syntax.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0 || syntax == "1") return "Basic";
            if (syntax.IndexOf("Crystal", StringComparison.OrdinalIgnoreCase) >= 0 || syntax == "0") return "Crystal";
            return syntax;
        }

        public List<CustomFunctionMetadata> GetCustomFunctions()
        {
            var list = new List<CustomFunctionMetadata>();
            if (_main == null) return list;
            _ctx.Try("customFunctions", DiagnosticCodes.CustomFunctionsFailed, () =>
            {
                var controller = _ctx.QuietRef(() => (object?)_main.CustomFunctionController);
                var functions = InvokeNoArg(controller, "GetCustomFunctions");
                foreach (var f in Reflect.Items(functions))
                {
                    list.Add(new CustomFunctionMetadata
                    {
                        Name = Reflect.GetString(f, "Name", typeof(ISCRCustomFunction)) ?? string.Empty,
                        Text = Reflect.GetString(f, "Text", typeof(ISCRCustomFunction)) ?? string.Empty,
                        Syntax = NormalizeSyntax(Reflect.GetString(f, "Syntax", typeof(ISCRCustomFunction)))
                    });
                }
            }, DiagnosticLevel.Info);
            return list;
        }

        public void EnrichParameters(List<ParameterMetadata> parameters)
        {
            _ctx.Try("parameters.ras", DiagnosticCodes.RasEnrichmentFailed, () =>
            {
                foreach (var item in Reflect.Items(Reflect.Get(DataDefinition, "ParameterFields", typeof(ISCRDataDefinition))))
                {
                    var name = Reflect.GetString(item, "Name", typeof(ISCRParameterField));
                    if (name == null) continue;
                    var pm = parameters.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (pm == null) continue;
                    pm.ValueListKind = Reflect.GetString(item, "ValueListKind", typeof(ISCRParameterField));
                    pm.PromptText = Text.NullIfEmpty(pm.PromptText) ?? Reflect.GetString(item, "Description", typeof(ISCRParameterField));
                    if (_ctx.Options.IncludeRawBags)
                    {
                        foreach (var kv in Reflect.ScalarBag(item, null, typeof(ISCRParameterField)))
                        {
                            if (!pm.Raw.ContainsKey(kv.Key)) pm.Raw["Ras" + kv.Key] = kv.Value;
                        }
                    }
                }
            }, DiagnosticLevel.Info);
        }

        public void EnrichGroups(List<GroupMetadata> groups)
        {
            _ctx.Try("groups.ras", DiagnosticCodes.RasEnrichmentFailed, () =>
            {
                var i = 0;
                foreach (var item in Reflect.Items(Reflect.Get(DataDefinition, "Groups", typeof(ISCRDataDefinition))))
                {
                    if (i >= groups.Count) break;
                    var gm = groups[i++];
                    var options = Reflect.Get(item, "Options", typeof(ISCRGroup));
                    if (options == null) continue;

                    gm.KeepGroupTogether = Reflect.GetBool(options, "KeepGroupTogether", typeof(ISCRGroupOptions));
                    gm.RepeatGroupHeader = Reflect.GetBool(options, "RepeatGroupHeader", typeof(ISCRGroupOptions));
                    gm.Condition = gm.Condition ?? Reflect.GetString(options, "Condition", typeof(ISCRGroupOptions));
                    gm.SortDirection = gm.SortDirection ?? Reflect.GetString(options, "SortDirection", typeof(ISCRGroupOptions));

                    // group-name / sort-direction formulas live in the keyed GroupOptions.ConditionFormulas collection
                    var groupFormulas = new List<ConditionFormulaMetadata>();
                    ReadConditionFormulas(Reflect.Get(options, "ConditionFormulas", typeof(ISCRGroupOptions)), groupFormulas, null);
                    foreach (var f in groupFormulas)
                    {
                        if (f.Type.StartsWith("GroupName", StringComparison.OrdinalIgnoreCase)) gm.CustomGroupNameFormula = f.Text;
                        else gm.Raw["ConditionFormula." + f.Type] = f.Text;
                    }

                    var topN = Reflect.Get(options, "TopNSortGroupOptions", typeof(ISCRGroupOptions));
                    if (topN != null)
                    {
                        var n = Reflect.GetInt(topN, "NumberOfTopOrBottomNGroups") ?? Reflect.GetInt(topN, "N");
                        if (n.HasValue && n.Value > 0) gm.TopN = n.Value.ToString();
                    }

                    foreach (var so in Reflect.Items(Reflect.Get(options, "SpecifiedOrder", typeof(ISCRGroupOptions)) ?? Reflect.Get(options, "SpecifiedGroups", typeof(ISCRGroupOptions))))
                    {
                        var sn = Reflect.GetString(so, "Name") ?? Reflect.ToText(so);
                        if (sn != null) gm.SpecifiedOrderNames.Add(sn);
                    }

                    if (_ctx.Options.IncludeRawBags)
                    {
                        foreach (var kv in Reflect.ScalarBag(options, null, typeof(ISCRGroupOptions)))
                        {
                            gm.Raw["Ras" + kv.Key] = kv.Value;
                        }
                    }
                }
            }, DiagnosticLevel.Info);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Layout
        // ---------------------------------------------------------------------------------------------------------

        public List<ConditionFormulaMetadata> GetAreaConditionFormulas(string areaName)
        {
            var list = new List<ConditionFormulaMetadata>();
            _ctx.Try("areas." + areaName + ".conditionFormulas", DiagnosticCodes.RasEnrichmentFailed, () =>
            {
                var def = ReportDefinition;
                if (def == null) return;
                foreach (var area in Reflect.Items(def.Areas))
                {
                    if (!string.Equals(Reflect.GetString(area, "Name", typeof(ISCRArea)), areaName, StringComparison.OrdinalIgnoreCase)) continue;
                    var format = Reflect.Get(area, "Format", typeof(ISCRArea));
                    ReadConditionFormulas(Reflect.Get(format, "ConditionFormulas", typeof(ISCRAreaFormat)), list, null);
                    break;
                }
            }, DiagnosticLevel.Info);
            return list;
        }

        public List<ConditionFormulaMetadata> GetSectionConditionFormulas(string sectionName)
        {
            var list = new List<ConditionFormulaMetadata>();
            _ctx.Try("sections." + sectionName + ".conditionFormulas", DiagnosticCodes.RasEnrichmentFailed, () =>
            {
                var def = ReportDefinition;
                if (def == null) return;
                var section = def.FindSectionByName(sectionName);
                if (section == null) return;
                var format = Reflect.Get(section, "Format", typeof(ISCRSection));
                ReadConditionFormulas(Reflect.Get(format, "ConditionFormulas", typeof(ISCRSectionFormat)), list, null);
            }, DiagnosticLevel.Info);
            return list;
        }

        public void EnrichObject(ObjectMetadata om)
        {
            var def = ReportDefinition;
            if (def == null) return;

            object? ro = _ctx.QuietRef(() => (object?)def.FindObjectByName(om.Name));
            if (ro == null) return;

            // format / border / font conditional formulas
            var format = Reflect.Get(ro, "Format", typeof(ISCRReportObject));
            ReadConditionFormulas(Reflect.Get(format, "ConditionFormulas", typeof(ISCRObjectFormat)), om.Format.ConditionFormulas, null);

            var border = Reflect.Get(ro, "Border", typeof(ISCRReportObject));
            if (border != null)
            {
                om.Border = om.Border ?? new BorderMetadata();
                ReadConditionFormulas(Reflect.Get(border, "ConditionFormulas", typeof(ISCRBorder)), om.Border.ConditionFormulas, null);

                // The Engine API reports "no background" as white. RAS uses the sentinel 0xFFFFFFFF for it and a COLORREF
                // (0x00BBGGRR) for a real colour, so it can tell a transparent object from a white one.
                var background = Reflect.Get(border, "BackgroundColor", typeof(ISCRBorder));
                if (background != null && Reflect.IsScalar(background))
                {
                    try
                    {
                        var value = Convert.ToInt64(background, System.Globalization.CultureInfo.InvariantCulture);
                        om.Border.BackgroundColor = value < 0 || value > 0xFFFFFF ? null : MetadataJson.FormatColorRef((int)value);
                    }
                    catch (Exception)
                    {
                        // keep the Engine value
                    }
                }
            }

            // FontColor lives on the field/text/field-heading interfaces, not on ISCRReportObject
            var fontColor = Reflect.Get(ro, "FontColor", typeof(ISCRFieldObject), typeof(ISCRTextObject), typeof(ISCRFieldHeadingObject));
            if (fontColor != null)
            {
                om.Font = om.Font ?? ReadFont(fontColor);
                ReadConditionFormulas(Reflect.Get(fontColor, "ConditionFormulas", typeof(ISCRFontColor)), om.Font!.ConditionFormulas, null);
            }

            switch (om.Kind)
            {
                case ObjectKind.Field:
                    EnrichFieldObject(ro, om);
                    break;
                case ObjectKind.Text:
                case ObjectKind.FieldHeading:
                    EnrichTextObject(ro, om);
                    break;
                case ObjectKind.Picture:
                    EnrichPicture(ro, om);
                    break;
                case ObjectKind.CrossTab:
                    EnrichCrossTab(ro, om);
                    break;
                case ObjectKind.Chart:
                    EnrichChart(ro, om);
                    break;
                case ObjectKind.Line:
                    if (om.Line != null)
                    {
                        om.Line.EndSectionName = om.Line.EndSectionName ?? Text.NullIfEmpty(Reflect.GetString(ro, "EndSectionName", typeof(ISCRLineObject)));
                        var right = Reflect.GetInt(ro, "Right", typeof(ISCRLineObject));
                        var bottom = Reflect.GetInt(ro, "Bottom", typeof(ISCRLineObject));
                        if (right.HasValue) om.Line.X2Twips = right.Value;
                        if (bottom.HasValue) om.Line.Y2Twips = bottom.Value;
                    }
                    break;
                case ObjectKind.Box:
                    if (om.Box != null)
                    {
                        om.Box.EndSectionName = om.Box.EndSectionName ?? Text.NullIfEmpty(Reflect.GetString(ro, "EndSectionName", typeof(ISCRBoxObject)));
                        // corner radius is only exposed by RAS (the Engine BoxObject has no such property)
                        om.Box.CornerEllipseWidthTwips = Reflect.GetInt(ro, "CornerEllipseWidth", typeof(ISCRBoxObject)) ?? om.Box.CornerEllipseWidthTwips;
                        om.Box.CornerEllipseHeightTwips = Reflect.GetInt(ro, "CornerEllipseHeight", typeof(ISCRBoxObject)) ?? om.Box.CornerEllipseHeightTwips;
                    }
                    break;
            }

            if (_ctx.Options.IncludeRawBags)
            {
                foreach (var kv in Reflect.ScalarBag(ro, new HashSet<string> { "Name", "Kind", "Left", "Top", "Width", "Height", "SectionName" }, typeof(ISCRReportObject)))
                {
                    if (!om.Raw.ContainsKey(kv.Key)) om.Raw["Ras" + kv.Key] = kv.Value;
                }
            }
        }

        private void EnrichFieldObject(object ro, ObjectMetadata om)
        {
            om.Field = om.Field ?? new FieldObjectMetadata();
            om.Field.DataSource = om.Field.DataSource ?? DataSourceText(Reflect.Get(ro, "DataSource", typeof(ISCRFieldObject)));

            var ff = Reflect.Get(ro, "FieldFormat", typeof(ISCRFieldObject));
            if (ff == null) return;

            foreach (var part in new[] { "CommonFormat", "NumericFormat", "DateFormat", "TimeFormat", "DateTimeFormat", "BooleanFormat" })
            {
                var sub = Reflect.Get(ff, part, typeof(ISCRFieldFormat));
                if (sub == null) continue;
                ReadConditionFormulas(Reflect.Get(sub, "ConditionFormulas", typeof(ISCRCommonFieldFormat), typeof(ISCRNumericFieldFormat), typeof(ISCRDateFieldFormat), typeof(ISCRTimeFieldFormat), typeof(ISCRDateTimeFieldFormat), typeof(ISCRBooleanFieldFormat)), om.Field.FieldFormat.ConditionFormulas, part);
            }

            ReadConditionFormulas(Reflect.Get(ff, "ConditionFormulas", typeof(ISCRFieldFormat)), om.Field.FieldFormat.ConditionFormulas, null);
        }

        private void EnrichTextObject(object ro, ObjectMetadata om)
        {
            var paragraphsObj = Reflect.Get(ro, "Paragraphs", typeof(ISCRTextObject));
            if (paragraphsObj == null) return;

            om.Text = om.Text ?? new TextObjectMetadata();
            var rebuilt = new StringBuilder();
            var paragraphs = new List<ParagraphMetadata>();
            var hasField = false;

            foreach (var p in Reflect.Items(paragraphsObj))
            {
                var pm = new ParagraphMetadata { Alignment = Reflect.GetString(p, "Alignment", typeof(ISCRParagraph)) };
                if (paragraphs.Count > 0) rebuilt.Append("\r\n");

                foreach (var e in Reflect.Items(Reflect.Get(p, "ParagraphElements", typeof(ISCRParagraph))))
                {
                    var em = new ParagraphElementMetadata();
                    if (e is ISCRParagraphFieldElement fieldElement)
                    {
                        em.Kind = "Field";
                        em.DataSource = DataSourceText(Reflect.Get(fieldElement, "DataSource", typeof(ISCRParagraphFieldElement)));
                        rebuilt.Append(em.DataSource ?? "{?}");
                        hasField = true;
                    }
                    else
                    {
                        em.Kind = "Text";
                        em.Text = Reflect.GetString(e, "Text", typeof(ISCRParagraphTextElement)) ?? string.Empty;
                        rebuilt.Append(em.Text);
                    }

                    var fc = Reflect.Get(e, "FontColor", typeof(ISCRParagraphElement));
                    if (fc != null) em.Font = ReadFont(fc);
                    pm.Elements.Add(em);
                }

                paragraphs.Add(pm);
            }

            om.Text.Paragraphs = paragraphs;
            if (hasField || string.IsNullOrEmpty(om.Text.Text))
            {
                om.Text.Text = rebuilt.ToString();
            }
        }

        private void EnrichPicture(object ro, ObjectMetadata om)
        {
            om.Picture = om.Picture ?? new PictureObjectMetadata();
            om.Picture.OriginalWidthTwips = Reflect.GetInt(ro, "OriginalWidth", typeof(ISCRPictureObject));
            om.Picture.OriginalHeightTwips = Reflect.GetInt(ro, "OriginalHeight", typeof(ISCRPictureObject));
            om.Picture.XScaling = Reflect.GetDouble(ro, "XScaling", typeof(ISCRPictureObject));
            om.Picture.YScaling = Reflect.GetDouble(ro, "YScaling", typeof(ISCRPictureObject));
            om.Picture.CropTwips.Left = Reflect.GetInt(ro, "CropLeft", typeof(ISCRPictureObject));
            om.Picture.CropTwips.Top = Reflect.GetInt(ro, "CropTop", typeof(ISCRPictureObject));
            om.Picture.CropTwips.Right = Reflect.GetInt(ro, "CropRight", typeof(ISCRPictureObject));
            om.Picture.CropTwips.Bottom = Reflect.GetInt(ro, "CropBottom", typeof(ISCRPictureObject));
        }

        private void EnrichCrossTab(object ro, ObjectMetadata om)
        {
            om.CrossTab = om.CrossTab ?? new CrossTabObjectMetadata();
            // cross-tab model interfaces vary between SPs; resolve members through the RCW's own interfaces
            var def = Reflect.Get(ro, "CrossTabDefinition", typeof(ISCRCrossTabObject)) ?? Reflect.Get(ro, "CrossTabDefinition");
            if (def == null) return;
            var defTypes = def.GetType().GetInterfaces();
            foreach (var g in Reflect.Items(Reflect.Get(def, "Rows", defTypes)))
            {
                var name = DataSourceText(Reflect.Get(g, "ConditionField", g.GetType().GetInterfaces()));
                if (name != null) om.CrossTab.Rows.Add(name);
            }

            foreach (var g in Reflect.Items(Reflect.Get(def, "Columns", defTypes)))
            {
                var name = DataSourceText(Reflect.Get(g, "ConditionField", g.GetType().GetInterfaces()));
                if (name != null) om.CrossTab.Columns.Add(name);
            }

            foreach (var s in Reflect.Items(Reflect.Get(def, "SummaryFields", defTypes)))
            {
                var name = DataSourceText(s);
                if (name != null) om.CrossTab.Summaries.Add(name);
            }
        }

        private void EnrichChart(object ro, ObjectMetadata om)
        {
            om.Chart = om.Chart ?? new ChartObjectMetadata();
            var style = Reflect.Get(ro, "ChartStyle", typeof(ISCRChartObject));
            om.Chart.ChartType = Reflect.GetString(style, "Type", typeof(ISCRChartStyle)) ?? Reflect.GetString(ro, "ChartType");
            if (_ctx.Options.IncludeRawBags && style != null)
            {
                om.Chart.Raw = Reflect.ScalarBag(style, null, typeof(ISCRChartStyle));
            }
        }

        /// <summary>Parent → child links (main document only). LinkedParameterName comes back in formula form "{?Pm-…}".</summary>
        public List<SubreportLinkMetadata> GetSubreportLinks(string subreportName)
        {
            var list = new List<SubreportLinkMetadata>();
            if (_main == null) return list;

            var links = _main.SubreportController.GetSubreportLinks(subreportName);
            foreach (var l in Reflect.Items(links))
            {
                var linked = Text.NullIfEmpty(Reflect.GetString(l, "LinkedParameterName", typeof(ISCRSubreportLink)));
                var link = new SubreportLinkMetadata
                {
                    MainReportField = Reflect.GetString(l, "MainReportFieldName", typeof(ISCRSubreportLink)),
                    SubreportField = Text.NullIfEmpty(Reflect.GetString(l, "SubreportFieldName", typeof(ISCRSubreportLink)))
                };

                if (linked != null)
                {
                    if (linked.StartsWith("{?", StringComparison.Ordinal) && linked.EndsWith("}", StringComparison.Ordinal))
                    {
                        link.SubreportParameter = linked;
                        link.LinkedParameterName = linked.Substring(2, linked.Length - 3);
                    }
                    else
                    {
                        link.SubreportParameter = "{?" + linked + "}";
                        link.LinkedParameterName = linked;
                    }
                }

                list.Add(link);
            }

            return list;
        }

        // ---------------------------------------------------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// RAS condition-formula collections are keyed, not listed: e.g. <c>ISCRObjectFormatConditionFormulas.Formula[CrObjectFormatConditionFormulaTypeEnum]</c>.
        /// The <c>ISCRConditionFormula</c> itself carries only Syntax/Text. So: find the indexed <c>Formula</c> property on the
        /// collection's interface, enumerate every value of its enum key, and keep the entries with text.
        /// </summary>
        private static void ReadConditionFormulas(object? formulas, List<ConditionFormulaMetadata> target, string? prefix)
        {
            if (formulas == null) return;

            System.Reflection.PropertyInfo? indexer = null;
            foreach (var t in formulas.GetType().GetInterfaces())
            {
                try
                {
                    var p = t.GetProperty("Formula");
                    if (p != null && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType.IsEnum)
                    {
                        indexer = p;
                        break;
                    }
                }
                catch
                {
                    // ambiguous / not applicable
                }
            }

            if (indexer == null)
            {
                // fall back to plain enumeration (older SPs)
                foreach (var cf in Reflect.Items(formulas))
                {
                    var text = Reflect.GetString(cf, "Text", typeof(ISCRConditionFormula));
                    if (string.IsNullOrEmpty(text)) continue;
                    var type = Reflect.GetString(cf, "ConditionFormulaType") ?? "Unknown";
                    target.Add(new ConditionFormulaMetadata { Type = Combine(prefix, TrimEnumName(type)), Text = text! });
                }

                return;
            }

            var keyType = indexer.GetIndexParameters()[0].ParameterType;
            foreach (var key in Enum.GetValues(keyType))
            {
                object? cf;
                try
                {
                    cf = indexer.GetValue(formulas, new[] { key });
                }
                catch
                {
                    continue;
                }

                var text = Reflect.GetString(cf, "Text", typeof(ISCRConditionFormula), typeof(ISCRDataConditionFormula));
                if (string.IsNullOrEmpty(text)) continue;
                var syntax = NormalizeSyntax(Reflect.GetString(cf, "Syntax", typeof(ISCRConditionFormula), typeof(ISCRDataConditionFormula)));
                var entry = new ConditionFormulaMetadata { Type = Combine(prefix, TrimEnumName(key.ToString() ?? "Unknown")), Text = text! };
                if (syntax != null && syntax != "Crystal") entry.Type += " (" + syntax + ")";
                target.Add(entry);
            }
        }

        /// <summary>"crObjectFormatConditionFormulaTypeEnableSuppress" → "EnableSuppress"; "crSortDirection" → "SortDirection".</summary>
        private static string TrimEnumName(string name)
        {
            var idx = name.IndexOf("ConditionFormulaType", StringComparison.Ordinal);
            if (idx >= 0) return name.Substring(idx + "ConditionFormulaType".Length);
            return name.StartsWith("cr", StringComparison.Ordinal) && name.Length > 2 ? name.Substring(2) : name;
        }

        private static string Combine(string? prefix, string type) => prefix == null ? type : prefix + "." + type;

        private static FontMetadata ReadFont(object fontColor)
        {
            var font = Reflect.Get(fontColor, "Font", typeof(ISCRFontColor));
            var color = Reflect.GetInt(fontColor, "Color", typeof(ISCRFontColor));
            return new FontMetadata
            {
                Name = Reflect.GetString(font, "Name", typeof(ISCRFont)),
                SizePoints = Reflect.GetDouble(font, "Size", typeof(ISCRFont)),
                Bold = Reflect.GetBool(font, "Bold", typeof(ISCRFont)),
                Italic = Reflect.GetBool(font, "Italic", typeof(ISCRFont)),
                Underline = Reflect.GetBool(font, "Underline", typeof(ISCRFont)),
                Strikeout = Reflect.GetBool(font, "Strikethrough", typeof(ISCRFont)) ?? Reflect.GetBool(font, "Strikeout", typeof(ISCRFont)),
                Color = color.HasValue ? MetadataJson.FormatColorRef(color.Value) : null
            };
        }

        /// <summary>RAS data sources are either a formula-form string ("{Table.Field}") or a field object exposing FormulaForm.</summary>
        private static string? DataSourceText(object? ds)
        {
            if (ds == null) return null;
            if (ds is string s) return Text.NullIfEmpty(s);
            return Reflect.GetString(ds, "FormulaForm", typeof(ISCRField)) ?? Reflect.GetString(ds, "Name", typeof(ISCRField));
        }

        private static object? InvokeNoArg(object? target, string methodName)
        {
            if (target == null) return null;
            var mi = FindMethod(target, methodName, null);
            try
            {
                return mi?.Invoke(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static System.Reflection.MethodInfo? FindMethod(object? target, string name, Type? preferredInterface, params Type[] parameterTypes)
        {
            if (target == null) return null;
            var candidates = new List<Type>();
            if (preferredInterface != null && preferredInterface.IsInstanceOfType(target)) candidates.Add(preferredInterface);
            candidates.Add(target.GetType());
            candidates.AddRange(target.GetType().GetInterfaces());
            foreach (var t in candidates)
            {
                try
                {
                    var mi = parameterTypes.Length > 0 ? t.GetMethod(name, parameterTypes) : t.GetMethod(name);
                    if (mi != null) return mi;
                }
                catch
                {
                    // ambiguous match etc. — try the next type
                }
            }

            return null;
        }
    }
}
