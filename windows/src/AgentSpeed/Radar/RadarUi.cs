using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AgentSpeed.Core.Radar;
using AgentSpeed.Localization;

namespace AgentSpeed.Radar;

internal static class RadarUi
{
    public static string L(string key) => LocalizationSource.Instance["win.radar." + key];
    public static string Count(RadarData data) => string.Format(CultureInfo.CurrentCulture, L("modelsEfforts"), data.Models.Length, data.Scores.Length);
    public static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
    public static TextBlock Text(string text, double size = 12, bool strong = false, string? resource = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
        block.SetResourceReference(TextBlock.ForegroundProperty, resource ?? "TextFillColorPrimaryBrush");
        return block;
    }
    public static string Status(RadarService service)
    {
        var status = !service.NetworkEnabled ? L("offline") : service.IsChecking ? L("checking")
            : service.Failed ? L("failed") : service.Cache.LastSuccess is not null ? L("every3h") : L("waiting");
        if (service.Cache.LastAttempt is { } at) status += " · " + L("checked") + " " + at.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        return status;
    }
    public static Button Recommendation(RadarRecommendation pick, RadarData data, bool compact, Action open)
    {
        var category = L(pick.Kind.ToString().ToLowerInvariant());
        var modelName = data.Models.First(m => m.Id == pick.Score.Model).Name + " " + pick.Score.Effort.ToUpperInvariant();
        var color = pick.Kind switch { RadarTaskKind.Hard => "#B0D344", RadarTaskKind.Daily => "#38BCAD", _ => "#70A9ED" };
        var text = new StackPanel();
        text.Children.Add(Text(category, compact ? 11 : 12, resource: "TextFillColorSecondaryBrush"));
        var model = Text(modelName, compact ? 13 : 16, strong: true);
        model.Margin = new Thickness(0, compact ? 5 : 8, 0, 0);
        text.Children.Add(model);
        var facts = Text($"{pick.Score.Score:0.00} / {data.ScoreMax:0.##}  ·  "
            + (pick.Score.CostUsd is { } c ? "$" + c.ToString("0.00", CultureInfo.InvariantCulture) : "—") + "  ·  "
            + (pick.Score.Seconds is { } s ? (s / 60).ToString("0.##", CultureInfo.InvariantCulture) + "m" : "—"),
            compact ? 10 : 11, resource: "TextFillColorSecondaryBrush");
        facts.Margin = new Thickness(0, 5, 0, 0); text.Children.Add(facts);
        if (!compact)
        {
            var rule = Text(L(pick.Kind.ToString().ToLowerInvariant() + "Rule"), 11, resource: "TextFillColorSecondaryBrush");
            rule.Margin = new Thickness(0, 9, 0, 0); text.Children.Add(rule);
        }
        var border = new Border { BorderThickness = new Thickness(0, 2, 0, 0), BorderBrush = Brush(color),
            Padding = new Thickness(compact ? 9 : 14, 9, compact ? 9 : 14, 11), Child = text };
        var button = new Button { Content = border, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            MinHeight = compact ? 72 : 86, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = L("openScores") };
        button.SetResourceReference(FrameworkElement.StyleProperty, "RadarCardButton");
        AutomationProperties.SetName(button, category + ": " + modelName + ". " + facts.Text + ". " + L("openScores"));
        button.Click += (_, _) => open();
        return button;
    }
}
