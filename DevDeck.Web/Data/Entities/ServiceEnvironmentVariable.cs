namespace DevDeck.Web.Data.Entities;

public sealed class ServiceEnvironmentVariable
{
    public int Id { get; set; }
    public int DevServiceId { get; set; }
    public DevService DevService { get; set; } = null!;
    public required string Key { get; set; }
    public required string Value { get; set; }
    public bool IsSecret { get; set; }
}
