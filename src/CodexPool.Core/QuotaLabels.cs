using System.Globalization;

namespace CodexPool.Core;

public static class QuotaLabels
{
    public static string Duration(int? seconds) => seconds switch
    {
        null or <= 0 => "额度窗口",
        604800 => "周",
        var value when value % 86400 == 0 => (value.Value / 86400).ToString(CultureInfo.InvariantCulture) + "d",
        var value when value % 3600 == 0 => (value.Value / 3600).ToString(CultureInfo.InvariantCulture) + "h",
        var value when value % 60 == 0 => (value.Value / 60).ToString(CultureInfo.InvariantCulture) + "m",
        var value => value.Value.ToString(CultureInfo.InvariantCulture) + "s"
    };
}
