namespace DevDeck.Web.Data.Entities;

public sealed class LaunchProfile
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    public List<LaunchProfileService> Services { get; set; } = [];
}
