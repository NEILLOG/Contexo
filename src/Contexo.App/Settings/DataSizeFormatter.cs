using System.Globalization;

namespace Contexo.App.Settings;

/// <summary>Shows byte counts the way people read them: KB below 1 MB, otherwise MB or GB, one decimal ("1.8 GB").</summary>
public static class DataSizeFormatter
{
    private const double Kilo = 1024;
    private const double Mega = Kilo * 1024;
    private const double Giga = Mega * 1024;

    public static string Format(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        // Decide the unit on the rounded number, so 1,048,575 bytes reads "1.0 MB" and not "1024.0 KB".
        var (number, unit) = value switch
        {
            _ when Math.Round(value / Kilo, 1) < 1024 => (value / Kilo, "KB"),
            _ when Math.Round(value / Mega, 1) < 1024 => (value / Mega, "MB"),
            _ => (value / Giga, "GB"),
        };
        return number.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit;
    }
}
