using DevDeck.Web.Data.Entities;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class DevServiceTests
{
    private static DevService NewService() => new()
    {
        Name = "svc",
        ServiceType = "AzureFunction",
        WorkingDirectory = "C:\\work",
        StartCommand = "func",
    };

    [Fact]
    public void EffectivePort_is_the_run_port_when_managed()
    {
        var s = NewService();
        s.Port = 7072;
        s.ExternalPort = 7071;
        s.UseExternalInstance = false;

        s.EffectivePort.Should().Be(7072);
    }

    [Fact]
    public void EffectivePort_is_the_external_port_in_passthru_mode()
    {
        var s = NewService();
        s.Port = 7072;
        s.ExternalPort = 7071;
        s.UseExternalInstance = true;

        s.EffectivePort.Should().Be(7071);
    }

    [Fact]
    public void EffectivePort_falls_back_to_run_port_when_external_port_unset()
    {
        var s = NewService();
        s.Port = 7071;
        s.ExternalPort = null;
        s.UseExternalInstance = true;

        s.EffectivePort.Should().Be(7071);
    }
}
