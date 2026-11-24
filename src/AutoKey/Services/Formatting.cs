namespace AutoKey.Services;

internal static class Formatting
{
    /// <summary>A short human-readable duration such as "45 s", "3 min 20 s" or "1 h 5 min".</summary>
    public static string Duration(TimeSpan time)
    {
        double seconds = Math.Ceiling(Math.Max(0, time.TotalSeconds));
        if (seconds < 60)
        {
            return $"{Math.Max(1, seconds):0} s";
        }

        var rounded = TimeSpan.FromSeconds(seconds);
        if (rounded.TotalMinutes < 10)
        {
            return rounded.Seconds == 0 ? $"{rounded.Minutes} min" : $"{rounded.Minutes} min {rounded.Seconds} s";
        }

        if (rounded.TotalHours < 1)
        {
            return $"{Math.Round(rounded.TotalMinutes):0} min";
        }

        return $"{(int)rounded.TotalHours} h {rounded.Minutes} min";
    }

    /// <summary>Shortens text to <paramref name="maxLength"/> characters, adding an ellipsis.</summary>
    public static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1).TrimEnd(), "…");
}
