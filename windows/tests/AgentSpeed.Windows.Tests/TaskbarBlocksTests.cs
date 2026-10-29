using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.TaskScore;
using AgentSpeed.Taskbar;

namespace AgentSpeed.Windows.Tests;

/// <summary>
/// The taskbar widget with its readout blocks (DESIGN 10, Menu bar and taskbar): the network block and the intelligence
/// block each add their fixed width, alone or together, and the widget never changes width with their values.
/// </summary>
public class TaskbarBlocksTests
{
    private static readonly Localizer ZhHant = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    private static TaskbarState Widget(BarTrafficBlock? traffic, BarIntelligenceBlock? intelligence) =>
        TaskbarState.Initial with { Enabled = true, Value = 532, Traffic = traffic, Intelligence = intelligence };

    private static BarTrafficBlock Network(string rx, string tx) =>
        new(new TrafficFormat.Parts(rx, "MB/s"), new TrafficFormat.Parts(tx, "KB/s"), false, null);

    private static BarIntelligenceBlock Score(int? shown, double thinking) => BarIntelligenceBlock.Of(
        shown is null ? TaskSetReading.None : new TaskSetReading(TaskForm.Live, shown, TaskState.Passing, 1, null),
        new IntelligenceSums(thinking > 0 ? 3 * thinking : 0, 3 * thinking, 3 * thinking), ZhHant);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void BothBlocksAddTheirFixedWidthsAndNeverMoveWithTheValues(double scale)
    {
        var (plain, network, intelligence, both, others) = OnStaThread(() =>
        {
            var renderer = new TaskbarRenderer();
            int Length(TaskbarState state) => renderer.MeasureLength(state, scale, vertical: false);
            var traffic = Network("1.28", "64.0");
            var score = Score(86, 128);
            var variants = new[]
            {
                Length(Widget(Network("0", "0"), Score(null, 0))),
                Length(Widget(Network("8.88", "888"), Score(100, 8_880))),
                Length(Widget(new BarTrafficBlock(null, null, false, null), Score(7, 0.4))),
            };
            return (Length(Widget(null, null)), Length(Widget(traffic, null)), Length(Widget(null, score)), Length(Widget(traffic, score)), variants);
        });

        // Whole pixels per measure: the sum may round by one pixel.
        Assert.True(network > plain && intelligence > plain);
        Assert.InRange(both - plain, network - plain + intelligence - plain - 1, network - plain + intelligence - plain + 1);
        Assert.All(others, length => Assert.Equal(both, length));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void QuotaAndUpstreamBlocksFitTogether(double scale)
    {
        OnStaThread(() =>
        {
            var renderer = new TaskbarRenderer();
            var all = Widget(Network("1.28", "64.0"), Score(86, 128)) with
            {
                Quota = new("5 小时", "周额度", "100%", "50%"),
            };
            var withoutQuota = all with { Quota = null };
            Assert.True(renderer.MeasureLength(all, scale, false) > renderer.MeasureLength(withoutQuota, scale, false));
            Assert.True(renderer.MeasureLength(all, scale, true) > renderer.MeasureLength(withoutQuota, scale, true));
            Assert.True(all.LayoutDiffers(withoutQuota));
            Assert.True(all.LayoutDiffers(all with { Intelligence = null }));
            return 0;
        });
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result : throw new InvalidOperationException("Measuring failed.", error);
    }
}
