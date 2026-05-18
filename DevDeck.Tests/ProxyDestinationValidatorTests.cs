using DevDeck.Web.Services.Proxy;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class ProxyDestinationValidatorTests
{
    [Theory]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://127.0.0.1:5080/")]
    [InlineData("http://app.localhost:5050/")]
    [InlineData("http://10.0.1.5:3000/")]
    [InlineData("http://192.168.1.10:8000/")]
    [InlineData("http://172.20.0.4:9000/")]
    public void Local_and_private_destinations_are_allowed(string url)
    {
        var validator = new ProxyDestinationValidator(allowExternal: false);
        validator.Validate(url).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://www.google.com/api")]
    public void Public_destinations_rejected_when_flag_off(string url)
    {
        var validator = new ProxyDestinationValidator(allowExternal: false);
        var result = validator.Validate(url);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("outside localhost");
    }

    [Theory]
    [InlineData("http://example.com/")]
    public void Public_destinations_allowed_when_flag_on(string url)
    {
        var validator = new ProxyDestinationValidator(allowExternal: true);
        validator.Validate(url).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://localhost/")]
    public void Invalid_or_wrong_scheme_rejected(string url)
    {
        var validator = new ProxyDestinationValidator(allowExternal: true);
        validator.Validate(url).IsValid.Should().BeFalse();
    }
}
