using System.Text.Json;

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

    private const int MaxListedMessages = 20;
    private const int MaxMessageLength = 300;

    /// <summary>
    /// Warnings then errors, serialized for TempData. TempData lives in a cookie, and every
    /// request to the host carries it until the next page reads it: an uncapped list (one
    /// message per rejected route, some quoting user input) can outgrow the server's request
    /// header limit and lock the whole UI out with HTTP 431. So keep it short.
    /// </summary>
    public string ToTempDataMessages()
    {
        var all = Warnings.Concat(Errors).ToList();
        var listed = all
            .Take(MaxListedMessages)
            .Select(m => m.Length <= MaxMessageLength ? m : m[..MaxMessageLength] + "…")
            .ToList();
        if (all.Count > MaxListedMessages)
        {
            listed.Add($"…and {all.Count - MaxListedMessages} more.");
        }
        return JsonSerializer.Serialize(listed);
    }
}
