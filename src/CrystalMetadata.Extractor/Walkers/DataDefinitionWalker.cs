using System;
using System.Collections.Generic;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.Shared;
using CrystalMetadata.Core.Diagnostics;
using CrystalMetadata.Core.Models;
using CrystalMetadata.Extractor.Helpers;

namespace CrystalMetadata.Extractor.Walkers
{
    /// <summary>Parameters, formulas, SQL expressions, summaries, running totals, groups and sort fields (Engine API).</summary>
    internal static class DataDefinitionWalker
    {
        private static readonly HashSet<string> ParameterExclude = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "FormulaName", "ParameterFieldName", "ValueType", "Kind", "ParameterValueKind", "ParameterType", "DiscreteOrRangeKind",
            "EnableAllowMultipleValue", "EnableNullValue", "IsOptionalPrompt", "PromptText", "EditMask", "MinimumValue", "MaximumValue",
            "DefaultValues", "CurrentValues", "HasCurrentValue", "ReportName", "ParameterFieldUsage2", "ParameterFieldUsage", "UseCount", "NumberOfBytes"
        };

        public static List<ParameterMetadata> ReadParameters(ReportDocument doc, ExtractionContext ctx)
        {
            var list = new List<ParameterMetadata>();
            var i = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.DataDefinition.ParameterFields)))
            {
                var path = "parameters[" + i++ + "]";
                var p = item as ParameterFieldDefinition;
                if (p == null) continue;

                var pm = ctx.Try(path, DiagnosticCodes.ParameterFailed, () => ReadParameter(p, ctx));
                if (pm != null) list.Add(pm);
            }

            return list;
        }

        private static ParameterMetadata ReadParameter(ParameterFieldDefinition p, ExtractionContext ctx)
        {
            var name = p.Name ?? string.Empty;
            var pm = new ParameterMetadata
            {
                Name = name,
                FormulaName = ctx.QuietRef(() => p.FormulaName) ?? ("{?" + name + "}"),
                ValueType = Text.Of(ctx.Quiet(() => p.ParameterValueKind)),
                ParameterType = Text.Of(ctx.Quiet(() => p.ParameterType)),
                DiscreteOrRange = Text.Of(ctx.Quiet(() => p.DiscreteOrRangeKind)),
                AllowMultipleValues = ctx.Quiet(() => p.EnableAllowMultipleValue),
                AllowNullValue = ctx.Quiet(() => p.EnableNullValue),
                IsOptionalPrompt = ctx.Quiet(() => p.IsOptionalPrompt),
                PromptText = ctx.QuietRef(() => p.PromptText),
                EditMask = Text.NullIfEmpty(ctx.QuietRef(() => p.EditMask)),
                MinimumValue = Reflect.ToText(ctx.QuietRef(() => p.MinimumValue)),
                MaximumValue = Reflect.ToText(ctx.QuietRef(() => p.MaximumValue)),
                HasCurrentValue = ctx.Quiet(() => p.HasCurrentValue),
                ReportName = ctx.QuietRef(() => p.ReportName),
                Usage = Text.Of(ctx.Quiet(() => p.ParameterFieldUsage2))
            };

            pm.DefaultValues = ReadValues(ctx.QuietRef(() => (object)p.DefaultValues));
            pm.CurrentValues = ReadValues(ctx.QuietRef(() => (object)p.CurrentValues));

            if (name.StartsWith("Pm-", StringComparison.OrdinalIgnoreCase))
            {
                pm.IsLinkedFromParentReport = true;
                pm.LinkedSourceHint = LinkedSourceHint(name.Substring(3));
            }

            if (ctx.Options.IncludeRawBags)
            {
                pm.Raw = Reflect.ScalarBag(p, ParameterExclude);
            }

            return pm;
        }

        /// <summary>Crystal names linked parameters "Pm-Table.Field", "Pm-@Formula" or "Pm-?Param".</summary>
        internal static string LinkedSourceHint(string rest)
        {
            if (rest.StartsWith("@", StringComparison.Ordinal)) return "{" + rest + "}";
            if (rest.StartsWith("?", StringComparison.Ordinal)) return "{" + rest + "}";
            if (rest.StartsWith("#", StringComparison.Ordinal)) return "{" + rest + "}";
            return "{" + rest + "}";
        }

        private static List<ParameterValueMetadata> ReadValues(object? values)
        {
            var list = new List<ParameterValueMetadata>();
            foreach (var v in Reflect.Items(values))
            {
                var pv = new ParameterValueMetadata();
                if (v is ParameterDiscreteValue d)
                {
                    pv.Value = Reflect.ToText(d.Value);
                    pv.Description = Text.NullIfEmpty(Reflect.GetString(d, "Description"));
                }
                else if (v is ParameterRangeValue r)
                {
                    pv.RangeStart = Reflect.ToText(r.StartValue);
                    pv.RangeEnd = Reflect.ToText(r.EndValue);
                    pv.LowerBoundType = Text.Of(r.LowerBoundType);
                    pv.UpperBoundType = Text.Of(r.UpperBoundType);
                }
                else
                {
                    pv.Value = Reflect.GetString(v, "Value") ?? Reflect.ToText(v);
                }

                list.Add(pv);
            }

            return list;
        }

        public static List<FormulaMetadata> ReadFormulas(ReportDocument doc, ExtractionContext ctx)
        {
            var list = new List<FormulaMetadata>();
            var i = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.DataDefinition.FormulaFields)))
            {
                var path = "formulas[" + i++ + "]";
                var f = item as FormulaFieldDefinition;
                if (f == null) continue;

                var fm = ctx.Try(path, DiagnosticCodes.FormulaFailed, () => new FormulaMetadata
                {
                    Name = f.Name ?? string.Empty,
                    FormulaName = ctx.QuietRef(() => f.FormulaName) ?? ("{@" + f.Name + "}"),
                    Text = f.Text ?? string.Empty,
                    ValueType = Text.Of(ctx.Quiet(() => f.ValueType)),
                    UseCount = ctx.Quiet(() => f.UseCount)
                });
                if (fm != null) list.Add(fm);
            }

            return list;
        }

        public static List<SqlExpressionMetadata> ReadSqlExpressions(ReportDocument doc, ExtractionContext ctx)
        {
            var list = new List<SqlExpressionMetadata>();
            var i = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.DataDefinition.SQLExpressionFields)))
            {
                var path = "sqlExpressions[" + i++ + "]";
                var f = item as SQLExpressionFieldDefinition;
                if (f == null) continue;

                var sm = ctx.Try(path, DiagnosticCodes.FormulaFailed, () => new SqlExpressionMetadata
                {
                    Name = f.Name ?? string.Empty,
                    FormulaName = ctx.QuietRef(() => f.FormulaName) ?? ("{%" + f.Name + "}"),
                    Text = f.Text ?? string.Empty,
                    ValueType = Text.Of(ctx.Quiet(() => f.ValueType))
                });
                if (sm != null) list.Add(sm);
            }

            return list;
        }

        public static List<SummaryFieldMetadata> ReadSummaries(ReportDocument doc, IList<Group> groups, ExtractionContext ctx)
        {
            var list = new List<SummaryFieldMetadata>();
            var i = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.DataDefinition.SummaryFields)))
            {
                var path = "summaryFields[" + i++ + "]";
                var s = item as SummaryFieldDefinition;
                if (s == null) continue;

                var sm = ctx.Try(path, DiagnosticCodes.SummaryFailed, () =>
                {
                    var m = new SummaryFieldMetadata
                    {
                        Name = s.Name ?? string.Empty,
                        FormulaName = ctx.QuietRef(() => s.FormulaName) ?? string.Empty,
                        Operation = Text.Of(ctx.Quiet(() => s.Operation)),
                        OperationParameter = Reflect.ToText(ctx.QuietRef(() => (object?)s.OperationParameter)),
                        SummarizedField = ctx.QuietRef(() => s.SummarizedField?.FormulaName),
                        SecondarySummarizedField = ctx.QuietRef(() => s.SecondarySummarizedField?.FormulaName),
                        ValueType = Text.Of(ctx.Quiet(() => s.ValueType))
                    };

                    var group = ctx.QuietRef(() => s.Group);
                    if (group != null)
                    {
                        m.GroupConditionField = ctx.QuietRef(() => group.ConditionField?.FormulaName);
                        var idx = IndexOfGroup(groups, group);
                        m.GroupIndex = idx > 0 ? idx : (int?)null;
                    }

                    return m;
                });
                if (sm != null) list.Add(sm);
            }

            return list;
        }

        private static int IndexOfGroup(IList<Group> groups, Group group)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                if (ReferenceEquals(groups[i], group))
                {
                    return i + 1;
                }
            }

            // fall back to condition-field identity (the SDK sometimes hands out distinct wrappers)
            var target = Reflect.GetString(Reflect.Get(group, "ConditionField"), "FormulaName");
            if (target != null)
            {
                for (var i = 0; i < groups.Count; i++)
                {
                    if (Reflect.GetString(Reflect.Get(groups[i], "ConditionField"), "FormulaName") == target)
                    {
                        return i + 1;
                    }
                }
            }

            return 0;
        }

        public static List<RunningTotalMetadata> ReadRunningTotals(ReportDocument doc, ExtractionContext ctx)
        {
            var list = new List<RunningTotalMetadata>();
            var i = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.DataDefinition.RunningTotalFields)))
            {
                var path = "runningTotals[" + i++ + "]";
                var r = item as RunningTotalFieldDefinition;
                if (r == null) continue;

                var rm = ctx.Try(path, DiagnosticCodes.RunningTotalFailed, () =>
                {
                    var m = new RunningTotalMetadata
                    {
                        Name = r.Name ?? string.Empty,
                        FormulaName = ctx.QuietRef(() => r.FormulaName) ?? ("{#" + r.Name + "}"),
                        Operation = Text.Of(ctx.Quiet(() => r.Operation)),
                        OperationParameter = Reflect.ToText(Reflect.Get(r, "OperationParameter")),
                        SummarizedField = ctx.QuietRef(() => r.SummarizedField?.FormulaName),
                        SecondarySummarizedField = Reflect.GetString(Reflect.Get(r, "SecondarySummarizedField"), "FormulaName"),
                        EvaluationConditionType = Reflect.GetString(r, "EvaluationConditionType"),
                        EvaluationCondition = ConditionText(Reflect.Get(r, "EvaluationCondition")),
                        ResetConditionType = Reflect.GetString(r, "ResetConditionType"),
                        ResetCondition = ConditionText(Reflect.Get(r, "ResetCondition")),
                        ValueType = Text.Of(ctx.Quiet(() => r.ValueType))
                    };

                    if (ctx.Options.IncludeRawBags)
                    {
                        m.Raw = Reflect.ScalarBag(r, new HashSet<string> { "Name", "FormulaName", "Operation", "ValueType", "Kind", "UseCount", "NumberOfBytes" });
                    }

                    return m;
                });
                if (rm != null) list.Add(rm);
            }

            return list;
        }

        /// <summary>A running-total condition is a field, a group, a formula string or nothing.</summary>
        private static string? ConditionText(object? condition)
        {
            if (condition == null) return null;
            if (condition is string s) return s;
            var formulaName = Reflect.GetString(condition, "FormulaName");
            if (formulaName != null) return formulaName;
            var groupField = Reflect.GetString(Reflect.Get(condition, "ConditionField"), "FormulaName");
            if (groupField != null) return "Group(" + groupField + ")";
            return Reflect.ToText(condition);
        }

        public static List<Group> EngineGroups(ReportDocument doc, ExtractionContext ctx)
        {
            var groups = new List<Group>();
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.DataDefinition.Groups)))
            {
                if (item is Group g) groups.Add(g);
            }

            return groups;
        }

        public static List<GroupMetadata> ReadGroups(IList<Group> groups, IList<SortMetadata> sorts, ExtractionContext ctx)
        {
            var list = new List<GroupMetadata>();
            var groupSorts = new List<SortMetadata>();
            foreach (var s in sorts)
            {
                // SDK enum SortFieldType member names are "GroupSortField" / "RecordSortField"
                if (s.SortType != null && s.SortType.StartsWith("GroupSort", StringComparison.OrdinalIgnoreCase))
                {
                    groupSorts.Add(s);
                }
            }

            for (var i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var index = i + 1;
                var path = "groups[" + i + "]";
                var gm = ctx.Try(path, DiagnosticCodes.GroupFailed, () =>
                {
                    var conditionField = ctx.QuietRef(() => g.ConditionField);
                    var m = new GroupMetadata
                    {
                        Index = index,
                        ConditionField = ctx.QuietRef(() => conditionField?.FormulaName),
                        ConditionFieldKind = Text.Of(conditionField == null ? null : ctx.Quiet(() => conditionField.Kind)),
                        Condition = Text.Of(ctx.Quiet(() => g.GroupOptions.Condition))
                    };

                    // group sort direction lives in SortFields (SortType == GroupSort), in group order
                    if (i < groupSorts.Count)
                    {
                        m.SortDirection = groupSorts[i].Direction;
                    }
                    else
                    {
                        foreach (var s in groupSorts)
                        {
                            if (s.Field == m.ConditionField)
                            {
                                m.SortDirection = s.Direction;
                                break;
                            }
                        }
                    }

                    if (ctx.Options.IncludeRawBags)
                    {
                        var options = ctx.QuietRef(() => (object)g.GroupOptions);
                        m.Raw = Reflect.ScalarBag(options, new HashSet<string> { "Condition" });
                    }

                    return m;
                });
                if (gm != null) list.Add(gm);
            }

            return list;
        }

        public static List<SortMetadata> ReadSortFields(ReportDocument doc, ExtractionContext ctx)
        {
            var list = new List<SortMetadata>();
            var i = 0;
            foreach (var item in Reflect.Items(ctx.QuietRef(() => (object)doc.DataDefinition.SortFields)))
            {
                var path = "sortFields[" + i++ + "]";
                var s = item as SortField;
                if (s == null) continue;

                var sm = ctx.Try(path, DiagnosticCodes.SortFailed, () => new SortMetadata
                {
                    Field = ctx.QuietRef(() => s.Field?.FormulaName),
                    Direction = Text.Of(ctx.Quiet(() => s.SortDirection)),
                    SortType = Text.Of(ctx.Quiet(() => s.SortType))
                });
                if (sm != null) list.Add(sm);
            }

            return list;
        }
    }
}
