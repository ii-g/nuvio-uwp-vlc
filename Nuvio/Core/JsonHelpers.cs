using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.Data.Json;

namespace Nuvio.Core
{
    /// <summary>
    /// Safe accessors over <see cref="Windows.Data.Json"/> values.
    /// Architecture decision D3 names System.Text.Json, but the UWP project carries no
    /// System.Text.Json package reference and this milestone names no new NuGet package, so
    /// the sanctioned D3 fallback (Windows.Data.Json with manual mapping) is used instead.
    /// The models' shapes are unchanged either way.
    /// </summary>
    internal static class JsonHelpers
    {
        public static JsonObject ParseObject(string text)
        {
            JsonValue value;
            if (string.IsNullOrEmpty(text) || !JsonValue.TryParse(text, out value)) return null;
            return value != null && value.ValueType == JsonValueType.Object ? value.GetObject() : null;
        }

        public static JsonValue Parse(string text)
        {
            JsonValue value;
            if (string.IsNullOrEmpty(text) || !JsonValue.TryParse(text, out value)) return null;
            return value;
        }

        public static JsonObject GetObject(JsonObject obj, string name)
        {
            if (obj == null || !obj.ContainsKey(name)) return null;
            var value = obj.GetNamedValue(name);
            return value != null && value.ValueType == JsonValueType.Object ? value.GetObject() : null;
        }

        public static JsonObject GetObject(IJsonValue value)
        {
            return value != null && value.ValueType == JsonValueType.Object ? value.GetObject() : null;
        }

        public static JsonArray GetArray(JsonObject obj, string name)
        {
            if (obj == null || !obj.ContainsKey(name)) return null;
            var value = obj.GetNamedValue(name);
            return value != null && value.ValueType == JsonValueType.Array ? value.GetArray() : null;
        }

        public static string GetString(JsonObject obj, string name, string fallback = null)
        {
            if (obj == null || !obj.ContainsKey(name)) return fallback;
            return GetString(obj.GetNamedValue(name), fallback);
        }

        /// <summary>Reads a string value; numbers are returned in invariant form (imdbRating is string|number).</summary>
        public static string GetString(IJsonValue value, string fallback = null)
        {
            if (value == null) return fallback;
            switch (value.ValueType)
            {
                case JsonValueType.String:
                    return value.GetString();
                case JsonValueType.Number:
                    return value.GetNumber().ToString(CultureInfo.InvariantCulture);
                default:
                    return fallback;
            }
        }

        public static int? GetInt(JsonObject obj, string name)
        {
            if (obj == null || !obj.ContainsKey(name)) return null;
            return GetInt(obj.GetNamedValue(name));
        }

        public static int? GetInt(IJsonValue value)
        {
            var number = GetNumber(value);
            if (number.HasValue) return (int)number.Value;
            return null;
        }

        public static long? GetLong(JsonObject obj, string name)
        {
            if (obj == null || !obj.ContainsKey(name)) return null;
            return GetLong(obj.GetNamedValue(name));
        }

        public static long? GetLong(IJsonValue value)
        {
            var number = GetNumber(value);
            if (number.HasValue) return (long)number.Value;
            return null;
        }

        public static double? GetDouble(JsonObject obj, string name)
        {
            if (obj == null || !obj.ContainsKey(name)) return null;
            return GetNumber(obj.GetNamedValue(name));
        }

        public static double? GetNumber(IJsonValue value)
        {
            if (value == null) return null;
            if (value.ValueType == JsonValueType.Number) return value.GetNumber();
            if (value.ValueType == JsonValueType.String)
            {
                double parsed;
                if (double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return null;
        }

        public static bool? GetBool(JsonObject obj, string name)
        {
            if (obj == null || !obj.ContainsKey(name)) return null;
            var value = obj.GetNamedValue(name);
            if (value == null) return null;
            if (value.ValueType == JsonValueType.Boolean) return value.GetBoolean();
            if (value.ValueType == JsonValueType.String)
            {
                bool parsed;
                if (bool.TryParse(value.GetString(), out parsed)) return parsed;
            }
            return null;
        }

        public static List<string> GetStringList(JsonObject obj, string name)
        {
            var array = GetArray(obj, name);
            if (array == null) return null;
            var result = new List<string>();
            foreach (var item in array)
            {
                var text = GetString(item);
                if (text != null) result.Add(text);
            }
            return result;
        }

        public static JsonValue CreateString(string value)
        {
            return value == null ? JsonValue.CreateNullValue() : JsonValue.CreateStringValue(value);
        }

        public static JsonValue CreateNumber(double value)
        {
            return JsonValue.CreateNumberValue(value);
        }

        public static JsonValue CreateBool(bool value)
        {
            return JsonValue.CreateBooleanValue(value);
        }
    }
}
