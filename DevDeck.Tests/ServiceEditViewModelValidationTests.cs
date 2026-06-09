using System.ComponentModel.DataAnnotations;
using DevDeck.Web.Areas.Manage.ViewModels;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class ServiceEditViewModelValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Out_of_range_port_fails_validation(int port)
    {
        var model = ValidModel();
        model.Port = port;

        Validate(model).Should().Contain(r => r.MemberNames.Contains(nameof(ServiceEditViewModel.Port)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5050)]
    [InlineData(65535)]
    [InlineData(null)]
    public void Valid_or_absent_port_passes_validation(int? port)
    {
        var model = ValidModel();
        model.Port = port;

        Validate(model).Should().NotContain(r => r.MemberNames.Contains(nameof(ServiceEditViewModel.Port)));
    }

    [Fact]
    public void Out_of_range_external_port_fails_validation()
    {
        var model = ValidModel();
        model.ExternalPort = 99999;

        Validate(model).Should().Contain(r => r.MemberNames.Contains(nameof(ServiceEditViewModel.ExternalPort)));
    }

    [Theory]
    [InlineData(99, 10)]
    [InlineData(600, 0)]
    public void Health_check_row_rejects_invalid_status_code_or_interval(int expectedStatusCode, int intervalSeconds)
    {
        var row = new HealthCheckEditRow
        {
            Url = "http://localhost:5050/health",
            ExpectedStatusCode = expectedStatusCode,
            IntervalSeconds = intervalSeconds,
        };

        Validate(row).Should().NotBeEmpty();
    }

    [Fact]
    public void Negative_start_delay_fails_validation()
    {
        var row = new ProfileServiceRow { DevServiceId = 1, ServiceName = "svc", StartDelaySeconds = -5 };

        Validate(row).Should().Contain(r => r.MemberNames.Contains(nameof(ProfileServiceRow.StartDelaySeconds)));
    }

    private static ServiceEditViewModel ValidModel() => new()
    {
        Name = "svc",
        ServiceType = "Custom",
        WorkingDirectory = "C:\\src\\svc",
        StartCommand = "dotnet",
    };

    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }
}
