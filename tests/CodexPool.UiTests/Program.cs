using CodexPool.Floating;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failed = 0;
        void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch { failed++; Console.WriteLine("FAIL " + name); }
        }
        Run("Either quota window exhausted marks the account, unknown alone does not", () =>
        {
            Check(QuotaIndicator.IsExhausted(0, 70));
            Check(QuotaIndicator.IsExhausted(80, 0));
            Check(QuotaIndicator.IsExhausted(null, 0));
            Check(QuotaIndicator.IsExhausted(0, null));
            Check(!QuotaIndicator.IsExhausted(null, null));
            Check(!QuotaIndicator.IsExhausted(40, 70));
        });
        Run("Full quota renders a full circle rather than an empty arc", () =>
        {
            var full = QuotaIndicator.Ring(100, 44, 3.5);
            var almost = QuotaIndicator.Ring(99, 44, 3.5);
            var partial = QuotaIndicator.Ring(50, 44, 3.5);
            var pen = new Pen(Brushes.Blue, 3.5);
            Check(full.Bounds.Width > 40 && full.Bounds.Height > 40);
            Check(full.GetWidenedPathGeometry(pen).GetArea() >= almost.GetWidenedPathGeometry(pen).GetArea());
            Check(partial.GetWidenedPathGeometry(pen).GetArea() < full.GetWidenedPathGeometry(pen).GetArea() * .6);
            Check(QuotaIndicator.Ring(0, 44, 3.5).IsEmpty());
            Check(QuotaIndicator.Ring(double.NaN, 44, 3.5).IsEmpty());
        });
        Console.WriteLine($"{2 - failed}/2 passed; no windows, credentials or proxy processes opened");
        return failed == 0 ? 0 : 1;
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException(); }
}
