using GenesisV2.Services.PropertySearch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using V2_Genesis.Data;
using V2_Genesis.Helpers;
using V2_Genesis.Models;
using V2_Genesis.Models.Attributes;
using V2_Genesis.Models.Lis;
using V2_Genesis.Models.LIS;
using V2_Genesis.Models.Results;
using V2_Genesis.Models.Section78;
using V2_Genesis.Services.Implementations;
using V2_Genesis.Services.Interfaces;
using V2_Genesis.Services.PropertySearch;


namespace V2_Genesis.Controllers;

[Authorize]
public class PropertySearchController : Controller
{
    private readonly IPropertySearchService _search;
    private readonly ApplicationDbContext _db;
    private readonly RollDatesSettings _rollDates;
    private readonly IConfiguration _config;
    private readonly ILisSearchService _lisSearchService;
    private readonly IOmissionService _omissionService;
    private readonly ILogger<PropertySearchController> _logger;
    private readonly IAttributesSearchService _attributesService;

    private static readonly Regex AdminPattern =
    new(@"^val\.admin(1[0-9]?|[1-9])@joburg\.org\.za$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);


    public PropertySearchController(
        IPropertySearchService search,
        ApplicationDbContext db,
      IOptions<RollDatesSettings> rollDatesOpts, IConfiguration config, ILisSearchService lisSearchService, IOmissionService omissionService, IAttributesSearchService attributesService,
  ILogger<PropertySearchController> logger)
    {
        _search = search;
        _db = db;
        _rollDates = rollDatesOpts.Value;
        _config = config;
        _logger = logger;
        _lisSearchService = lisSearchService;
        _omissionService = omissionService;
        _attributesService = attributesService;
    }
    private bool IsAdmin(string? email) =>
       !string.IsNullOrWhiteSpace(email) &&
       (
           email.Equals(
               "AdministrationEnquiries@Joburg.org.za",
               StringComparison.OrdinalIgnoreCase)
           ||
           AdminPattern.IsMatch(email)
       );

    private bool IsCurrentUserAdmin()
    {
        var email =
            User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue(ClaimTypes.Name)
            ?? User.Identity?.Name;

        return IsAdmin(email);
    }

    private static bool IsQueryRoll(string? rollSource)
    {
        return
            string.Equals(
                rollSource,
                "Query",
                StringComparison.OrdinalIgnoreCase)
            ||
            string.Equals(
                rollSource,
                "Objection_Query",
                StringComparison.OrdinalIgnoreCase);
    }

    private bool CanSearchAndLinkRoll(
        string rollSource,
        out string? message)
    {
        message = null;

        if (IsCurrentUserAdmin())
            return true;

        if (IsQueryRoll(rollSource))
            return true;

        var dates = _rollDates.For(rollSource);

        if (dates is null)
            return true;

        var now = DateTime.Now;

        if (now >= dates.OpenDate &&
            now <= dates.VisibleUntil)
        {
            return true;
        }

        if (now < dates.OpenDate)
        {
            message =
                $"The objection period has not opened yet. " +
                $"Property search and linking will be available from " +
                $"{dates.OpenDate:dd MMMM yyyy 'at' HH:mm}.";
        }
        else
        {
            message =
                $"The objection period closed on " +
                $"{dates.VisibleUntil:dd MMMM yyyy 'at' HH:mm}. " +
                $"You can no longer search and link a property for this roll.";
        }

        return false;
    }



    // Search / view only. The current roll stays searchable after its
    // objection period has closed (until the next roll opens); linking is
    // still controlled by CanSearchAndLinkRoll().
    private bool CanSearchRoll(
        string rollSource,
        out string? message)
    {
        message = null;

        if (IsCurrentUserAdmin() || IsQueryRoll(rollSource))
            return true;

        if (RollDatesSettings.CanSearch(_rollDates.Dates, rollSource, DateTime.Now))
            return true;

        var dates = _rollDates.For(rollSource);

        message = dates is not null && DateTime.Now < dates.OpenDate
            ? $"The objection period has not opened yet. Property search will be available from {dates.OpenDate:dd MMMM yyyy 'at' HH:mm}."
            : $"The objection period for this roll closed on {dates?.VisibleUntil:dd MMMM yyyy 'at' HH:mm}. Property search is no longer available for this roll.";

        return false;
    }

    private static string ResolveReviewStatus(
        DateTime? reviewCloseDate)
    {
        /*
         * Current business rule:
         *
         * NULL Review_Close_Date:
         *     Initial Section 78 Query is open.
         *
         * Today or future date:
         *     Review period is open.
         *
         * Past date:
         *     Review period is closed.
         */
        // No close date = not a Section 78 Review -> Query.
        if (!reviewCloseDate.HasValue)
            return Section78ReviewStatus.Query;

        return reviewCloseDate.Value.Date >= DateTime.Today
            ? Section78ReviewStatus.Open
            : Section78ReviewStatus.Closed;
    }

    private static void ApplySection78ReviewStatus(
        IEnumerable<PropertyDetailResult> properties)
    {
        foreach (var property in properties)
        {
            // IndexSection78Property now returns Review_Status
            // (Query / Open / Closed). Keep it; only a past close date
            // may still turn an Open review into Closed.
            if (Section78ReviewStatus.IsOpen(property.Review_Status) &&
                property.Review_Close_Date.HasValue &&
                property.Review_Close_Date.Value.Date < DateTime.Today)
            {
                property.Review_Status = Section78ReviewStatus.Closed;
            }
            else if (string.IsNullOrWhiteSpace(property.Review_Status))
            {
                property.Review_Status =
                    ResolveReviewStatus(
                        property.Review_Close_Date);
            }
        }
    }
    // ── GET /search/{rollSource} ──────────────────────────────────────
    // Signed-out visitors may search and view properties while the roll's
    // objection period is open. Linking still requires sign-in.
    [HttpGet]
    [AllowAnonymous]
    [Route("search/{rollSource}")]
    public async Task<IActionResult> Index(string rollSource)
    {
        // ─────────────────────────────────────────────────────────────
        // 1. Resolve selected valuation roll
        // ─────────────────────────────────────────────────────────────
        var roll = await _db.GvList
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Source == rollSource);

        if (roll is null)
        {
            return NotFound(
                $"Roll '{rollSource}' not found.");
        }

        // ─────────────────────────────────────────────────────────────
        // 2. Make sure this roll has Property Search configuration
        // ─────────────────────────────────────────────────────────────
        if (!RollSearchRegistry.Configs.ContainsKey(rollSource))
        {
            return NotFound(
                $"No search configuration found for '{rollSource}'.");
        }

        // ─────────────────────────────────────────────────────────────
        // 3. Check whether clients may search this roll.
        //    After the objection period the current roll can still be
        //    searched and viewed (not linked) — see CanSearchRoll().
        // ─────────────────────────────────────────────────────────────
        if (!CanSearchAndLinkRoll(rollSource, out _))
        {
            var closedDates = _rollDates.For(rollSource);
            ViewBag.PeriodClosedMessage =
                (closedDates is null
                    ? "The objection period for this roll has closed."
                    : $"The objection period closed on {closedDates.VisibleUntil:dd MMMM yyyy 'at' HH:mm}.")
                + " You can still search and view property details, but properties can no longer be linked.";
        }

        if (!CanSearchRoll(
                rollSource,
                out var periodMessage))
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                // Signed-out visitor: back to the public home page.
                TempData["NoticeError"] = periodMessage;
                return RedirectToAction("Index", "Home");
            }

