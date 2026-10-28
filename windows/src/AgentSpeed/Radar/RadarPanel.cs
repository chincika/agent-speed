using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AgentSpeed.Core.Radar;

namespace AgentSpeed.Radar;

internal sealed class RadarPanel : StackPanel
{
    private readonly RadarService _service;
    private readonly Action _open;
    private readonly UniformGrid _picks = new() { Columns = 1, Margin = new Thickness(0, 9, -6, 0) };
    private readonly TextBlock _description = RadarUi.Text("", 11, resource: "TextFillColorSecondaryBrush");
    private readonly TextBlock _status = RadarUi.Text("", 10, resource: "TextFillColorTertiaryBrush");
    private readonly Button _header = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 5, 0, 5) };
    private readonly TextBlock _note = RadarUi.Text("", 10, resource: "TextFillColorSecondaryBrush");
    private RadarData? _shown;

    public RadarPanel(RadarService service, Action open)
    {
        _service = service;
        _open = open;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AgentSpeed;component/Radar/RadarStyles.xaml", UriKind.Relative) });
        _header.SetResourceReference(StyleProperty, "DisclosureButton");
        _header.Click += (_, _) => _open();
        Children.Add(_header);
        Children.Add(_description);
        Children.Add(_picks);
        Children.Add(_note);
        _status.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(_status);
        Loaded += (_, _) => { _service.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => _service.Changed -= OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();
    public void Relocalize() { _shown = null; Refresh(); }

    private void Refresh()
    {
        _header.Content = RadarUi.Text(RadarUi.L("panelTitle") + "  ↗", 14, strong: true);
        _description.Text = RadarUi.L("sourceShort") + (_service.Data is { } data ? " · " + RadarUi.Count(data) : "");
        _status.Text = RadarUi.Status(_service);
        _note.Text = RadarUi.L("recommendationNote");
        if (!ReferenceEquals(_shown, _service.Data) || _picks.Children.Count == 0)
        {
            _shown = _service.Data;
            _picks.Children.Clear();
            if (_service.Data is { } snapshot)
                foreach (var pick in RadarRecommendations.Evaluate(snapshot)) _picks.Children.Add(RadarUi.Recommendation(pick, snapshot, true, _open));
            if (_picks.Children.Count == 0) _picks.Children.Add(RadarUi.Text(RadarUi.L("emptyPicks"), 11));
        }
    }
}
