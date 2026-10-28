using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentSpeed.Core.Radar;
using AgentSpeed.Theme;

namespace AgentSpeed.Radar;

internal sealed partial class RadarWindow : Window
{
    private readonly RadarService _service;
    private RadarData? _shown;
    private string? _selected;
    private readonly Dictionary<string, Button> _cells = new();
    private readonly Dictionary<string, Border> _selectionBorders = new();

    public RadarWindow(RadarService service)
    {
        InitializeComponent();
        _service = service;
        Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 48));
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 48));
        Icon = AppIconSource.Frame(32);
        _service.Changed += OnChanged;
        Refresh();
    }
    protected override void OnClosed(EventArgs e) { _service.Changed -= OnChanged; base.OnClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
    public void Relocalize() { _shown = null; Refresh(); }
    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await _service.RefreshAsync();
    private void OnSource(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://codexradar.com/#stationmaster-round") { UseShellExecute = true });

    private void OnMatrixMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // A nested ScrollViewer consumes the wheel even with vertical scrolling disabled.
        // Forward it before that class handler runs, preserving the system's wheel/page settings.
        e.Handled = true;
        PageScroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = PageScroller,
        });
    }

    private void Refresh()
    {
        RefreshButton.IsEnabled = _service.CanRefresh;
        StatusText.Text = RadarUi.Status(_service);
        if (ReferenceEquals(_shown, _service.Data) && ScoreGrid.Children.Count > 0) return;
        _shown = _service.Data;
        RecommendationsGrid.Children.Clear();
        if (_shown is { } snapshot)
            foreach (var pick in RadarRecommendations.Evaluate(snapshot))
                RecommendationsGrid.Children.Add(RadarUi.Recommendation(pick, snapshot, false, () => SelectRecommendation(pick)));
        if (RecommendationsGrid.Children.Count == 0) RecommendationsGrid.Children.Add(RadarUi.Text(RadarUi.L("emptyPicks")));
        BuildMatrix();
    }

    private void BuildMatrix()
    {
        ScoreGrid.Children.Clear(); ScoreGrid.RowDefinitions.Clear(); ScoreGrid.ColumnDefinitions.Clear();
        _cells.Clear(); _selectionBorders.Clear();
        EmptyText.Visibility = _shown is null ? Visibility.Visible : Visibility.Collapsed;
        if (_shown is not { } data) { SelectionText.Text = RadarUi.L("selectHint"); return; }
        ScoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(152) });
        foreach (var _ in data.Efforts) ScoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 155 });
        ScoreGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        Put(RadarUi.Text(RadarUi.L("modelEffort"), 11), 0, 0);
        for (var col = 0; col < data.Efforts.Length; col++)
        {
            var label = RadarUi.Text(data.Efforts[col].ToUpperInvariant(), 11, strong: true, resource: "TextFillColorSecondaryBrush");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Put(label, 0, col + 1);
        }
        for (var row = 0; row < data.Models.Length; row++)
        {
            ScoreGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(84) });
            var label = RadarUi.Text(data.Models[row].Name, 13, strong: true);
            label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 12, 6);
            Put(label, row + 1, 0);
            for (var col = 0; col < data.Efforts.Length; col++)
            {
                var score = data.Scores.FirstOrDefault(s => s.Model == data.Models[row].Id && s.Effort == data.Efforts[col]);
                Put(score is null ? Missing() : Cell(score, data), row + 1, col + 1);
            }
        }
        CountText.Text = RadarUi.Count(data) + $" · / {data.ScoreMax:0.##}";
        SourceDateText.Text = RadarUi.L("sourceShort") + " · " + RadarUi.L("usdNote")
            + (string.IsNullOrEmpty(data.RoundId) ? "" : " · " + data.RoundId)
            + (DateTimeOffset.TryParse(data.SourceUpdated, out var stamp) ? " · " + RadarUi.L("sourceUpdated") + " " + stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "");
        if (_selected is not null && _cells.ContainsKey(_selected)) Select(_selected);
        else { _selected = null; SelectionText.Text = RadarUi.L("selectHint"); }
    }

    private Button Cell(RadarScore score, RadarData data)
    {
        var ratio = score.Score / data.ScoreMax;
        var color = ratio >= .88 ? "#B0D344" : ratio >= .80 ? "#69B660" : ratio >= .74 ? "#2D9D73"
            : ratio >= .68 ? "#288977" : ratio >= .60 ? "#285F73" : "#263F5B";
        var foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : RadarUi.Brush(ratio >= .68 ? "#06100D" : "#FFFFFF");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var value = RadarUi.Text(score.Score.ToString("0.00", CultureInfo.InvariantCulture), 23, strong: true);
        value.FontFamily = new FontFamily("Cascadia Mono, Consolas"); value.Foreground = foreground;
        value.VerticalAlignment = VerticalAlignment.Center; value.HorizontalAlignment = HorizontalAlignment.Center;
        value.TextWrapping = TextWrapping.NoWrap;
        grid.Children.Add(value);
        var meta = new Grid(); meta.RowDefinitions.Add(new RowDefinition()); meta.RowDefinitions.Add(new RowDefinition());
        var cost = Meta(score.CostUsd is { } c ? "$" + c.ToString("0.00", CultureInfo.InvariantCulture) : "—", foreground);
        var time = Meta(score.Seconds is { } s ? (s / 60).ToString("0.##", CultureInfo.InvariantCulture) + "m" : "—", foreground);
        var costBorder = new Border { BorderBrush = RadarUi.Brush("#30203020"), BorderThickness = new Thickness(1, 0, 0, 1), Child = cost };
        var timeBorder = new Border { BorderBrush = RadarUi.Brush("#30203020"), BorderThickness = new Thickness(1, 0, 0, 0), Child = time };
        meta.Children.Add(costBorder); Grid.SetRow(timeBorder, 1); meta.Children.Add(timeBorder);
        Grid.SetColumn(meta, 1); grid.Children.Add(meta);
        var selected = new Border { BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(5), Child = grid };
        var name = data.Models.First(m => m.Id == score.Model).Name + " " + score.Effort;
        var button = new Button { Content = selected, Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : RadarUi.Brush(color), BorderBrush = Brushes.Transparent,
            Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Cursor = Cursors.Hand, ToolTip = name + " · " + score.Score.ToString("0.00", CultureInfo.InvariantCulture) + "/" + data.ScoreMax + " · " + RadarUi.L("usdNote") };
        button.SetResourceReference(StyleProperty, "RadarCardButton");
        AutomationProperties.SetName(button, name + ", " + value.Text + ", " + cost.Text + ", " + time.Text);
        var key = score.Model + ":" + score.Effort;
        button.Click += (_, _) => Select(key);
        _cells[key] = button; _selectionBorders[key] = selected;
        return button;
    }

    private static TextBlock Meta(string text, Brush foreground)
    {
        var block = RadarUi.Text(text, 10, strong: true);
        block.Foreground = foreground; block.HorizontalAlignment = HorizontalAlignment.Center;
        block.VerticalAlignment = VerticalAlignment.Center; block.TextWrapping = TextWrapping.NoWrap;
        return block;
    }
    private static Border Missing() => new() { Margin = new Thickness(0, 0, 7, 7), BorderThickness = new Thickness(1),
        BorderBrush = RadarUi.Brush("#50587771"), CornerRadius = new CornerRadius(6),
        Child = new TextBlock { Text = "—", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = RadarUi.L("notTested") } };
    private void Put(UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); ScoreGrid.Children.Add(element); }
    private void Select(string key)
    {
        _selected = key;
        foreach (var (id, border) in _selectionBorders) border.BorderBrush = id == key
            ? SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brushes.White : Brushes.Transparent;
        if (_shown is not { } data || data.Scores.FirstOrDefault(s => s.Model + ":" + s.Effort == key) is not { } score) return;
        var name = data.Models.First(m => m.Id == score.Model).Name + " " + score.Effort.ToUpperInvariant();
        SelectionText.Text = name + $"  ·  {score.Score:0.00} / {data.ScoreMax:0.##}  ·  "
            + RadarUi.L("breakdown") + $"  {score.Combined:0.##} / {score.Pompeii:0.##} / {score.Pelican:0.##} / {score.Candy:0.##}";
    }
    private void SelectRecommendation(RadarRecommendation pick)
    {
        var score = pick.Score;
        var key = score.Model + ":" + score.Effort;
        Select(key); _cells[key].BringIntoView(); _cells[key].Focus();
    }
}
