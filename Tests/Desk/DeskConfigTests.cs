using Hydra.Config;
using Hydra.Desk;

namespace Tests.Desk;

public class DeskConfigTests
{
    private const string Relay = "\"embeddedStyx\":{\"server\":\"http://localhost:5000\",\"password\":\"pw\"}";

    [Test]
    public void TheControllerDecidesEachComputersRole()
    {
        var profile = new HydraConfig { Mode = Mode.Slave, Controller = "pc" };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile.ResolveMode("pc"), Is.EqualTo(Mode.Master));
            Assert.That(profile.ResolveMode("PC"), Is.EqualTo(Mode.Master), "computer names are matched case-insensitively");
            Assert.That(profile.ResolveMode("mac"), Is.EqualTo(Mode.Slave));
        }
    }

    [Test]
    public void WithoutAControllerTheExplicitModeStillWins()
    {
        var profile = new HydraConfig { Mode = Mode.Master };
        Assert.That(profile.ResolveMode("anything"), Is.EqualTo(Mode.Master));
    }

    [Test]
    public void OneDocumentCanCarryBothRolesSettingsWhenItNamesAController()
    {
        // The same text lives on every computer, so master-only and slave-only keys travel together.
        var json = $$$"""
            {"name":"mac","profiles":[{"mode":"Slave","controller":"pc",{{{Relay}}},"hideCursor":true,"mouseScale":1.5}]}
            """;
        Assert.DoesNotThrow(() => HydraConfigFile.Parse(json, "test.conf"));
    }

    [Test]
    public void WithoutAControllerTheRoleSpecificKeysAreStillRejected()
    {
        var json = $$$"""
            {"name":"mac","profiles":[{"mode":"Slave",{{{Relay}}},"hideCursor":true}]}
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfigFile.Parse(json, "test.conf"));
        Assert.That(ex!.Message, Does.Contain("hideCursor is master-only"));
    }

    [Test]
    public void AMonitorCannotBeAssignedTwiceInOneScene()
    {
        var json = $$$"""
            {"profiles":[{"mode":"Master",{{{Relay}}},"displayRouting":{"monitors":[{"monitor":"benq","host":"pc"},{"monitor":"BENQ","host":"mac"}]}}]}
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfigFile.Parse(json, "test.conf"));
        Assert.That(ex!.Message, Does.Contain("more than once"));
    }

    [Test]
    public void TheDeskMonitorTableIsValidated()
    {
        var json = $$$"""
            {"monitors":[{"id":"benq","sources":[{"host":"pc","input":300}]}],"profiles":[{"mode":"Master",{{{Relay}}}}]}
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfigFile.Parse(json, "test.conf"));
        Assert.That(ex!.Message, Does.Contain("between 0 and 255"));
    }

    [Test]
    public void APushedDeskKeepsThisMachinesIdentityAndRelay()
    {
        var local = new HydraConfigFile
        {
            Name = "mac",
            LogFile = "/tmp/mac.log",
            Profiles =
            [
                new HydraConfig
                {
                    Mode = Mode.Slave,
                    ProfileName = "Default",
                    EmbeddedStyx = new EmbeddedStyxConfig { Server = "auto://studio", Password = "a-long-enough-secret" },
                    MouseScale = 1.4m,
                },
            ],
        };
        var incoming = new HydraConfigFile
        {
            Name = "pc",
            LogFile = "/var/log/pc.log",
            Monitors = [new DeskMonitorConfig { Id = "benq", Label = "BenQ", Width = 1920, Height = 1080 }],
            Profiles =
            [
                new HydraConfig
                {
                    Mode = Mode.Master,
                    ProfileName = "Default",
                    Controller = "pc",
                    EmbeddedStyxServer = new EmbeddedStyxServerConfig { Port = 5000, Password = "a-long-enough-secret", DiscoveryName = "studio" },
                    DisplayRouting = new DisplayRoutingConfig { Monitors = [new MonitorAssignmentConfig { Monitor = "benq", Host = "pc" }] },
                },
            ],
        };

        var merged = DeskConfigStore.Merge(local, incoming);

        var profile = merged.Profiles.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Name, Is.EqualTo("mac"));
            Assert.That(merged.LogFile, Is.EqualTo("/tmp/mac.log"));
            Assert.That(merged.Monitors.Single().Id, Is.EqualTo("benq"), "the desk itself is shared");
            Assert.That(profile.Controller, Is.EqualTo("pc"));
            Assert.That(profile.DisplayRouting.Monitors.Single().Host, Is.EqualTo("pc"));
            Assert.That(profile.EmbeddedStyxServer, Is.Null, "the relay stays on the computer that runs it");
            Assert.That(profile.EmbeddedStyx?.Server, Is.EqualTo("auto://studio"));
            Assert.That(profile.MouseScale, Is.EqualTo(1.4m), "pointer tuning is per machine");
        }
    }

    [Test]
    public void AnEquivalentPushIsRecognisedSoItDoesNotCauseARestartLoop()
    {
        var file = new HydraConfigFile
        {
            Monitors = [new DeskMonitorConfig { Id = "benq", DeskX = 10, Width = 1920, Height = 1080, Sources = [new MonitorSourceConfig { Host = "pc", Input = 15 }] }],
            Profiles = [new HydraConfig { Mode = Mode.Master, ProfileName = "Default", Controller = "pc" }],
        };
        var same = new HydraConfigFile
        {
            Monitors = [new DeskMonitorConfig { Id = "benq", DeskX = 10, Width = 1920, Height = 1080, Sources = [new MonitorSourceConfig { Host = "pc", Input = 15 }] }],
            Profiles = [new HydraConfig { Mode = Mode.Slave, ProfileName = "Default", Controller = "pc" }],
        };
        var different = new HydraConfigFile
        {
            Monitors = file.Monitors,
            Profiles = [new HydraConfig { Mode = Mode.Master, ProfileName = "Default", Controller = "mac" }],
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeskConfigStore.SameDesk(file, same), Is.True);
            Assert.That(DeskConfigStore.SameDesk(file, different), Is.False);
        }
    }
}
