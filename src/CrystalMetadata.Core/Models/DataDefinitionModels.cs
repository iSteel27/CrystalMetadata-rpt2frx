using System.Collections.Generic;

namespace CrystalMetadata.Core.Models
{
    /// <summary>A report / query / stored-procedure parameter.</summary>
    public sealed class ParameterMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string FormulaName { get; set; } = string.Empty;
        public string? ValueType { get; set; }
        public string? ParameterType { get; set; }
        public string? DiscreteOrRange { get; set; }
        public bool? AllowMultipleValues { get; set; }
        public bool? AllowNullValue { get; set; }
        public bool? IsOptionalPrompt { get; set; }
        public string? PromptText { get; set; }
        public string? EditMask { get; set; }
        public string? MinimumValue { get; set; }
        public string? MaximumValue { get; set; }
        public List<ParameterValueMetadata> DefaultValues { get; set; } = new List<ParameterValueMetadata>();
        public List<ParameterValueMetadata> CurrentValues { get; set; } = new List<ParameterValueMetadata>();
        public bool? HasCurrentValue { get; set; }
        public string? ValueListKind { get; set; }

        /// <summary>Empty for main-report parameters; the subreport name for parameters that belong to a subreport.</summary>
        public string? ReportName { get; set; }

        /// <summary>True when the parameter is fed by the parent report (Crystal "Pm-" convention or subreport link).</summary>
        public bool IsLinkedFromParentReport { get; set; }

        /// <summary>For linked parameters: the parent-side field Crystal derived the name from, e.g. <c>{Customer.CustomerCode}</c>.</summary>
        public string? LinkedSourceHint { get; set; }

        public string? Usage { get; set; }
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    /// <summary>A discrete or range parameter value.</summary>
    public sealed class ParameterValueMetadata
    {
        public string? Value { get; set; }
        public string? Description { get; set; }
        public string? RangeStart { get; set; }
        public string? RangeEnd { get; set; }
        public string? LowerBoundType { get; set; }
        public string? UpperBoundType { get; set; }
    }

    /// <summary>A formula field. <see cref="Text"/> is verbatim — never parsed, trimmed or reformatted.</summary>
    public sealed class FormulaMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string FormulaName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string? Syntax { get; set; }
        public string? ValueType { get; set; }
        public int? UseCount { get; set; }
    }

    public sealed class SqlExpressionMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string FormulaName { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string? ValueType { get; set; }
    }

    public sealed class CustomFunctionMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string? Syntax { get; set; }
    }

    public sealed class SummaryFieldMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string FormulaName { get; set; } = string.Empty;
        public string? Operation { get; set; }
        public string? OperationParameter { get; set; }
        public string? SummarizedField { get; set; }
        public string? SecondarySummarizedField { get; set; }

        /// <summary>1-based group index the summary belongs to; null for grand totals.</summary>
        public int? GroupIndex { get; set; }

        public string? GroupConditionField { get; set; }
        public string? ValueType { get; set; }
    }

    public sealed class RunningTotalMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string FormulaName { get; set; } = string.Empty;
        public string? Operation { get; set; }
        public string? OperationParameter { get; set; }
        public string? SummarizedField { get; set; }
        public string? SecondarySummarizedField { get; set; }
        public string? EvaluationConditionType { get; set; }
        public string? EvaluationCondition { get; set; }
        public string? ResetConditionType { get; set; }
        public string? ResetCondition { get; set; }
        public string? ValueType { get; set; }
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class GroupMetadata
    {
        /// <summary>1-based group level.</summary>
        public int Index { get; set; }
        public string? ConditionField { get; set; }
        public string? ConditionFieldKind { get; set; }
        public string? SortDirection { get; set; }
        public string? Condition { get; set; }
        public bool? KeepGroupTogether { get; set; }
        public bool? RepeatGroupHeader { get; set; }
        public string? CustomGroupNameFormula { get; set; }
        public List<string> SpecifiedOrderNames { get; set; } = new List<string>();
        public string? TopN { get; set; }
        public Dictionary<string, object?> Raw { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class SortMetadata
    {
        public string? Field { get; set; }
        public string? Direction { get; set; }
        public string? SortType { get; set; }
    }
}
