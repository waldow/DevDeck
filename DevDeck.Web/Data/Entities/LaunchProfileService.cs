namespace DevDeck.Web.Data.Entities;

public sealed class LaunchProfileService
{
    public int LaunchProfileId { get; set; }
    public LaunchProfile LaunchProfile { get; set; } = null!;
    public int DevServiceId { get; set; }
    public DevService DevService { get; set; } = null!;
    public int StartOrder { get; set; } = 0;
    public int StartDelaySeconds { get; set; } = 0;
}
