using Microsoft.AspNetCore.Mvc;

namespace DevDeck.Web.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Dashboard", new { area = "Manage" });
}
