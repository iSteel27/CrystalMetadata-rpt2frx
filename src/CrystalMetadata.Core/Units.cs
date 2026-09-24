using System;

namespace CrystalMetadata.Core
{
    /// <summary>
    /// Unit conversions for Crystal Reports layout values. The SDK reports every position and size in twips
    /// (1 inch = 1440 twips = 72 points; 1 point = 20 twips). Font sizes are already in points.
    /// </summary>
    public static class Units
    {
        public const int TwipsPerInch = 1440;
        public const int TwipsPerPoint = 20;
        public const double TwipsPerMm = 56.6929;

        /// <summary>Converts twips to points (1/72 inch).</summary>
        public static double ToPoints(int twips) => Math.Round(twips / (double)TwipsPerPoint, 2);

        /// <summary>Converts twips to millimetres.</summary>
        public static double ToMm(int twips) => Math.Round(twips / TwipsPerMm, 2);

        /// <summary>Converts twips to inches.</summary>
        public static double ToInches(int twips) => Math.Round(twips / (double)TwipsPerInch, 4);
    }
}
