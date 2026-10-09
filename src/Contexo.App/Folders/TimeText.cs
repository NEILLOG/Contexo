namespace Contexo.App.Folders;

/// <summary>Plain Traditional Chinese wording for durations shown on the folder screens.</summary>
public static class TimeText
{
    /// <summary>Roughly how long the first build takes, assuming one second per file: "約 10 分鐘", "約 1～2 小時".</summary>
    public static string FirstBuildEstimate(int fileCount, bool atLeast = false)
    {
        var text = EstimateCore(Math.Max(fileCount, 0));
        return atLeast ? "至少 " + (text.StartsWith("約 ") ? text[2..] : text) : text;
    }

    private static string EstimateCore(int seconds)
    {
        if (seconds < 60)
        {
            return "不到 1 分鐘";
        }

        if (seconds < 3600)
        {
            var minutes = (int)Math.Ceiling(seconds / 60.0);
            if (minutes >= 10)
            {
                minutes = (int)(Math.Round(minutes / 5.0, MidpointRounding.AwayFromZero) * 5);
            }

            return minutes >= 60 ? "約 1 小時" : $"約 {minutes} 分鐘";
        }

        var hours = seconds / 3600.0;
        return hours <= 24 ? Range(hours, "小時") : Range(hours / 24.0, "天");

        static string Range(double value, string unit)
        {
            var low = (int)Math.Floor(value);
            var high = (int)Math.Ceiling(value);
            return low == high ? $"約 {low} {unit}" : $"約 {low}～{high} {unit}";
        }
    }

    /// <summary>"不到 1 分鐘", "42 分鐘", "2 小時 10 分鐘".</summary>
    public static string Remaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
        {
            return "不到 1 分鐘";
        }

        var totalMinutes = (int)Math.Round(remaining.TotalMinutes);
        if (totalMinutes < 60)
        {
            return $"{totalMinutes} 分鐘";
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours >= 24)
        {
            return $"{hours / 24} 天以上";
        }

        return minutes == 0 ? $"{hours} 小時" : $"{hours} 小時 {minutes} 分鐘";
    }
}
