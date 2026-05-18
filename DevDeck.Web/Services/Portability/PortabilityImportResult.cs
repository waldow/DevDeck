namespace DevDeck.Web.Services.Portability;

public sealed class PortabilityImportResult
{
    public string EntityName { get; set; } = string.Empty;
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];

    public bool HasErrors => Errors.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
    public int TotalAffected => Created + Updated;

    public string ToFlashMessage()
    {
        var parts = new List<string> { $"Imported {TotalAffected} {EntityName}" };
        if (Created > 0) parts.Add($"{Created} created");
        if (Updated > 0) parts.Add($"{Updated} updated");
        if (Skipped > 0) parts.Add($"{Skipped} skipped");
        if (Warnings.Count > 0) parts.Add($"{Warnings.Count} warning{(Warnings.Count == 1 ? "" : "s")}");
        return string.Join(" · ", parts);
    }
}
