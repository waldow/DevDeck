using DevDeck.Web.Services.Commands;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class CommandPresetProviderTests
{
    [Fact]
    public void AzureFunction_preset_defaults_to_local_storage()
    {
        var provider = new CommandPresetProvider(new CommandExecutableResolver(isWindows: false));

        var preset = provider.Find("AzureFunction");

        preset.Should().NotBeNull();
        preset!.DefaultEnvironment.Should().ContainSingle(env =>
            env.Key == "AzureWebJobsStorage" &&
            env.Value == "UseDevelopmentStorage=true");
    }
}
