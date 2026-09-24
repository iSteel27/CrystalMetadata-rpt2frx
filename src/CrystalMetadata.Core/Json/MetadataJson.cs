using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace CrystalMetadata.Core.Json
{
    /// <summary>
    /// The single source of truth for JSON serialization on both sides of the pipe and for files on disk.
    /// camelCase properties, enums as strings, ISO-8601 UTC dates, explicit nulls (null = "not available from Crystal"),
    /// dictionary keys untouched (raw bags / logon properties keep the SDK's names).
    /// </summary>
    public static class MetadataJson
    {
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static JsonSerializerSettings CreateSettings(bool indented)
        {
            return new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy
                    {
                        ProcessDictionaryKeys = false,
                        OverrideSpecifiedNames = false
                    }
                },
                Converters = { new StringEnumConverter() },
                NullValueHandling = NullValueHandling.Include,
                DefaultValueHandling = DefaultValueHandling.Include,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                DateFormatString = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                Formatting = indented ? Formatting.Indented : Formatting.None,
                ReferenceLoopHandling = ReferenceLoopHandling.Error,
                MaxDepth = 256,
                TypeNameHandling = TypeNameHandling.None
            };
        }

        public static readonly JsonSerializerSettings Compact = CreateSettings(indented: false);
        public static readonly JsonSerializerSettings Indented = CreateSettings(indented: true);

        public static string Serialize<T>(T value, bool indented = false)
        {
            return JsonConvert.SerializeObject(value, indented ? Indented : Compact);
        }

        public static byte[] SerializeToBytes<T>(T value)
        {
            return Utf8NoBom.GetBytes(Serialize(value));
        }

        public static T? Deserialize<T>(string json) where T : class
        {
            return JsonConvert.DeserializeObject<T>(json, Compact);
        }

        public static T? Deserialize<T>(byte[] utf8) where T : class
        {
            return Deserialize<T>(Utf8NoBom.GetString(utf8));
        }

        public static void WriteFile<T>(string path, T value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, Serialize(value, indented: true), Utf8NoBom);
        }

        public static T? ReadFile<T>(string path) where T : class
        {
            return Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>Formats a colour as <c>#RRGGBB</c>; callers pass ARGB components.</summary>
        public static string FormatColor(byte r, byte g, byte b)
        {
            return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }

        public static string FormatColor(int argb)
        {
            return FormatColor((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
        }

        /// <summary>Formats a Crystal RAS colour (COLORREF: 0x00BBGGRR) as <c>#RRGGBB</c>.</summary>
        public static string FormatColorRef(int colorRef)
        {
            return FormatColor((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));
        }

        public static DateTime ToUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }
    }
}
