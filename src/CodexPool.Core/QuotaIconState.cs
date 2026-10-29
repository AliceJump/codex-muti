using System.Globalization;

namespace CodexPool.Core;

// Shared by the tray renderer and the declarative Quick Settings presentation.
public sealed record QuotaIconState(double? Remaining, bool Exhausted)
{
    public bool Known => Remaining is { } value && double.IsFinite(value);
    public double Amount => Known ? Math.Clamp(Remaining!.Value, 0, 100) : 0;
    public uint AccentArgb => Exhausted || Known && Amount < 15 ? 0xFFFF7069
        : !Known ? 0xFF96A1B1 : Amount < 30 ? 0xFFFFC65F : 0xFF75DBAF;
    public string Label => Known ? Amount.ToString("0", CultureInfo.InvariantCulture) : "—";
}
