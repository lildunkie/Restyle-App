using Restyle.Core.Engine;
using Restyle.Core.Settings;

namespace Restyle.Core.Tests;

/// <summary>
/// Read-only checks against the real Windows registry and APIs of the machine running the tests.
/// These never apply anything.
/// </summary>
public class RealMachineTests
{
    [Fact]
    public void EverySupportedSetting_CanBeReadOnThisPc()
    {
        var catalog = new SettingsCatalog(SettingContext.ForCurrentMachine());

        foreach (var setting in catalog.All.Where(s => s.IsSupported))
        {
            var value = setting.ReadJson();
            Assert.NotNull(value);
            Assert.False(string.IsNullOrWhiteSpace(setting.DescribeJson(value)), setting.Id);
            Assert.True(setting.IsSameJson(value, value), $"{setting.Id} should equal itself");
        }
    }

    [Fact]
    public void CurrentStateProducesAnEmptyPlan()
    {
        var engine = new SettingsEngine(new SettingsCatalog(SettingContext.ForCurrentMachine()), new ChangeHistory());
        Assert.True(engine.Plan(engine.Capture()).IsEmpty);
    }
}
