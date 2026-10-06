using System.Globalization;

namespace QuotaDuck.App.Presentation;

public static class TimeText
{
    /// <summary>"less than a minute", "5 min", "2 h 14 m", "3 d 4 h" — always rounded DOWN so a countdown never promises more time than there is.</summary>
    public static string Humanize(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        if (t < TimeSpan.FromMinutes(1))
        {
            return "less than a minute";
        }

        if (t < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes} min");
        }

        if (t < TimeSpan.FromDays(1))
        {
            int h = (int)t.TotalHours;
            int m = t.Minutes;
            return m == 0 ? string.Create(CultureInfo.InvariantCulture, $"{h} h") : string.Create(CultureInfo.InvariantCulture, $"{h} h {m} m");
        }

        int d = (int)t.TotalDays;
        int hh = t.Hours;
        return hh == 0 ? string.Create(CultureInfo.InvariantCulture, $"{d} d") : string.Create(CultureInfo.InvariantCulture, $"{d} d {hh} h");
    }

    /// <summary>Local clock time of a reset: "14:32", or "Sat 14:32" when it is more than a day away.</summary>
    public static string LocalReset(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(at, zone);
        return at - now > TimeSpan.FromDays(1)
            ? local.ToString("ddd HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Percent(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
