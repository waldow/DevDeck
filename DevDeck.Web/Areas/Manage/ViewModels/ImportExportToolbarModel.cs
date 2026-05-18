namespace DevDeck.Web.Areas.Manage.ViewModels;

public sealed class ImportExportToolbarModel
{
    public required string ControllerName { get; init; }
    public required string EntityLabelSingular { get; init; }
    public required string EntityLabelPlural { get; init; }
    public bool SupportsIncludeSecrets { get; init; }
}
