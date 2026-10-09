using System.Globalization;

namespace Contexo.App.AiClients;

/// <summary>Plain Traditional Chinese wording for times shown on the AI client cards.</summary>
internal static class TimeText
{
    /// <summary>"今天 14:32", "昨天 09:05" or "10/3 08:00".</summary>
    public static string Absolute(DateTimeOffset time, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(time, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (local.Date == today)
        {
            return $"今天 {clock}";
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"昨天 {clock}";
        }

        return $"{local.Month}/{local.Day} {clock}";
    }

    /// <summary>"剛剛", "N 分鐘前", "N 小時前"; older than a day falls back to <see cref="Absolute"/>.</summary>
    public static string Relative(DateTimeOffset time, DateTimeOffset now, TimeZoneInfo zone)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "剛剛"; // Includes small clock differences (a time slightly in the future).
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} 分鐘前";
        }

        return age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} 小時前" : Absolute(time, now, zone);
    }
}
