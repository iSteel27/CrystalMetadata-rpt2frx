using System;
using System.Linq;

namespace CrystalMetadata.Core.Models
{
    /// <summary>
    /// Helpers for interpreting suppress flags correctly. In Crystal a conditional "Suppress" formula overrides the static
    /// checkbox, and the SDK still reports the checkbox as true in that case — so <c>format.suppress == true</c> alone does
    /// NOT mean the object is hidden. It is hidden only when there is no suppress formula deciding at run time.
    /// </summary>
    public static class MetadataExtensions
    {
        /// <summary>True when a formula decides at run time whether the object is suppressed.</summary>
        public static bool HasConditionalSuppress(this ObjectFormatMetadata format) =>
            format.ConditionFormulas.Any(c => c.Type.StartsWith("EnableSuppress", StringComparison.OrdinalIgnoreCase));

        /// <summary>True when the object never prints (static suppress and no formula overriding it).</summary>
        public static bool IsAlwaysSuppressed(this ObjectFormatMetadata format) =>
            format.Suppress == true && !format.HasConditionalSuppress();

        /// <summary>The Crystal formula that suppresses the object when it evaluates to true, or null.</summary>
        public static string? SuppressFormula(this ObjectFormatMetadata format) =>
            format.ConditionFormulas.FirstOrDefault(c => c.Type.StartsWith("EnableSuppress", StringComparison.OrdinalIgnoreCase))?.Text;

        public static bool HasConditionalSuppress(this SectionFormatMetadata format) =>
            format.ConditionFormulas.Any(c => c.Type.StartsWith("EnableSuppress", StringComparison.OrdinalIgnoreCase));

        public static bool IsAlwaysSuppressed(this SectionFormatMetadata format) =>
            format.Suppress == true && !format.HasConditionalSuppress();
    }
}
