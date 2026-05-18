using DevDeck.Web.Data.Entities;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class LaunchProfileOrderingTests
{
    [Fact]
    public void Profile_services_order_by_StartOrder()
    {
        var profile = new LaunchProfile
        {
            Name = "Full Stack",
            Services =
            {
                new LaunchProfileService { DevServiceId = 1, StartOrder = 2 },
                new LaunchProfileService { DevServiceId = 2, StartOrder = 0 },
                new LaunchProfileService { DevServiceId = 3, StartOrder = 1 },
            },
        };

        profile.Services.OrderBy(s => s.StartOrder).Select(s => s.DevServiceId)
            .Should().BeEquivalentTo(new[] { 2, 3, 1 }, opts => opts.WithStrictOrdering());
    }
}
