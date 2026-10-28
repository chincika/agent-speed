using AgentSpeed.Radar;

namespace AgentSpeed.Dashboard;

internal sealed partial class DashboardWindow
{
    private RadarPanel? _radarPanel;
    public void AttachRadar(RadarService service, Action open)
    {
        _radarPanel = new RadarPanel(service, open);
        RadarHost.Children.Clear();
        RadarHost.Children.Add(_radarPanel);
    }
    public void RelocalizeRadar() => _radarPanel?.Relocalize();
}
