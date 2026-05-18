using System.ComponentModel.DataAnnotations;

namespace DevDeck.Web.Areas.Manage.ViewModels;

public sealed class ProfileEditViewModel
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; } = 0;

    public List<ProfileServiceRow> Services { get; set; } = new();

    public List<ServiceOption> AvailableServices { get; set; } = new();
}

public sealed class ProfileServiceRow
{
    public int DevServiceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public int StartOrder { get; set; } = 0;
    public int StartDelaySeconds { get; set; } = 0;
    public bool Include { get; set; } = true;
}
