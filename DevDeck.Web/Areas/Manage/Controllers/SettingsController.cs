using DevDeck.Web.Data;
using DevDeck.Web.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
[Route("Manage/Settings")]
public sealed class SettingsController : Controller
{
    private readonly IOptions<DevDeckOptions> _options;

    public SettingsController(IOptions<DevDeckOptions> options)
    {
        _options = options;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        ViewBag.DatabasePath = DevDeckPaths.DatabaseFile;
        ViewBag.LogsFolder = DevDeckPaths.LogsFolder;
        return View(_options.Value);
    }
}
