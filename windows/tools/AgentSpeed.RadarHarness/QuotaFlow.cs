using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentSpeed.Core;
using AgentSpeed.Cost;
using AgentSpeed.Localization;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Quota;
using AgentSpeed.Services;
using AgentSpeed.Settings;
using AgentSpeed.Taskbar;
using AgentSpeed.Theme;
using AgentSpeed.Updates;

namespace AgentSpeed.RadarHarness;

internal static class QuotaFlow
{
    public static int Run(string output)
    {
        if (!Path.IsPathFullyQualified(output)) return 2;
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("AGENT_SPEED_SETTINGS_FILE", Path.Combine(output, "settings.json"));
        Environment.SetEnvironmentVariable("AGENT_SPEED_LOGS_DIR", Path.Combine(output, "logs"));
        var app = new AgentSpeed.App.App();
        var loc = new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans");
        LocalizationSource.Instance.Attach(loc);
        using var theme = new ThemeService(app.Dispatcher, AppearanceMode.Dark, forceReduceMotion: true);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AgentSpeed;component/Theme/Styles.xaml") });
        AppFonts.Apply(app.Resources, loc.LanguageCode);
        theme.EnableFluent();
        app.Startup += async (_, _) =>
        {
            try
            {
                var quota = await CodexQuotaClient.ReadAsync(CancellationToken.None);
                Require(quota.FiveHourRemaining is not null && quota.WeekRemaining is not null, "live local quota read");
                CaptureMatrix(quota, output);
                using var settings = new SettingsService(app.Dispatcher);
                using var preview = new PreviewService(new SpeedHub(), app.Dispatcher);
                using var updates = new UpdateService(app.Dispatcher, settings, "0.0.0");
                using var cost = new CostService(app.Dispatcher, settings, loc, "0.0.0", Path.Combine(output, "history.json"));
                var model = new SettingsViewModel(settings, preview, loc, _ => null, updates, () => { }, () => { }, () => false, () => { }, cost);
                var window = new SettingsWindow(model);
                window.ShowPage("taskbar");
                window.Show();
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var toggle = Descendants(window).OfType<CheckBox>().Single(c => c.GetBindingExpression(CheckBox.IsCheckedProperty)?.ParentBinding.Path.Path == "BarCodexQuota");
                Require(toggle.IsChecked == true, "default on binding");
                toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                toggle.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
                Require(!model.BarCodexQuota && !settings.Current.BarCodexQuota, "UI toggle off");
                Require(settings.Flush(), "save off");
                Require(!SettingsSerializer.Deserialize(File.ReadAllText(Path.Combine(output, "settings.json"))).BarCodexQuota, "reload off");
                toggle.SetCurrentValue(CheckBox.IsCheckedProperty, true);
                toggle.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
                Require(model.BarCodexQuota && settings.Current.BarCodexQuota, "UI toggle on");
                Require(settings.Flush(), "save on");
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Capture((FrameworkElement)window.Content, Path.Combine(output, "settings-quota.png"));
                window.Close();
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    passed = true, fiveHourRemaining = quota.FiveHourRemaining, weekRemaining = quota.WeekRemaining,
                    source = "local codex app-server account/rateLimits/read", refreshSeconds = CodexQuotaService.RefreshInterval.TotalSeconds,
                    checks = new[] { "live read", "100/125/150/200 percent DPI dark and light", "high contrast", "unknown placeholder", "fixed width", "settings toggle off/on", "settings persist/reload" },
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString());
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static void CaptureMatrix(CodexQuotaSnapshot quota, string output)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, 640, 548));
            var row = 0;
            foreach (var dark in new[] { true, false })
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var state = TaskbarState.Initial with { Enabled = true, Value = 42, TaskbarDark = dark,
                    Quota = new("5 小时", "周额度", CodexQuotaSnapshot.Percent(quota.FiveHourRemaining), CodexQuotaSnapshot.Percent(quota.WeekRemaining)) };
                var image = Widget(state, scale);
                dc.DrawRectangle(dark ? Brushes.Black : Brushes.White, null, new Rect(0, row * 54, 640, 52));
                dc.DrawImage(image, new Rect(16, row * 54 + 2, image.PixelWidth / scale, image.PixelHeight / scale));
                row++;
            }
            foreach (var unknown in new[] { false, true })
            {
                var state = TaskbarState.Initial with { Enabled = true, Value = 42, HighContrast = !unknown,
                    Quota = new("5 小时", "周额度", unknown ? "—" : "100%", unknown ? "—" : "100%") };
                dc.DrawRectangle(unknown ? Brushes.Black : SystemColors.WindowBrush, null, new Rect(0, row * 54, 640, 52));
                var image = Widget(state, 1);
                dc.DrawImage(image, new Rect(16, row * 54 + 2, image.PixelWidth, image.PixelHeight));
                row++;
            }
        }
        var bitmap = new RenderTargetBitmap(640, 548, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Save(bitmap, Path.Combine(output, "taskbar-quota-matrix.png"));
    }

    private static BitmapSource Widget(TaskbarState state, double scale)
    {
        var renderer = new TaskbarRenderer();
        var width = renderer.MeasureLength(state, scale, false);
        foreach (var value in new[] { "0%", "9%", "100%", "—" })
            Require(width == renderer.MeasureLength(state with { Quota = state.Quota! with { FiveHourValue = value, WeekValue = value } }, scale, false), "stable quota width");
        var height = (int)(48 * scale);
        var buffer = new byte[width * height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var scene = new BarScene();
            scene.SetTarget(state.Value, state.Tier);
            scene.Snap();
            renderer.Render(state, scene, width, height, scale, false, false, false, handle.AddrOfPinnedObject(), buffer.Length);
        }
        finally { handle.Free(); }
        return BitmapSource.Create(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, buffer, width * 4);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        var opaque = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        opaque.Render(visual);
        Save(opaque, path);
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
