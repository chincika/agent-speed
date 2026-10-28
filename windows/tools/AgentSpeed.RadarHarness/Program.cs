using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentSpeed.Core.Radar;
using AgentSpeed.Localization;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Radar;
using AgentSpeed.Theme;

namespace AgentSpeed.RadarHarness;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--quota", var quotaOutput]) return QuotaFlow.Run(quotaOutput);
        if (args is ["--orb-foreground", var foregroundOutput]) return OrbForegroundFlow.Run(foregroundOutput);
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        var output = args[0];
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("AGENT_SPEED_RADAR_DIR", Path.Combine(output, "cache"));
        Environment.SetEnvironmentVariable("AGENT_SPEED_LOGS_DIR", Path.Combine(output, "logs"));
        var app = new AgentSpeed.App.App();
        var loc = new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans");
        LocalizationSource.Instance.Attach(loc);
        using var theme = new ThemeService(app.Dispatcher, AppearanceMode.Dark, forceReduceMotion: true);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AgentSpeed;component/Theme/Styles.xaml", UriKind.Absolute) });
        AppFonts.Apply(app.Resources, loc.LanguageCode);
        theme.EnableFluent();
        app.Startup += async (_, _) =>
        {
            var result = 1;
            try
            {
                using var service = new RadarService(app.Dispatcher, networkEnabled: true);
                await service.RefreshAsync();
                Require(service.Data is not null && !service.Failed, "live JSON fetch");
                var data = service.Data!;
                Require(data.Scores.Length > 0 && RadarRecommendations.Evaluate(data).Length == 3, "three local recommendations");
                var window = new RadarWindow(service);
                window.Show();
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                foreach (var name in new[] { "CardBackgroundFillColorDefaultBrush", "CardStrokeColorDefaultBrush", "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "SolidBackgroundFillColorBaseBrush" })
                    Console.WriteLine(name + "=" + window.TryFindResource(name));
                var matrix = (Grid)window.FindName("ScoreGrid");
                Require(matrix.Children.OfType<Button>().Count() == data.Scores.Length, "one clickable cell per measured configuration");
                await VerifyMouseWheel(window, app.Dispatcher);
                var recs = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("RecommendationsGrid");
                Require(recs.Children.Count == 3, "three full-window cards");
                ((Button)recs.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(((TextBlock)window.FindName("SelectionText")).Text.Contains(" / ", StringComparison.Ordinal), "recommendation locates score and shows task breakdown");
                Capture((FrameworkElement)window.Content, Path.Combine(output, "radar-dark.png"));
                var opened = 0;
                var panel = new RadarPanel(service, () => { opened++; window.Activate(); });
                var host = new Window { Width = 368, SizeToContent = SizeToContent.Height, Content = new Border { Padding = new Thickness(20), Child = panel } };
                host.Show();
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                ((Button)panel.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(opened == 1, "dashboard panel opens full-window callback");
                Capture((FrameworkElement)host.Content, Path.Combine(output, "panel-dark.png"));
                var identity = service.Data;
                var changeTime = service.Cache.ChangedAt;
                await service.RefreshAsync();
                Require(ReferenceEquals(identity, service.Data) && changeTime == service.Cache.ChangedAt, "unchanged live feed retains snapshot and change time");
                var lastAttempt = service.Cache.LastAttempt;
                service.SetNetworkEnabled(false);
                await service.RefreshAsync();
                Require(!service.CanRefresh && service.Cache.LastAttempt == lastAttempt, "master switch blocks manual network request");
                using (var restored = new RadarService(app.Dispatcher, networkEnabled: false))
                    Require(restored.Data?.Fingerprint() == service.Data?.Fingerprint(), "offline restart restores valid cache");
                theme.Mode = AppearanceMode.Light;
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Capture((FrameworkElement)window.Content, Path.Combine(output, "radar-light.png"));
                Capture((FrameworkElement)host.Content, Path.Combine(output, "panel-light.png"));
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    passed = true,
                    endpoint = RadarClient.ScoresUrl.AbsoluteUri,
                    models = data.Models.Length, scores = data.Scores.Length,
                    picks = RadarRecommendations.Evaluate(data).Select(p => new { kind = p.Kind.ToString(), p.Score.Model, p.Score.Effort, p.Score.Score, p.Score.CostUsd, p.Score.Seconds }),
                    checks = new[] { "live JSON", "matrix count", "wheel over cells and labels", "wheel over recommendations", "wheel up from bottom", "horizontal scrollbar", "recommendation click", "panel callback", "unchanged data", "network switch", "offline cache", "dark and light captures" },
                }, new JsonSerializerOptions { WriteIndented = true }));
                host.Close(); window.Close();
                result = 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); }
            app.Shutdown(result);
        };
        return app.Run();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
    private static async Task VerifyMouseWheel(RadarWindow window, Dispatcher dispatcher)
    {
        var page = (ScrollViewer)window.FindName("PageScroller");
        var horizontal = (ScrollViewer)window.FindName("MatrixScroller");
        var matrix = (Grid)window.FindName("ScoreGrid");
        var cells = matrix.Children.OfType<Button>().ToArray();
        var recs = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("RecommendationsGrid");
        Require(page.ScrollableHeight > 0, "score page has vertical overflow");
        foreach (var source in new UIElement[] { cells[0], ((Grid)((Border)cells[0].Content).Child).Children[0], (Button)recs.Children[0] })
        {
            page.ScrollToTop();
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Wheel(source, -120);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(page.VerticalOffset > 0, "wheel down over " + source.GetType().Name + " scrolls the page");
        }
        page.ScrollToEnd();
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bottom = page.VerticalOffset;
        Wheel(cells[^1], 120);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(page.VerticalOffset < bottom, "wheel up over a score cell leaves the bottom");
        var originalWidth = window.Width;
        window.Width = window.MinWidth;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        horizontal.ScrollToRightEnd();
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(horizontal.HorizontalOffset > 0, "horizontal scrolling still works at minimum window width");
        horizontal.ScrollToLeftEnd();
        window.Width = originalWidth;
        page.ScrollToTop();
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Wheel(UIElement source, int delta)
    {
        // Native WPF input first tunnels PreviewMouseWheel, then bubbles MouseWheel if not handled.
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        source.RaiseEvent(preview);
        if (!preview.Handled)
            source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent });
    }
    internal static void Capture(FrameworkElement element, string file)
    {
        element.UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            dc.DrawRectangle(element.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? SystemColors.WindowBrush, null, rect);
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, rect);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 1.5), (int)Math.Ceiling(element.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }
}
