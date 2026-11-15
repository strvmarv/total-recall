// src/TotalRecall.Infrastructure/WhatsNew/UpdateSettings.cs
//
// Pure rules for the background update check: when it is on and how often it
// runs. No I/O.

using System;

namespace TotalRecall.Infrastructure.WhatsNew;

public static class UpdateSettings
{
    public const string NoUpdateCheckEnvVar = "TOTAL_RECALL_NO_UPDATE_CHECK";
    public const int DefaultIntervalHours = 24;
    public const int MaxIntervalHours = 8760;

    /// <summary>
    /// True when the environment value turns the check off: any value that is non-empty after
    /// trimming and is neither "0" nor "false" (case-insensitive).
    /// </summary>
    public static bool EnvDisablesCheck(string? value)
    {
        if (value is null) return false;
        var t = value.Trim();
        if (t.Length == 0) return false;
        if (string.Equals(t, "0", StringComparison.Ordinal)) return false;
        if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static bool CheckOn(bool noticeEnabled, bool checkSetting, string? envValue) =>
        noticeEnabled && checkSetting && !EnvDisablesCheck(envValue);

    /// <summary>A whole number from 1 to 8760 is kept; anything else (or null) is 24.</summary>
    public static int NormalizeIntervalHours(object? raw)
    {
        long value;
        if (raw is long l) value = l;
        else if (raw is int i) value = i;
        else return DefaultIntervalHours;
        return value >= 1 && value <= MaxIntervalHours ? (int)value : DefaultIntervalHours;
    }
}
