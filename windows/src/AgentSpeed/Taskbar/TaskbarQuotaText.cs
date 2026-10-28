using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AgentSpeed.Taskbar;

internal sealed record BarQuotaBlock(string FiveHourLabel, string WeekLabel, string FiveHourValue, string WeekValue)
{
    public string Spoken => $"{FiveHourLabel} {FiveHourValue} · {WeekLabel} {WeekValue}";
}

/// <summary>Two lines beside tok/s; reserves 100% so refreshed percentages cannot shift the taskbar.</summary>
internal sealed class TaskbarQuotaText
{
    public const double Gap = 8;
    public const double Height = 28;
    private const double Pitch = 13;
    private const double Size = 11;
    private readonly Typeface _face = new(new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private readonly Dictionary<(string, Color, double), FormattedText> _cache = new();

    public double Width(BarQuotaBlock block, double scale, Color primary, Color secondary) => Gap
        + Math.Max(Text(block.FiveHourLabel, secondary, scale).WidthIncludingTrailingWhitespace,
            Text(block.WeekLabel, secondary, scale).WidthIncludingTrailingWhitespace)
        + 5 + Text("100%", primary, scale).WidthIncludingTrailingWhitespace;

    public void Draw(DrawingContext dc, BarQuotaBlock block, double x, double centerY, double scale, Color primary, Color secondary)
    {
        var right = x + Width(block, scale, primary, secondary);
        DrawLine(block.FiveHourLabel, block.FiveHourValue, centerY - Pitch / 2);
        DrawLine(block.WeekLabel, block.WeekValue, centerY + Pitch / 2);

        void DrawLine(string label, string value, double y)
        {
            var caption = Text(label, secondary, scale);
            var number = Text(value, value == "—" ? secondary : primary, scale);
            dc.DrawText(caption, new Point(Snap(x + Gap), Snap(y - caption.Height / 2)));
            dc.DrawText(number, new Point(Snap(right - number.WidthIncludingTrailingWhitespace), Snap(y - number.Height / 2)));
        }

        double Snap(double value) => Math.Round(value * scale) / scale;
    }

    private FormattedText Text(string value, Color color, double scale)
    {
        var key = (value, color, scale);
        if (_cache.TryGetValue(key, out var existing)) return existing;
        if (_cache.Count > 128) _cache.Clear();
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, Size,
            new SolidColorBrush(color), null, TextFormattingMode.Ideal, scale);
        _cache[key] = text;
        return text;
    }
}
