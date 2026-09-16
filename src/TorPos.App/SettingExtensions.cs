using TorPos.Core;

namespace TorPos.App;

public static class SettingExtensions
{
    public static string GetText(this IReadOnlyDictionary<string,string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out var value) ? value : fallback;

    public static bool GetBool(this IReadOnlyDictionary<string,string> values, string key, bool fallback = false)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    public static int GetInt(this IReadOnlyDictionary<string,string> values, string key, int fallback = 0)
    {
        if (!values.TryGetValue(key, out var value)) return fallback;
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
