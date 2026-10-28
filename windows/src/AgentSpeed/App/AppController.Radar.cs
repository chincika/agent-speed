using System.Windows;
using AgentSpeed.Radar;
using AgentSpeed.Theme;

namespace AgentSpeed.App;

internal sealed partial class AppController
{
    private RadarService _radar = null!;
    private RadarWindow? _radarWindow;

    private void ShowRadar()
    {
        if (_quitting) return;
        _dashboard?.Dismiss();
        if (_radarWindow is null)
        {
            _radarWindow = new RadarWindow(_radar) { Language = AppFonts.XmlLanguageFor(_loc.LanguageCode) };
            _radarWindow.Closed += (_, _) => _radarWindow = null;
        }
        if (!_radarWindow.IsVisible) _radarWindow.Show();
        if (_radarWindow.WindowState == WindowState.Minimized) _radarWindow.WindowState = WindowState.Normal;
        _radarWindow.Activate();
        _radar.CheckDue();
    }
}
