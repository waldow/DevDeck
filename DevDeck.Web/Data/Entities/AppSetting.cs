namespace DevDeck.Web.Data.Entities;

public sealed class AppSetting
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
}
