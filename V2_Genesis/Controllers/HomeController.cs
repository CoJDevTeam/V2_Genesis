using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using V2_Genesis.Data;
using V2_Genesis.Services.PropertySearch;
using V2_Genesis.Models;
using V2_Genesis.Models.Rates;
using V2_Genesis.Models.ViewModels.Home;
using V2_Genesis.Services;
using V2_Genesis.Services.Interfaces;


namespace GenesisV2.Controllers;

public class HomeController : Controller
{
    private readonly IAnnouncementService _announcement;
    private readonly DisclaimerSettings _disclaimer;
    private readonly ValuationRollSettings _roll;
    private readonly IHomeSearchService _homeSearchService;
    private readonly ILogger<HomeController> _logger;
    private readonly IPropertyRateCalculatorService _rateCalculator;
    private readonly ApplicationDbContext _db;
    private readonly RollDatesSettings _rollDates;
    public HomeController(
        IAnnouncementService announcement,
        IOptions<DisclaimerSettings> disclaimerOpts,
        IOptions<ValuationRollSettings> rollOpts,
        IHomeSearchService homeSearchService,
        ILogger<HomeController> logger,
        IPropertyRateCalculatorService rateCalculator,
        ApplicationDbContext db,
        IOptions<RollDatesSettings> rollDatesOpts)
    {
        _db = db;
        _rollDates = rollDatesOpts.Value;
        _announcement = announcement;
        _disclaimer = disclaimerOpts.Value;
        _roll = rollOpts.Value;
        _homeSearchService = homeSearchService;
        _logger = logger;
        _rateCalculator = rateCalculator;
    }
    [HttpGet]
    [Route("error")]
    [AllowAnonymous]
    public IActionResult Error()
    {
        var exceptionFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();

        if (exceptionFeature?.Error is not null)
        {
            _logger.LogError(
                exceptionFeature.Error,
                "Unhandled request error on {Path}",
                exceptionFeature.Path);
        }

        return View("~/Views/Shared/Error.cshtml", new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }
    [HttpGet]
    [AllowAnonymous]
    [Route("help/user-manual")]
    public IActionResult UserManual()
    {
        return View();
    }

    public IActionResult Contact()
    {
        return View();
    }
    public IActionResult FAQ()
    {
        return View();
    }
    public IActionResult Privacy()
    {
        return View();
    }
    [HttpGet]
    [Route("/")]
    public async Task<IActionResult> Index()
    {
        // Authenticated users → their dashboard
        if (User.Identity?.IsAuthenticated == true)
        {
            return User.IsInRole("Admin")
                ? RedirectToAction("Index", "Admin")
                : RedirectToAction("Index", "Dashboard");
        }

        var showDisclaimer = !Request.Cookies.ContainsKey(_disclaimer.CookieName);

        var vm = new LandingViewModel
        {
            Announcement = _announcement.GetAnnouncement(),
            Disclaimer = _disclaimer,
            Roll = _roll,
            ShowDisclaimer = showDisclaimer,
            RollDates = _rollDates.Dates
        };

        // Same roll order as the client dashboard: valuation rolls by ID,
        // Section 78 Query/Review last.
        try
        {
            vm.Rolls = (await _db.GvList.AsNoTracking().OrderBy(r => r.ID).ToListAsync())
                .OrderBy(r => r.IsQuery)
                .ThenBy(r => r.ID)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Home] Could not load valuation rolls for the public dashboard.");
        }

        return View(vm);
    }

    [HttpPost]
    [Route("accept-disclaimer")]
    public IActionResult AcceptDisclaimer()
    {
        Response.Cookies.Append(
            _disclaimer.CookieName,
            "1",
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddHours(_disclaimer.CookieExpiryHours),
                HttpOnly = true,
                SameSite = SameSiteMode.Lax
            });

        return Ok();
    }

    // ════════════════════════════════════════════════════════
    //  ADD TO HomeController.cs
    //  Inject IHomeSearchService in constructor
    // ════════════════════════════════════════════════════════

    [HttpGet]
    [AllowAnonymous]
    [Route("home/townships")]
    public async Task<IActionResult> GetTownships()
    {
        var (towns, schemes) = await _homeSearchService.GetTownsAndSchemesAsync();

        _logger.LogInformation(
            "[HomeController] /home/townships → {T} towns, {S} schemes",
            towns.Count, schemes.Count);

        return Json(new
        {
            towns = V2_Genesis.Helpers.NaturalSort.Order(towns),
            schemes = V2_Genesis.Helpers.NaturalSort.Order(schemes)
        });
    }

    // ── POST /home/search ────────────────────────────────────────────────
    // Searches all rolls and returns the results partial.
    [HttpPost]
    [AllowAnonymous]
    [Route("home/search")]
    public async Task<IActionResult> Search(
        string? SearchTownName,
        string? SearchStand,
        string? SearchAddress,
        string? SearchScheme,
        string? SearchUnit)
    {
        // At least one field required
        if (string.IsNullOrWhiteSpace(SearchTownName) &&
            string.IsNullOrWhiteSpace(SearchStand) &&
            string.IsNullOrWhiteSpace(SearchAddress) &&
            string.IsNullOrWhiteSpace(SearchScheme) &&
            string.IsNullOrWhiteSpace(SearchUnit))
        {
            return Content(string.Empty);
        }

        var p = new HomeSearchParams
        {
            SearchTownName = SearchTownName?.Trim(),
            SearchStand = SearchStand?.Trim(),
            SearchAddress = SearchAddress?.Trim(),
            SearchScheme = SearchScheme?.Trim(),
            SearchUnit = SearchUnit?.Trim(),
        };

        var results = await _homeSearchService.SearchAllRollsAsync(p);

        ViewBag.IsAuthenticated = User.Identity?.IsAuthenticated == true;

        if (!results.Any())
            return PartialView("_HomeSearchNoResults");

        return PartialView("_HomeSearchResults", results);
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("home/rates/tariffs")]
    public async Task<IActionResult> GetRateTariffs(
    CancellationToken cancellationToken)
    {
        var tariffs = await _rateCalculator
            .GetActiveTariffsAsync(cancellationToken);

        return Json(tariffs.Select(x => new
        {
            financialYearId = x.FinancialYearId,
            financialYear = x.FinancialYear.FinancialYear,
            categoryCode = x.CategoryCode,
            categoryName = x.CategoryName,
            ratio = x.Ratio,
            annualTariff = x.AnnualTariff,
            isZeroRated = x.IsZeroRated,
            isMultipurpose = x.IsMultipurpose,
            isPenaltyTariff = x.IsPenaltyTariff
        }));
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [Route("home/rates/calculate")]
    public async Task<IActionResult> CalculatePossibleRates(
    [FromForm] PossibleRateCalculationRequest request,
    CancellationToken cancellationToken)
    {
        try
        {
            var result = await _rateCalculator.CalculateAsync(
                request,
                cancellationToken);

            return Json(new
            {
                success = true,
                data = result
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                success = false,
                message = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new
            {
                success = false,
                message = ex.Message
            });
        }
    }
}