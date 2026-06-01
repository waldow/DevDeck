using System.ComponentModel.DataAnnotations;

namespace DevDeck.Web.Areas.Manage.ViewModels;

public sealed class ServiceEditViewModel
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(64)]
    public string ServiceType { get; set; } = "Custom";

    [Required]
    public string WorkingDirectory { get; set; } = string.Empty;

    [Required]
    public string StartCommand { get; set; } = string.Empty;

    public string? StartArguments { get; set; }
    public string? StopCommand { get; set; }
    public string? StopArguments { get; set; }
    public string? Url { get; set; }
    public int? Port { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; } = false;
    public bool UseExternalInstance { get; set; } = false;
    public int? ExternalPort { get; set; }
    public int DisplayOrder { get; set; } = 0;

    public List<EnvVarEditRow> EnvironmentVariables { get; set; } = new();
    public List<HealthCheckEditRow> HealthChecks { get; set; } = new();
}

public sealed class EnvVarEditRow
{
    public const string SecretPlaceholder = "********";

    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public bool Delete { get; set; }
}

public sealed class HealthCheckEditRow
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public int ExpectedStatusCode { get; set; } = 200;
    public int IntervalSeconds { get; set; } = 10;
    public bool Enabled { get; set; } = true;
    public bool Delete { get; set; }
}
