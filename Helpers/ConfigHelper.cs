using System;

namespace WeatherImageApp.Helpers
{
    public static class ConfigHelper
    {
        public static string Get(string name, string? fallback = null)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? (fallback ?? string.Empty) : v;
        }

        public static int GetInt(string name, int fallback)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return int.TryParse(v, out var i) ? i : fallback;
        }
    }
}
