using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CrystalMetadata.FastReport;

/// <summary>What a Crystal formula field does, as far as the converter can tell.</summary>
public enum FormulaKind
{
    /// <summary>A plain expression with no variables; can be inlined as a FastReport expression.</summary>
    Pure,

    /// <summary><c>numbervar v; v := v + expr</c> — a running accumulator (becomes a FastReport Total).</summary>
    Accumulator,

    /// <summary><c>numbervar v; v</c> — prints the accumulator.</summary>
    Show,

    /// <summary><c>numbervar v; v := 0</c> — resets the accumulator (the reset moment becomes ResetAfterPrint).</summary>
    Reset,

    /// <summary><c>Sum(expr)</c>, <c>Count(expr)</c>, ... used as a field — a FastReport Total scoped to the band it is printed in.</summary>
    Aggregate,

    /// <summary>Anything else; needs manual or LLM translation.</summary>
    Unsupported
}

public sealed record FormulaInfo(FormulaKind Kind, string? Variable, string? Expression, string? Reason);

/// <summary>
/// Deterministic translation of the common Crystal formula shapes to FastReport expressions. Anything it does not
/// recognise is reported as <see cref="FormulaKind.Unsupported"/> with a reason — that is the hand-off point for an
/// LLM or a human. It never guesses.
/// </summary>
public static class CrystalFormulaTranslator
{
    private static readonly Regex AccumulatorRx = new(@"^\s*(?:whileprintingrecords\s*;\s*)?numbervar\s+(\w+)\s*;\s*\1\s*:=\s*\1\s*\+\s*(.+?)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ResetRx = new(@"^\s*(?:whileprintingrecords\s*;\s*)?numbervar\s+(\w+)\s*;\s*\1\s*:=\s*0\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    // "numbervar v; v" or "numbervar v; v/1000": prints the accumulator, optionally inside an arithmetic expression
    private static readonly Regex ShowRx = new(@"^\s*(?:whileprintingrecords\s*;\s*)?numbervar\s+(\w+)\s*;\s*(?<rest>\1\b(?!.*:=).*?)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AggregateRx = new(@"^\s*(sum|count|distinctcount|average|maximum|minimum)\s*\(\s*(.+?)\s*\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex EvalTimeRx = new(@"^\s*(?:whileprintingrecords|whilereadingrecords)\s*;\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Crystal allows 'text' as well as "text"; C# would read 'text' as a char literal. Double-quoted segments are left alone.
    private static readonly Regex SingleQuoteRx = new("(\"[^\"]*\")|'([^']*)'", RegexOptions.Compiled);
    private static readonly HashSet<string> OperatorWords = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not", "xor", "if", "then", "else" };
    private static readonly Regex ChrRx = new(@"\bchr\s*\(\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsNullRx = new(@"\bisnull\s*\(\s*([^()]*?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnsupportedRx = new(@"\b(numbervar|stringvar|datevar|booleanvar|currencyvar|for|while|do|select|case|local|global|shared|exit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FunctionCallRx = new(@"\b([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex IfRx = new(@"^\s*if\s*(.+?)\s*then\s*(.+?)\s*else\s*(.+?)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FieldRx = new(@"\{([^{}@?#%][^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex FormulaRefRx = new(@"\{@([^{}]+)\}", RegexOptions.Compiled);
    private static readonly Regex ParamRefRx = new(@"\{\?([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>Classifies a formula and, where possible, produces its FastReport expression.</summary>
    /// <param name="text">The verbatim Crystal formula text.</param>
    /// <param name="resolveFormula">Looks up another formula by its bare name (for {@Other} references).</param>
    /// <param name="depth">Recursion guard for formulas that reference other formulas.</param>
    public static FormulaInfo Analyze(string text, Func<string, string?> resolveFormula, int depth = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new FormulaInfo(FormulaKind.Unsupported, null, null, "empty formula");
        }

        var reset = ResetRx.Match(text);
        if (reset.Success) return new FormulaInfo(FormulaKind.Reset, reset.Groups[1].Value, null, null);

        var acc = AccumulatorRx.Match(text);
        if (acc.Success)
        {
            var operand = Translate(acc.Groups[2].Value, resolveFormula, depth, out var why);
            return operand == null
                ? new FormulaInfo(FormulaKind.Unsupported, acc.Groups[1].Value, null, why)
                : new FormulaInfo(FormulaKind.Accumulator, acc.Groups[1].Value, operand, null);
        }

        var show = ShowRx.Match(text);
        if (show.Success)
        {
            var rest = show.Groups["rest"].Value.Trim();
            if (rest.Equals(show.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            {
                return new FormulaInfo(FormulaKind.Show, show.Groups[1].Value, null, null);
            }

            var shown = Translate(rest, resolveFormula, depth, out var showWhy);
            return shown == null
                ? new FormulaInfo(FormulaKind.Unsupported, show.Groups[1].Value, null, showWhy)
                : new FormulaInfo(FormulaKind.Show, show.Groups[1].Value, shown, null);
        }

        var aggregate = AggregateRx.Match(EvalTimeRx.Replace(text, string.Empty));
        if (aggregate.Success)
        {
            var operand = Translate(aggregate.Groups[2].Value, resolveFormula, depth, out var aggWhy);
            return operand == null
                ? new FormulaInfo(FormulaKind.Unsupported, null, null, aggWhy)
                : new FormulaInfo(FormulaKind.Aggregate, aggregate.Groups[1].Value.ToLowerInvariant(), operand, null);
        }

        var expr = Translate(text, resolveFormula, depth, out var reason);
        return expr == null
            ? new FormulaInfo(FormulaKind.Unsupported, null, null, reason)
            : new FormulaInfo(FormulaKind.Pure, null, expr, null);
    }

    /// <summary>Translates a variable-free Crystal expression. Returns null (and a reason) when it is not understood.</summary>
    public static string? Translate(string crystal, Func<string, string?> resolveFormula, int depth, out string? reason)
    {
        reason = null;
        if (depth > 6)
        {
            reason = "formula references are nested too deeply";
            return null;
        }

        // "whileprintingrecords;" only controls when Crystal evaluates the formula; it has no meaning for the expression
        var text = EvalTimeRx.Replace(crystal.Trim(), string.Empty).Trim();
        text = IsNullRx.Replace(text, "(($1) == null)");
        text = ChrRx.Replace(text, "Convert.ToString(Convert.ToChar($1))");
        text = SingleQuoteRx.Replace(text, m => m.Groups[1].Success ? m.Value : "\"" + m.Groups[2].Value + "\"");
        text = Regex.Replace(text, @"\bnot\b", "!", RegexOptions.IgnoreCase);
        if (UnsupportedRx.IsMatch(StripStrings(text)))
        {
            reason = "uses variables, loops or statements that need manual translation";
            return null;
        }

        var ifMatch = IfRx.Match(text);
        if (ifMatch.Success)
        {
            var cond = Translate(ifMatch.Groups[1].Value, resolveFormula, depth, out reason);
            var thenPart = cond == null ? null : Translate(ifMatch.Groups[2].Value, resolveFormula, depth, out reason);
            var elsePart = thenPart == null ? null : Translate(ifMatch.Groups[3].Value, resolveFormula, depth, out reason);
            // a C# conditional, not IIf(): IIf returns an untyped object and cannot be compared or multiplied afterwards
            return elsePart == null ? null : $"({cond} ? {thenPart} : {elsePart})";
        }

        // inline {@Other} formulas
        string? failure = null;
        var inlined = FormulaRefRx.Replace(text, m =>
        {
            var referenced = resolveFormula(m.Groups[1].Value);
            if (referenced == null)
            {
                failure ??= "references unknown formula {@" + m.Groups[1].Value + "}";
                return m.Value;
            }

            var info = Analyze(referenced, resolveFormula, depth + 1);
            if (info.Kind != FormulaKind.Pure || info.Expression == null)
            {
                failure ??= "references formula {@" + m.Groups[1].Value + "} which is not a plain expression";
                return m.Value;
            }

            return "(" + info.Expression + ")";
        });
        if (failure != null)
        {
            reason = failure;
            return null;
        }

        text = inlined;
        text = ParamRefRx.Replace(text, "[$1]");
        text = FieldRx.Replace(text, "[$1]");

        var body = StripStrings(text);
        foreach (Match call in FunctionCallRx.Matches(body))
        {
            if (OperatorWords.Contains(call.Groups[1].Value)) continue;
            if (!call.Groups[1].Value.Equals("IIf", StringComparison.OrdinalIgnoreCase) &&
                !call.Groups[1].Value.Equals("Convert", StringComparison.OrdinalIgnoreCase) &&
                !call.Groups[1].Value.Equals("ToString", StringComparison.OrdinalIgnoreCase) &&
                !call.Groups[1].Value.Equals("ToChar", StringComparison.OrdinalIgnoreCase))
            {
                reason = "uses the Crystal function " + call.Groups[1].Value + "(), which has no automatic translation yet";
                return null;
            }
        }

        text = Regex.Replace(text, @"<>", "!=");
        text = Regex.Replace(text, @"(?<![<>!=:])=(?!=)", "==");
        text = Regex.Replace(text, @"\band\b", "&&", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bor\b", "||", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?<=\)|\]|""|\w)\s*&\s*(?=\(|\[|""|\w)", " + ");
        return text;
    }

    /// <summary>Blanks the contents of string literals so keywords inside them are not mistaken for code.</summary>
    private static string StripStrings(string s) => Regex.Replace(s, "\"[^\"]*\"", "\"\"");
}
