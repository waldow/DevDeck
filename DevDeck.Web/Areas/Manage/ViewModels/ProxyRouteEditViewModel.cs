using System.ComponentModel.DataAnnotations;

namespace DevDeck.Web.Areas.Manage.ViewModels;

public sealed class ProxyRouteEditViewModel
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int? DevServiceId { get; set; }

    public string? DestinationUrlOverride { get; set; }

    [Required]
    public string MatchPath { get; set; } = "/{**catch-all}";

    public string? MatchHostsCsv { get; set; }

    public int Order { get; set; } = 0;

    [Required]
    public string PathTransformMode { get; set; } = "None";

    public string? PathPrefixToRemove { get; set; }
    public string? PathPrefixToAdd { get; set; }
    public string? PathSet { get; set; }

    public bool PreserveHostHeader { get; set; } = false;
    public bool AutoStartService { get; set; } = false;
    public bool RequireHealthyDestination { get; set; } = false;

    [Range(1, 600, ErrorMessage = "Timeout must be between 1 and 600 seconds.")]
    public int? TimeoutSeconds { get; set; }
    public string? AuthorizationPolicy { get; set; }
    public bool ShowOnDashboard { get; set; } = true;

    public List<ServiceOption> AvailableServices { get; set; } = new();
}

public sealed record ServiceOption(int Id, string Name, string? Url);
