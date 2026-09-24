using System;
using System.Drawing;
using CrystalMetadata.Core.Json;
using CrystalMetadata.Core.Models;

namespace CrystalMetadata.Extractor.Helpers
{
    /// <summary>Small formatting helpers shared by the walkers.</summary>
    internal static class Text
    {
        /// <summary>Enum (or any scalar) as text; null stays null.</summary>
        public static string? Of(object? value) => Reflect.ToText(value);

        /// <summary>System.Drawing colour as #RRGGBB; empty colours become null.</summary>
        public static string? Color(Color color) => color.IsEmpty ? null : MetadataJson.FormatColor(color.R, color.G, color.B);

        public static FontMetadata? Font(Font? font, Color color)
        {
            if (font == null)
            {
                return null;
            }

            return new FontMetadata
            {
                Name = font.Name,
                SizePoints = Math.Round(font.SizeInPoints, 2),
                Bold = font.Bold,
                Italic = font.Italic,
                Underline = font.Underline,
                Strikeout = font.Strikeout,
                Color = Color(color)
            };
        }

        public static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
