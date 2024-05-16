using System.Globalization;
using System.Text.RegularExpressions;

namespace UnturnedModdingCollective.Util;
public static class TimeUtility
{
    public static Regex TimeRegex { get; } = new Regex(@"([\d\.]+)\s{0,1}([a-z]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a timespan string in the form '3d 4hr 21min etc'. Can also be 'perm[anent]'.
    /// </summary>
    /// <returns>Total amount of time. <see cref="Timeout.InfiniteTimeSpan"/> is returned if <paramref name="input"/> is permanent.</returns>
    public static TimeSpan ParseTimespan(string input)
    {
        if (input.StartsWith("perm", StringComparison.OrdinalIgnoreCase))
            return Timeout.InfiniteTimeSpan;

        if (int.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out int mins) && mins > -1)
            return TimeSpan.FromMinutes(mins);

        TimeSpan time = TimeSpan.Zero;
        foreach (Match match in TimeRegex.Matches(input))
        {
            if (match.Groups.Count != 3) continue;

            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double t))
                continue;

            string key = match.Groups[2].Value;

            if (key.StartsWith("ms", StringComparison.OrdinalIgnoreCase))
                time += TimeSpan.FromMilliseconds(t);
            else if (key.StartsWith("s", StringComparison.OrdinalIgnoreCase))
                time += TimeSpan.FromSeconds(t);
            else if (key.StartsWith("mo", StringComparison.OrdinalIgnoreCase))
                time += TimeSpan.FromSeconds(t * 2565000); // 29.6875 days (356.25 / 12)
            else if (key.StartsWith("m", StringComparison.OrdinalIgnoreCase))
                time += TimeSpan.FromMinutes(t);
            else if (key.StartsWith("h", StringComparison.OrdinalIgnoreCase))
                time += TimeSpan.FromHours(t);
            else if (key.StartsWith("d", StringComparison.OrdinalIgnoreCase))
                time += TimeSpan.FromDays(t);
            else if (key.StartsWith("w", StringComparison.OrdinalIgnoreCase))
                time += TimeSpan.FromDays(t * 7);
            else if (key.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                time += TimeSpan.FromDays(t * 365.25);
        }
        return time;
    }
}
