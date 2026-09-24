using System;
using System.Collections.Generic;
using CrystalDecisions.CrystalReports.Engine;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Extractor.Helpers;

namespace CrystalMetadata.Extractor.Walkers
{
    /// <summary>Tables, fields, connections and joins via the Engine API. RAS adds command SQL / SP detection afterwards.</summary>
    internal static class DataSourceWalker
    {
        private static readonly HashSet<string> TableExclude = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "Location", "Fields", "LogOnInfo", "ReportName"
        };

        private static readonly HashSet<string> FieldExclude = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "FormulaName", "TableName", "ValueType", "NumberOfBytes", "UseCount", "Kind", "HeadingText"
        };

        public static DataSourceMetadata Read(ReportDocument doc, ExtractionContext ctx)
        {
            var ds = new DataSourceMetadata();
            var connectionIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Tables? tables = null;
            if (!ctx.Try("dataSource.tables", DiagnosticCodes.DatabaseInfoFailed, () => { tables = doc.Database.Tables; }))
            {
                return ds;
            }

            var index = 0;
            foreach (var item in Reflect.Items(tables))
            {
                var path = "dataSource.tables[" + index + "]";
                var table = item as Table;
                index++;
                if (table == null)
                {
                    continue;
                }

                var tm = ctx.Try(path, DiagnosticCodes.TableFailed, () => ReadTable(table, path, ds, connectionIds, ctx));
                if (tm != null)
                {
                    ds.Tables.Add(tm);
                }
            }

            ctx.Try("dataSource.links", DiagnosticCodes.LinkFailed, () =>
            {
                var i = 0;
                foreach (var item in Reflect.Items(doc.Database.Links))
                {
                    var p = "dataSource.links[" + i++ + "]";
                    var link = item as TableLink;
                    if (link == null)
                    {
                        continue;
                    }

                    var lm = ctx.Try(p, DiagnosticCodes.LinkFailed, () => ReadLink(link, ctx));
                    if (lm != null)
                    {
                        ds.Links.Add(lm);
                    }
                }
            });

            return ds;
        }

        private static TableMetadata ReadTable(Table table, string path, DataSourceMetadata ds, Dictionary<string, string> connectionIds, ExtractionContext ctx)
        {
            var tm = new TableMetadata
            {
                Alias = table.Name ?? string.Empty,
                Name = ctx.QuietRef(() => table.Name),
                Location = ctx.QuietRef(() => table.Location),
                QualifiedName = ctx.QuietRef(() => table.Location),
                Kind = TableKind.Unknown
            };

            if (ctx.Options.IncludeRawBags)
            {
                tm.Raw = Reflect.ScalarBag(table, TableExclude);
            }

            ctx.Try(path + ".connection", DiagnosticCodes.TableFailed, () =>
            {
                var ci = table.LogOnInfo.ConnectionInfo;
                var cm = new ConnectionMetadata
                {
                    ServerName = Text.NullIfEmpty(ci.ServerName),
                    DatabaseName = Text.NullIfEmpty(ci.DatabaseName),
                    UserId = Text.NullIfEmpty(ci.UserID),
                    IntegratedSecurity = ctx.Quiet(() => ci.IntegratedSecurity),
                    ConnectionType = Text.Of(ctx.Quiet(() => ci.Type))
                };

                foreach (var pair in Reflect.Items(ctx.QuietRef(() => (object)ci.LogonProperties)))
                {
                    var name = Reflect.GetString(pair, "Name");
                    if (string.IsNullOrEmpty(name) || IsSecret(name!))
                    {
                        continue;
                    }

                    cm.LogonProperties[name!] = Reflect.GetString(pair, "Value");
                }

                var attrs = ctx.QuietRef(() => (object)ci.Attributes);
                ReadAttributes(attrs, cm.Attributes, 0);

                if (cm.LogonProperties.TryGetValue("Provider", out var provider))
                {
                    cm.Provider = provider;
                }

                if (cm.Attributes.TryGetValue("Database DLL", out var dll) && dll != null)
                {
                    cm.DatabaseDll = Convert.ToString(dll);
                }

                var key = (cm.ServerName ?? "") + "|" + (cm.DatabaseName ?? "") + "|" + (cm.DatabaseDll ?? "") + "|" + (cm.Provider ?? "") + "|" + (cm.ConnectionType ?? "");
                if (!connectionIds.TryGetValue(key, out var id))
                {
                    id = "c" + (ds.Connections.Count + 1);
                    connectionIds[key] = id;
                    cm.Id = id;
                    ds.Connections.Add(cm);
                }

                tm.ConnectionId = id;
            }, DiagnosticLevel.Info);

            ctx.Try(path + ".fields", DiagnosticCodes.FieldFailed, () =>
            {
                var i = 0;
                foreach (var item in Reflect.Items(table.Fields))
                {
                    var fp = path + ".fields[" + i++ + "]";
                    var field = item as DatabaseFieldDefinition;
                    if (field == null)
                    {
                        continue;
                    }

                    var fm = ctx.Try(fp, DiagnosticCodes.FieldFailed, () => ReadField(field, tm.Alias, ctx));
                    if (fm == null)
                    {
                        continue;
                    }

                    if (!fm.IsUsedInReport && !ctx.Options.IncludeUnusedFields)
                    {
                        continue;
                    }

                    tm.Fields.Add(fm);
                }
            });

            return tm;
        }

        private static FieldMetadata ReadField(DatabaseFieldDefinition field, string tableAlias, ExtractionContext ctx)
        {
            var useCount = ctx.Quiet(() => field.UseCount);
            var fm = new FieldMetadata
            {
                Name = field.Name ?? string.Empty,
                FormulaName = ctx.QuietRef(() => field.FormulaName) ?? ("{" + tableAlias + "." + field.Name + "}"),
                TableAlias = ctx.QuietRef(() => field.TableName) ?? tableAlias,
                ValueType = Text.Of(ctx.Quiet(() => field.ValueType)),
                LengthBytes = ctx.Quiet(() => field.NumberOfBytes),
                Nullable = null,
                UseCount = useCount,
                IsUsedInReport = (useCount ?? 0) > 0,
                HeadingText = Text.NullIfEmpty(Reflect.GetString(field, "HeadingText"))
            };

            if (ctx.Options.IncludeRawBags)
            {
                fm.Raw = Reflect.ScalarBag(field, FieldExclude);
            }

            return fm;
        }

        private static TableLinkMetadata ReadLink(TableLink link, ExtractionContext ctx)
        {
            var lm = new TableLinkMetadata
            {
                SourceTable = ctx.QuietRef(() => link.SourceTable?.Name),
                DestinationTable = ctx.QuietRef(() => link.DestinationTable?.Name),
                JoinType = Text.Of(ctx.QuietRef(() => (object?)link.JoinType)),
                LinkOperator = Reflect.GetString(link, "LinkOperator")
            };

            foreach (var f in Reflect.Items(ctx.QuietRef(() => (object)link.SourceFields)))
            {
                var name = Reflect.GetString(f, "FormulaName") ?? Reflect.GetString(f, "Name");
                if (name != null) lm.SourceFields.Add(name);
            }

            foreach (var f in Reflect.Items(ctx.QuietRef(() => (object)link.DestinationFields)))
            {
                var name = Reflect.GetString(f, "FormulaName") ?? Reflect.GetString(f, "Name");
                if (name != null) lm.DestinationFields.Add(name);
            }

            if (ctx.Options.IncludeRawBags)
            {
                lm.Raw = Reflect.ScalarBag(link, new HashSet<string> { "JoinType", "LinkOperator" });
            }

            return lm;
        }

        /// <summary>Walks a DbConnectionAttributes / NameValuePair collection (possibly nested) into a dictionary.</summary>
        private static void ReadAttributes(object? attrs, Dictionary<string, object?> target, int depth)
        {
            if (attrs == null || depth > 3)
            {
                return;
            }

            var collection = Reflect.Get(attrs, "Collection") ?? attrs;
            foreach (var pair in Reflect.Items(collection))
            {
                var name = Reflect.GetString(pair, "Name");
                if (string.IsNullOrEmpty(name) || IsSecret(name!))
                {
                    continue;
                }

                var value = Reflect.Get(pair, "Value");
                if (value == null || Reflect.IsScalar(value))
                {
                    target[name!] = Reflect.ToJsonScalar(value);
                }
                else
                {
                    var nested = new Dictionary<string, object?>(StringComparer.Ordinal);
                    ReadAttributes(value, nested, depth + 1);
                    target[name!] = nested;
                }
            }
        }

        internal static bool IsSecret(string name)
        {
            var n = name.ToLowerInvariant();
            return n.Contains("password") || n.Contains("pwd") || n == "secret";
        }
    }
}