            TempData["PropertySearchPeriodMessage"] =
                periodMessage;

            TempData["LinkError"] =
                periodMessage;

            return RedirectToAction(
                "Index",
                "Dashboard",
                new
                {
                    openRoll = rollSource
                });
        }

        // ─────────────────────────────────────────────────────────────
        // 4. Load ONLY townships belonging to the selected roll
        //
        //    GV     -> GV township SP
        //    Sup1   -> Sup1 township SP
        //    Sup2   -> Sup2 township SP
        //    Sup3   -> Sup3 township SP
        //    Sup4   -> Sup4 township SP
        //
        //    Schemes are NOT loaded here.
        //    They are loaded after the client selects a township.
        // ─────────────────────────────────────────────────────────────
        var townships =
            await _search.GetTownshipsAsync(
                rollSource);

        // ─────────────────────────────────────────────────────────────
        // 5. Populate page data
        // ─────────────────────────────────────────────────────────────
        ViewBag.Roll =
            roll;

        ViewBag.Townships =
            townships;

        // Scheme list deliberately starts empty.
        // JavaScript will call:
        // /search/{rollSource}/schemes?township=...
        ViewBag.Schemes =
            new List<string>();

        ViewBag.GvList =
            await _db.GvList
                .AsNoTracking()
                .OrderBy(r => r.ID)
                .ToListAsync();

        return View(
            new PropertySearchParams());
    }

    // ── POST /search/{rollSource} — returns partial (AJAX) ───────────
    [HttpPost]
    [AllowAnonymous]
    [Route("search/{rollSource}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Search(string rollSource, PropertySearchParams @params)
    {
        var roll = await _db.GvList
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Source == rollSource, HttpContext.RequestAborted);

        if (!ModelState.IsValid || roll is null)
            return PartialView("_NoResults", roll);

        // Server-side protection: a client must not be able to keep using an
        // already-open search page after the objection period has closed.
        // Admin users and Section 78 Query / Review are allowed by
        // CanSearchAndLinkRoll().
        if (!CanSearchRoll(
                rollSource,
                out var periodMessage))
        {
            _logger.LogWarning(
                "Blocked property search outside objection period. User={User}, Roll={Roll}",
                User.Identity?.Name,
                rollSource);

            var safeMessage =
                System.Net.WebUtility.HtmlEncode(
                    periodMessage
                    ?? "The objection period is closed. You can no longer search and link a property for this roll.");

            return Content(
                $@"<div class=""alert alert-warning m-3"" role=""alert"">
                        <i class=""fa-solid fa-calendar-xmark me-2""></i>
                        <strong>Property Search Unavailable</strong>
                        <div class=""mt-1"">{safeMessage}</div>
                   </div>",
                "text/html");
        }

        var started = System.Diagnostics.Stopwatch.StartNew();

        // Preserve the exact criteria used for the roll search before doing
        // any database work. If the selected roll search fails, the client
        // must still be able to continue to LIS / Omission with the same
        // criteria instead of being trapped by an HTTP 500 response.
        ViewBag.Params = @params;
        ViewBag.Roll = roll;

        try
        {
            var results = await _search.SearchAsync(
                rollSource,
                @params,
                HttpContext.RequestAborted);

            started.Stop();

            _logger.LogInformation(
                "Property search completed in {ElapsedMs} ms. Roll={Roll}, Results={Count}, Town={Town}, Stand={Stand}, Address={Address}, Scheme={Scheme}, Unit={Unit}",
                started.ElapsedMilliseconds,
                rollSource,
                results.Count,
                @params.TownName,
                @params.Stand,
                @params.Address,
                @params.Scheme,
                @params.Unit);

            if (results.Any())
                return PartialView("_Results", results);

            // A valid search with no roll match must continue through the
            // existing LIS / Omission flow.
            return PartialView("_NoResults", roll);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            started.Stop();

            _logger.LogError(
                ex,
                "Property roll search failed after {ElapsedMs} ms. Roll={Roll}, Town={Town}, Stand={Stand}, Address={Address}, Scheme={Scheme}, Unit={Unit}. Returning LIS fallback.",
                started.ElapsedMilliseconds,
                rollSource,
                @params.TownName,
                @params.Stand,
                @params.Address,
                @params.Scheme,
                @params.Unit);

            // Do not return HTTP 500 here. The roll databases are legacy and
            // can fail independently. The business flow requires the user to
            // continue to LIS and, when applicable, Omission.
            ViewBag.SearchMessage =
                "The selected valuation roll could not return a match. You can continue by searching the Land Information System (LIS) using the same Township and search values.";

            return PartialView("_NoResults", roll);
        }
    }


    [HttpGet]
    [Route("property/view")]
    [AllowAnonymous]
    public async Task<IActionResult> ViewProperty(
      string rollSource,
      string unitKey,
      string valuationKey,
      string? propertyFrom)
    {
        if (string.IsNullOrWhiteSpace(rollSource))
            return BadRequest("Roll source is required.");

        propertyFrom = string.IsNullOrWhiteSpace(propertyFrom)
            ? rollSource
            : propertyFrom.Trim();

        unitKey = NormalizeKey(unitKey) ?? string.Empty;
        valuationKey = NormalizeKey(valuationKey) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(unitKey) &&
            string.IsNullOrWhiteSpace(valuationKey))
        {
            return BadRequest("A unit key or valuation key is required.");
        }

        var isUniversalSearch = propertyFrom.Equals(
            "UniversalSearch",
            StringComparison.OrdinalIgnoreCase);

        // ============================================================
        // ATTRIBUTES FLOW
        // Attributes is NOT in GvList, so do not check GvList
        // ============================================================
        if (rollSource.Equals("Attributes", StringComparison.OrdinalIgnoreCase))
        {
            var attrItem = await _attributesService.GetPropertyDetailAsync(unitKey);

            if (attrItem == null)
                return NotFound("Attribute property details not found.");

            HttpContext.Session.SetString("UnitKey", unitKey);
            HttpContext.Session.SetString("ValuationKey", valuationKey);
            HttpContext.Session.SetString("RollSource", "Attributes");
            HttpContext.Session.SetString("PropertyFrom", "Attributes");

            ViewBag.GvList = await _db.GvList
                .OrderBy(r => r.ID)
                .ToListAsync();

            var attrVm = new PropertyDetailViewModel
            {
                Items = new List<PropertyDetailResult>
            {
                MapAttributePropertyToResult(attrItem)
            },
                Roll = null,
                IsAttributes = true,
                IsLis = false
            };

            return View(attrVm);
        }

        // ============================================================
        // LIS FLOW
        // Property was not found on the roll, but found on LIS.
        // Do NOT call normal roll property detail SP here.
        // ============================================================
        if (propertyFrom.Equals("LIS", StringComparison.OrdinalIgnoreCase))
        {
            var roll = await _db.GvList
                .FirstOrDefaultAsync(r => r.Source == rollSource);

            if (roll is null)
                return NotFound($"Roll '{rollSource}' not found.");

            var lisItem = await _lisSearchService.GetPropertyByKeysAsync(
                rollSource,
                unitKey,
                valuationKey);

            if (lisItem == null)
                return NotFound("LIS property details not found.");

            HttpContext.Session.SetString("UnitKey", unitKey);
            HttpContext.Session.SetString("ValuationKey", valuationKey);
            HttpContext.Session.SetString("RollSource", rollSource);
            HttpContext.Session.SetString("PropertyFrom", "LIS");

            if (IsQueryRoll(rollSource))
            {
                var reviewStatus =
                    ResolveReviewStatus(
                        lisItem.ReviewCloseDate);

                HttpContext.Session.SetString(
                    "ReviewStatus",
                    reviewStatus);

                if (lisItem.ReviewCloseDate.HasValue)
                {
                    HttpContext.Session.SetString(
                        "ReviewCloseDate",
                        lisItem.ReviewCloseDate.Value
                            .ToString(
                                "yyyy-MM-dd",
                                CultureInfo.InvariantCulture));
                }
                else
                {
                    HttpContext.Session.Remove(
                        "ReviewCloseDate");
                }
            }

            ViewBag.GvList = await _db.GvList
                .OrderBy(r => r.ID)
                .ToListAsync();

            var dates = _rollDates.For(rollSource);

            var mappedLisProperty =
      MapLisPropertyToResult(lisItem);

            if (IsQueryRoll(rollSource))
            {
                mappedLisProperty.Review_Status =
                    ResolveReviewStatus(
                        mappedLisProperty.Review_Close_Date);
            }

            var lisItems = new List<PropertyDetailResult> { mappedLisProperty };
            await _search.ApplyLisLeaseFlagsAsync(rollSource, lisItems);

            var lisVm = new PropertyDetailViewModel
            {
                Items = lisItems,

                Roll = roll,

                OpenDate = IsQueryRoll(rollSource)
                    ? null
                    : dates?.OpenDate,

                VisibleUntil = IsQueryRoll(rollSource)
                    ? null
                    : dates?.VisibleUntil,

                IsAttributes = false,
                IsLis = true
            };

            return View(lisVm);
        }

        // ============================================================
        // NORMAL ROLL FLOW
        // GV23 / SUPP / QUERY etc.
        // ============================================================
        var normalRoll = await _db.GvList
            .FirstOrDefaultAsync(r => r.Source == rollSource);

        if (normalRoll is null)
            return NotFound($"Roll '{rollSource}' not found.");

        var items = await _search.GetPropertyDetailsAsync(
      rollSource,
      unitKey,
      valuationKey);

        if (!items.Any())
            return NotFound("Property details not found.");

        if (IsQueryRoll(rollSource))
        {
            ApplySection78ReviewStatus(items);
        }

        if (!items.Any())
            return NotFound("Property details not found.");

        HttpContext.Session.SetString("UnitKey", unitKey);
        HttpContext.Session.SetString("ValuationKey", valuationKey);
        HttpContext.Session.SetString("RollSource", rollSource);
        HttpContext.Session.SetString(
            "PropertyFrom",
            isUniversalSearch ? "UniversalSearch" : rollSource);

        if (isUniversalSearch)
        {
            // Global Search is a public, read-only property lookup.
            // Do not pass owner information to its view model.
            foreach (var item in items)
            {
                item.OwnerName = null;
            }
        }

        ViewBag.GvList = await _db.GvList
            .OrderBy(r => r.ID)
            .ToListAsync();

        var rollDates = _rollDates.For(rollSource);

        var vm = new PropertyDetailViewModel
        {
            Items = items,
            Roll = normalRoll,
            OpenDate = IsQueryRoll(rollSource)
                ? null
                : rollDates?.OpenDate,

            VisibleUntil = IsQueryRoll(rollSource)
                ? null
                : rollDates?.VisibleUntil,
            IsAttributes = false,
            IsLis = false,
            IsUniversalSearch = isUniversalSearch,
            CanViewOwnerDetails = !isUniversalSearch &&
                User.Identity?.IsAuthenticated == true
        };

        return View(vm);
    }
    private static PropertyDetailResult MapLisPropertyToResult(
        LisProperty property)
    {
        return new PropertyDetailResult
        {
            TownNameDesc =
                property.TownNameDescription,

            PropertyDesc =
                !string.IsNullOrWhiteSpace(
                    property.PropertyDescription)
                    ? property.PropertyDescription
                    : BuildLisPropertyDescription(property),

            LisStreetAddress =
                property.LisStreetAddress,

            Erf =
                property.Erf,

            Ptn =
                property.Ptn.ToString(),

            Re =
                string.IsNullOrWhiteSpace(property.Re)
                    ? "-"
                    : property.Re,

            CatDesc =
                property.CATDescription,

            RateableArea =
                property.RateableArea,

            MarketValue =
                property.MarketValue,

            SchemeName =
                property.SchemeName,

            SchemeNumber =
                property.SchemeNumber,

            SchemeYear =
                property.SchemeYear,

            UnitNo =
                int.TryParse(
                    property.UnitNo,
                    out var unitNumber)
                        ? unitNumber
                        : 0,

            Reason =
                property.Reason,

            UnitKey =
                property.UnitKey,

            ValuationKey =
                property.ValuationKey,

            WefDate =
                property.ValuationEffectiveDateWefDate,

            AdditionalNotes =
                property.AdditionalNotes,

            ValuationDate =
                property.ValuationEndDate,

            OwnerName =
                property.OwnerName,

            PremiseId =
                property.PremiseId,

            PropertyId =
                property.PropertyId,

            Sector =
                property.Sector,

            /*
             * Section 78 Review information.
             */
            Review_Close_Date =
                property.ReviewCloseDate,

            Review_Status =
                ResolveReviewStatus(
                    property.ReviewCloseDate)
        };
    }
    private static string BuildLisPropertyDescription(V2_Genesis.Models.Lis.LisProperty l)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(l.Re))
            parts.Add(l.Re);

        if (l.Ptn > 0)
            parts.Add($"PORTION {l.Ptn}");

        if (l.Erf > 0)
            parts.Add($"ERF {l.Erf}");

        if (!string.IsNullOrWhiteSpace(l.TownNameDescription))
            parts.Add(l.TownNameDescription);

        if (!string.IsNullOrWhiteSpace(l.SchemeName))
            parts.Add(l.SchemeName);

        if (!string.IsNullOrWhiteSpace(l.UnitNo))
            parts.Add($"UNIT {l.UnitNo}");

        return parts.Any()
            ? string.Join(" ", parts)
            : "LIS Property";
    }
    private static string NormalizeKey(object? value)
    {
        if (value == null)
            return string.Empty;

        var key = value.ToString()?.Trim();

        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        // Fix broken scientific notation like "1.05735e 007"
        key = Regex.Replace(
            key,
            @"([eE])\s+([+-]?\d+)",
            "$1+$2");

        // Convert scientific notation / float / decimal into plain whole-number string
        if (decimal.TryParse(
            key,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var decimalValue))
        {
            return Math.Round(decimalValue, 0)
                .ToString("0", CultureInfo.InvariantCulture);
        }

        return key;
    }

    private static PropertyDetailResult MapAttributePropertyToResult(
       LisPropertyDetail d)
    {
        return new PropertyDetailResult
        {
            // ── Identifiers ───────────────────────────────────────
            UnitKey = d.UnitKey,           // ← CRITICAL for link form
            ValuationKey = d.ValuationKey,
            Id = d.UnitKey.ToString(),
            PremiseId = d.PremiseId,

            // ── Property ──────────────────────────────────────────
            PropertyDesc = d.PropertyDesc,
            TownNameDesc = d.TownNameDesc,
            Erf = d.Erf,
            Ptn = d.Ptn,
            Re = d.Re,
            LisStreetAddress = d.LisStreetAddress,
            OwnerName = d.OwnerName,
            LeaseDesc = null,

            // ── Valuation ─────────────────────────────────────────
            CatDesc = d.CatDesc,
            RateableArea = d.RateableAreaVal ?? d.RateableArea,
            MarketValue = d.MarketValue,
            WefDate = d.WefDate,
            ValuationDate = d.ValuationDate,
            Reason = d.Reason,

            // ── Scheme ────────────────────────────────────────────
            SchemeName = d.SchemeName,
            SchemeNumber = d.SchemeNumber,
            SchemeYear = d.SchemeYear,
            UnitNo = d.UnitNo,
        };
    }

    [HttpGet]
    [Route("property/save")]
    [Authorize]
    public async Task<IActionResult> SaveRecord(
     string rollSource,
     string? key,
     string? sourceTable,
     string? propertyFrom,
     string? unitKey,
     string? valuationKey,
     string? propertyId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        _logger.LogInformation(
            "SaveRecord called. User={UserId}, Roll={Roll}, Key={Key}, SourceTable={SourceTable}, PropertyFrom={PropertyFrom}, UnitKey={UnitKey}, ValuationKey={ValuationKey}, PropertyId={PropertyId}",
            userId, rollSource, key, sourceTable, propertyFrom, unitKey, valuationKey, propertyId);

        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        // Any link started before sign-in is now being completed.
        PendingPropertyLink.Clear(HttpContext);

        if (string.IsNullOrWhiteSpace(rollSource))
        {
            TempData["LinkError"] = "Property could not be linked because the valuation roll is missing.";
            return RedirectToAction("Index", "Dashboard");
        }

        rollSource = rollSource.Trim();

        propertyFrom = string.IsNullOrWhiteSpace(propertyFrom)
            ? sourceTable
            : propertyFrom.Trim();

        var isLis = string.Equals(
        propertyFrom,
        "LIS",
        StringComparison.OrdinalIgnoreCase);

        var isAdmin = IsCurrentUserAdmin();

        // Final server-side link protection. This prevents a client from
        // linking a property from an old/bookmarked property page after the
        // objection period has closed. Admin users and Query / Review remain
        // allowed through CanSearchAndLinkRoll().
        if (!CanSearchAndLinkRoll(
                rollSource,
                out var periodMessage))
        {
            TempData["PropertySearchPeriodMessage"] =
                periodMessage;

            TempData["LinkError"] =
                periodMessage;

            _logger.LogWarning(
                "Blocked property link outside objection period. User={UserId}, Roll={Roll}, PropertyFrom={PropertyFrom}, Key={Key}, PropertyId={PropertyId}, UnitKey={UnitKey}, ValuationKey={ValuationKey}",
                userId,
                rollSource,
                propertyFrom,
                key,
                propertyId,
                unitKey,
                valuationKey);

            return RedirectToAction(
                "Index",
                "Dashboard",
                new
                {
                    openRoll = rollSource
                });
        }

        try
        {
            // LIS: link the ONE unit the client chose (unit key + valuation key),
            // not the Property ID – all units of a scheme share that Property ID.
            var idProperty = isLis
                ? V2_Genesis.Helpers.LisLinkKey.Build(NormalizeKey(unitKey), NormalizeKey(valuationKey))
                  ?? FirstNotEmpty(propertyId, key, unitKey, valuationKey)
                : key;

            if (string.IsNullOrWhiteSpace(idProperty))
            {
                TempData["LinkError"] = "Property could not be linked because the property key is missing.";

                _logger.LogWarning(
                    "Property link failed. Missing key. User={UserId}, Roll={Roll}, PropertyFrom={PropertyFrom}, Key={Key}, PropertyId={PropertyId}, UnitKey={UnitKey}, ValuationKey={ValuationKey}",
                    userId,
                    rollSource,
                    propertyFrom,
                    key,
                    propertyId,
                    unitKey,
                    valuationKey);

                return AfterLinkRedirect(rollSource, isAdmin);
            }

            var linkPropertyFrom = isLis
                ? "LIS"
                : sourceTable ?? rollSource;

            // Section 78: send the keys too, so the property is found
            // even if the search and link tables use different IDs.
            var linkUnitKey = FirstNotEmpty(
                NormalizeKey(unitKey),
                NormalizeKey(HttpContext.Session.GetString("UnitKey")));

            var linkValuationKey = FirstNotEmpty(
                NormalizeKey(valuationKey),
                NormalizeKey(HttpContext.Session.GetString("ValuationKey")));

            var result = await _search.LinkPropertyAsync(
                rollSource: rollSource,
                idProperty: idProperty,
                userId: userId,
                propertyFrom: linkPropertyFrom,
                unitKey: linkUnitKey,
                valuationKey: linkValuationKey);

            if (result.Success)
            {
                var isQuery =
                    IsQueryRoll(rollSource);

                if (isQuery)
                {
                    TempData["ReviewStatus"] =
                        result.ReviewStatus
                        ?? Section78ReviewStatus.Query;

                    if (result.ReviewCloseDate.HasValue)
                    {
                        TempData["ReviewCloseDate"] =
                            result.ReviewCloseDate.Value
                                .ToString(
                                    "dd MMMM yyyy",
                                    CultureInfo.GetCultureInfo("en-ZA"));
                    }

                    TempData["LinkSuccess"] =
                        Section78ReviewStatus.IsClosed(result.ReviewStatus)
                            ? "Property linked successfully. This property is on the Section 78 review list, but the review period has closed."
                            : Section78ReviewStatus.IsOpen(result.ReviewStatus)
                                ? "Property linked successfully. This property is on the Section 78 review list — you can Lodge a Review from your dashboard."
                                : "Property linked successfully. You can Lodge a Query for this property from your dashboard.";
                }
                else
                {
                    TempData["LinkSuccess"] = isLis
                        ? "LIS property successfully linked to your profile."
                        : "Property successfully linked to your profile.";
                }

                _logger.LogInformation(
                    "User {UserId} linked property {PropertyKey} from {Roll}. " +
                    "PropertyFrom={PropertyFrom}, PropertyId={PropertyId}, " +
                    "UnitKey={UnitKey}, ValuationKey={ValuationKey}, " +
                    "ReviewStatus={ReviewStatus}, ReviewCloseDate={ReviewCloseDate}",
                    userId,
                    idProperty,
                    rollSource,
                    linkPropertyFrom,
                    propertyId,
                    unitKey,
                    valuationKey,
                    result.ReviewStatus,
                    result.ReviewCloseDate);
            }
            else
            {
                TempData["LinkError"] =
                    result.ErrorMessage
                    ?? "The property could not be linked.";
            }

        }
        catch (Exception ex)
        {
            TempData["LinkError"] = "An error occurred while linking the property. Please try again.";

            _logger.LogError(
                ex,
                "Error linking property. User={UserId}, Roll={Roll}, PropertyFrom={PropertyFrom}, Key={Key}, PropertyId={PropertyId}, UnitKey={UnitKey}, ValuationKey={ValuationKey}",
                userId,
                rollSource,
                propertyFrom,
                key,
                propertyId,
                unitKey,
                valuationKey);
        }

        return AfterLinkRedirect(rollSource, isAdmin);
    }

    // ── GET /property/link-after-login ───────────────────────────────
    // "Link Property" for a signed-out visitor. Remembers the link, sends
    // them to Sign In (or Register), and after sign-in the link is completed
    // automatically. Signed-in users go straight to property/save.
    [HttpGet]
    [AllowAnonymous]
    [Route("property/link-after-login")]
    public async Task<IActionResult> LinkAfterLogin(
        string rollSource,
        string? key,
        string? sourceTable,
        string? propertyFrom,
        string? unitKey,
        string? valuationKey,
        string? propertyId,
        string? desc,
        bool register = false)
    {
        if (string.IsNullOrWhiteSpace(rollSource))
            return RedirectToAction("Index", "Home");

        rollSource = rollSource.Trim();

        var saveUrl = "/property/save" + QueryString.Create(
            new Dictionary<string, string?>
            {
                ["rollSource"] = rollSource,
                ["key"] = key,
                ["sourceTable"] = sourceTable,
                ["propertyFrom"] = propertyFrom,
                ["unitKey"] = unitKey,
                ["valuationKey"] = valuationKey,
                ["propertyId"] = propertyId
            }
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new KeyValuePair<string, string?>(x.Key, x.Value!.Trim()))).ToUriComponent();

        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(saveUrl);

        if (!CanSearchAndLinkRoll(rollSource, out var periodMessage))
        {
            TempData["NoticeError"] = periodMessage;
            return RedirectToAction("Index", "Home");
        }

        var roll = await _db.GvList
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Source == rollSource);

        PendingPropertyLink.Save(HttpContext, new PendingPropertyLink
        {
            Url = saveUrl,
            PropertyDescription = string.IsNullOrWhiteSpace(desc) ? null : desc.Trim(),
            RollSource = rollSource,
            RollName = roll?.Name ?? roll?.Short
        });

        _logger.LogInformation(
            "Signed-out visitor started a property link. Roll={Roll}, Key={Key}, Register={Register}",
            rollSource, key, register);

        return register
            ? Redirect("/register?returnUrl=" + Uri.EscapeDataString(saveUrl))
            : Redirect("/login?returnUrl=" + Uri.EscapeDataString(saveUrl));
    }

    // ── GET /property/pending-link/dismiss ────────────────────────────
    [HttpGet]
    [AllowAnonymous]
    [Route("property/pending-link/dismiss")]
    public IActionResult DismissPendingLink(string? returnUrl)
    {
        PendingPropertyLink.Clear(HttpContext);

        return !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");
    }

    // After linking, go straight to the roll's detail page (Linked
    // Properties section) so the client sees the property and the
    // Lodge Query / Lodge Review button. The old "/dashboard?openRoll="
    // is ignored by the new dashboard landing page.
    private IActionResult AfterLinkRedirect(string rollSource, bool isAdmin)
    {
        if (isAdmin)
        {
            return RedirectToAction("Index", "Admin", new
            {
                openRoll = rollSource
            });
        }

        return LocalRedirect(
            "/dashboard/roll-detail/" +
            Uri.EscapeDataString(rollSource) +
            "?section=linked");
    }

    private static string? FirstNotEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
    [HttpPost]
    [AllowAnonymous]
    [Route("search/{rollSource}/lis")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SearchLis(
    string rollSource,
    string? SearchTownName,
    string? SearchStand,
    string? SearchAddress,
    string? SearchScheme,
    string? SearchUnit,
    string? SearchOwner)
    {
        // Section 78 Query / Review works only on properties that are on the
        // roll: there is no LIS search (and no omission) for it. Blocks a
        // direct call to /search/Objection_Query/lis as well.
        if (IsQueryRoll(rollSource))
        {
            return Content(
                @"<div class=""alert alert-warning m-3"" role=""alert"">
                        <i class=""fa-solid fa-circle-info me-2""></i>
                        <strong>LIS search is not available for Section 78</strong>
                        <div class=""mt-1"">A Section 78 Query or Review can only be lodged for a property on the valuation roll. Please check the township, stand, address, scheme or unit you entered and search again.</div>
                   </div>",
                "text/html");
        }

        // LIS is part of the same property-search/linking journey. Do not let
        // a client bypass a closed objection period by calling the LIS endpoint
        // directly. Admin users and Section 78 Query / Review are exempt.
        if (!CanSearchRoll(
                rollSource,
                out var periodMessage))
        {
            _logger.LogWarning(
                "Blocked LIS search outside objection period. User={User}, Roll={Roll}",
                User.Identity?.Name,
                rollSource);

            var safeMessage =
                System.Net.WebUtility.HtmlEncode(
                    periodMessage
                    ?? "The objection period is closed. You can no longer search and link a property for this roll.");

            return Content(
                $@"<div class=""alert alert-warning m-3"" role=""alert"">
                        <i class=""fa-solid fa-calendar-xmark me-2""></i>
                        <strong>LIS Search Unavailable</strong>
                        <div class=""mt-1"">{safeMessage}</div>
                   </div>",
                "text/html");
        }

        var p = new LisSearchParams
        {
            SearchTownName = SearchTownName,
            SearchStand = SearchStand,
            SearchAddress = SearchAddress,
            SearchScheme = SearchScheme,
            SearchUnit = SearchUnit,
            SearchOwner = SearchOwner,
        };

        var lisResults = await _lisSearchService.SearchAsync(rollSource, p);
        _logger.LogInformation(
    "[LIS] Search returned {Count} records for Roll={Roll}, Town={Town}, Stand={Stand}, Address={Address}, Scheme={Scheme}, Unit={Unit}",
    lisResults.Count(),
    rollSource,
    SearchTownName,
    SearchStand,
    SearchAddress,
    SearchScheme,
    SearchUnit);

        var firstLis = lisResults.FirstOrDefault();

        if (firstLis != null)
        {
            _logger.LogInformation(
                "[LIS] First result: {@FirstLis}",
                firstLis);
        }

        if (!lisResults.Any())
        {
            // Load towns + schemes in parallel so the omission form
            // is ready without a second round-trip
            // FIX: use _search (DefaultConnection + Objection.dbo SPs) — same source
            //      as the home and property search pages. OmissionService was connecting
            //      to the roll-specific DB (e.g. Sup3) which doesn't have those SPs.
            var townsTask = _search.GetTownshipsAsync(rollSource);
            var schemesTask = _search.GetSchemesAsync();
            await Task.WhenAll(townsTask, schemesTask);

            var roll = await _db.GvList
                .FirstOrDefaultAsync(r => r.Source == rollSource);

            ViewBag.RollSource = rollSource;
            ViewBag.RollName = roll?.Name ?? rollSource;
            ViewBag.Towns = townsTask.Result;
            ViewBag.Schemes = schemesTask.Result;
            ViewBag.SearchParams = p;          // pre-fill editable fields

            return PartialView("_LisNoResults");
        }

        // Map and return results in the standard partial
        var mapped = lisResults
     .Select(property => new PropertySearchResult
     {
         TownNameDesc =
             property.TownNameDescription,

         LisStreetAddress =
             property.LisStreetAddress,

         Erf =
             property.Erf,

         Ptn =
             property.Ptn,

         Re =
             property.Re,

         CatDesc =
             property.CATDescription,

         RateableArea =
             property.RateableArea,

         MarketValue =
             property.MarketValue,

         SchemeName =
             property.SchemeName,

         SchemeNumber =
             property.SchemeNumber,

         SchemeYear =
             property.SchemeYear,

         Lease =
             property.Lease,

         UnitNo =
             int.TryParse(
                 property.UnitNo,
                 out var unitNumber)
                     ? unitNumber
                     : 0,

         Reason =
             property.Reason,

         UnitKey =
             property.UnitKey,

         ValuationKey =
             property.ValuationKey,

         Review_Close_Date =
             property.ReviewCloseDate
     })
     .ToList();

        var rollRecord = await _db.GvList
            .FirstOrDefaultAsync(r => r.Source == rollSource);

        ViewBag.Roll = rollRecord;
        ViewBag.IsLisSearch = true;

        // LEASE (Y/N): unit type FL / SL. Never fails the search.
        await _search.ApplyLisLeaseFlagsAsync(rollSource, mapped);

        return PartialView("_Results", mapped);
    }


    // ── POST /search/{rollSource}/omission ────────────────────────────────
    // Called when client confirms their details and clicks "Lodge as Omission"
    [HttpPost]
    [Authorize]
    [Route("search/{rollSource}/omission")]
    [ValidateAntiForgeryToken]
    public IActionResult SubmitOmission(
       string rollSource,
       string? propType,
       // Freehold
       string? FH_Town,
       string? FH_ERF,
       string? FH_Portion,
       string? FH_RE,
       string? FH_Right,
       string? FH_Address,
       // Sectional Title
       string? ST_Scheme,
       string? ST_SchemeNumber,
       string? ST_SchemeYear,
       string? ST_Unit,
       string? ST_Right)
    {
        // An omitted property cannot be lodged as a Section 78 Query / Review.
        if (IsQueryRoll(rollSource))
        {
            TempData["LinkError"] =
                "A Section 78 Query or Review can only be lodged for a property on the valuation roll. Please check your search details.";
            return RedirectToAction(nameof(Index), new { rollSource });
        }

        // Omission is also reached from the property-search journey. Prevent
        // direct POSTs after the objection period has closed.
        if (!CanSearchAndLinkRoll(
                rollSource,
                out var periodMessage))
        {
            TempData["PropertySearchPeriodMessage"] =
                periodMessage;

            TempData["LinkError"] =
                periodMessage;

            _logger.LogWarning(
                "Blocked omission submission outside objection period. User={User}, Roll={Roll}",
                User.Identity?.Name,
                rollSource);

            return RedirectToAction(
                "Index",
                "Dashboard",
                new
                {
                    openRoll = rollSource
                });
        }

        // ── Call service to build desc + resolve correct roll target ───────
        var (propertyDesc, sourceTable, controllerName) =
            _omissionService.BuildOmissionDescription(
                rollSource,
                propType ?? "FH",
                FH_Town,
                FH_ERF,
                FH_Portion,
                FH_RE,
                FH_Right,
                ST_Scheme,
                ST_SchemeNumber,
                ST_SchemeYear,
                ST_Unit,
                ST_Right);

        var town = (propType == "ST" ? FH_Town : FH_Town)?.Trim();

        _logger.LogInformation(
            "[Omission] {RollSource} → controller={Ctrl} sourceTable={St} desc={Desc}",
            rollSource, controllerName, sourceTable, propertyDesc);

        // ── Set TempData ───────────────────────────────────────────────────
        TempData["OmissionStatus"] = "True";
        TempData["OmittedPropertyDesc"] = propertyDesc;
        TempData["OmittedTownName"] = town;
        TempData["RollSource"] = rollSource;
        TempData["Omission_Address"] = FH_Address?.Trim();
        TempData["Omission_Stand"] = FH_ERF?.Trim();
        TempData["Omission_Scheme"] = propType == "ST" ? ST_Scheme?.Trim() : null;
        TempData["Omission_Unit"] = propType == "ST" ? ST_Unit?.Trim() : null;

        // ── Redirect — pass sourceTable so CheckProperty picks up the right
        //   controller and DB connection without defaulting to Sup3 ─────────
        return RedirectToAction("CheckProperty", "Objection",
            new
            {
                rollSource,
                sourceTable,          // ← now correctly set e.g. "GV23-SUP2"
                PropertyFrom = rollSource,
                omission = true
            });
    }
    [HttpGet]
    [AllowAnonymous]
    [Route("search/{rollSource}/schemes")]
    public async Task<IActionResult> GetSchemes(
    string rollSource,
    string township)
    {
        if (string.IsNullOrWhiteSpace(township))
        {
            return Json(Array.Empty<string>());
        }

        if (!RollSearchRegistry.Configs.ContainsKey(rollSource))
        {
            return NotFound();
        }

        var schemes =
            await _search.GetSchemesAsync(
                rollSource,
                township);

        return Json(V2_Genesis.Helpers.NaturalSort.Order(schemes));
    }
}