// ═══════════════════════════════════════════════════════════════
//  Controllers/AdminController.cs  — REPLACE full file
// ═══════════════════════════════════════════════════════════════
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.RegularExpressions;
using V2_Genesis.Data;
using V2_Genesis.Models.Admin;
using V2_Genesis.Models.Results.Admin;
using V2_Genesis.Models.ViewModels.Admin;
using V2_Genesis.Models.ViewModels.Dashboard;
using V2_Genesis.Services;
using V2_Genesis.Services.Attributes;
using V2_Genesis.Services.Interfaces;
using V2_Genesis.Services.PropertySearch;

namespace V2_Genesis.Controllers;

public class AdminController : Controller
{
    private readonly IDashboardService _dashboardService;
    private readonly IAdminDashboardService _adminService;
    private readonly IAdminClientAccountService _adminClientAccountService;
    private readonly IAdminPropertyEnquiryService _adminPropertyEnquiryService;
    private readonly IAdminReferenceResolver _adminReferenceResolver;
    private readonly IAdminAccountInformationService _adminAccountInformationService;
    private readonly IAdminRollInformationService _adminRollInformationService;
    private readonly IAcknowledgementDownloadService _acknowledgementDownloadService;
    private readonly IAuditService _audit;
    private readonly ApplicationDbContext _db;
    private readonly RollDatesSettings _rollDates;
    private readonly IAnnouncementService _announcement;
    private readonly IAttributesDashboardService _attributesService;
    private readonly IRebatesService _rebates;
    private readonly IPropertySearchService _search;
    private readonly INoticeService _noticeService;
    private readonly IAdminFormViewService _adminFormViewService;
    private readonly ILogger<AdminController> _logger;

    private static readonly Regex AdminPattern =
        new(@"^val\.admin(1[0-9]?|[1-9])@joburg\.org\.za$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AdminController(
        IDashboardService dashboardService,
        IAdminDashboardService adminService,
        IAdminClientAccountService adminClientAccountService,
        IAdminPropertyEnquiryService adminPropertyEnquiryService,
        IAdminReferenceResolver adminReferenceResolver,
        IAdminAccountInformationService adminAccountInformationService,
        IAdminRollInformationService adminRollInformationService,
        IAcknowledgementDownloadService acknowledgementDownloadService,
        IAuditService audit,
        ApplicationDbContext db,
        IOptions<RollDatesSettings> rollDatesOpts,
        IAnnouncementService announcement,
        IAttributesDashboardService attributesService,
        IRebatesService rebates,
        IPropertySearchService search,
        INoticeService noticeService,
        IAdminFormViewService adminFormViewService,
        ILogger<AdminController> logger)
    {
        _dashboardService = dashboardService;
        _adminService = adminService;
        _adminClientAccountService = adminClientAccountService;
        _adminPropertyEnquiryService = adminPropertyEnquiryService;
        _adminReferenceResolver = adminReferenceResolver;
        _adminAccountInformationService = adminAccountInformationService;
        _adminRollInformationService = adminRollInformationService;
        _acknowledgementDownloadService = acknowledgementDownloadService;
        _audit = audit;
        _db = db;
        _adminFormViewService = adminFormViewService;
        _rollDates = rollDatesOpts.Value;
        _announcement = announcement;
        _attributesService = attributesService;
        _rebates = rebates;
        _search = search;
        _noticeService = noticeService;
        _logger = logger;
    }

    // ── Helpers ───────────────────────────────────────────────────────
    private bool IsAdmin()
    {
        return User.Identity?.IsAuthenticated == true
            && (
                User.IsInRole("Admin")
                || User.FindFirstValue("UMRole")?.Equals("Admin", StringComparison.OrdinalIgnoreCase) == true
                || !string.IsNullOrWhiteSpace(User.FindFirstValue("SAPNumber"))
            );
    }

    private string AdminEmail => User.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    private string SapNumber => User.FindFirstValue("SAPNumber") ?? HttpContext.Session.GetString("AdminSapNumber") ?? string.Empty;
    private string ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // ══════════════════════════════════════════════════════════════════
    //  GET /admin  — Dashboard
    // ══════════════════════════════════════════════════════════════════
    [HttpGet]
    [Route("admin")]
    public async Task<IActionResult> Index()
    {
        if (!IsAdmin())
            return RedirectToAction("Login", "Account");

        var userId = UserId;
        var userEmail = AdminEmail;

        var rolls = await _db.GvList.OrderBy(r => r.ID).ToListAsync();

        var attributesData = await _attributesService.GetDashboardDataAsync(userId);
        var attributesLinked = await _dashboardService.GetAttributesLinkedAsync(userId);

        ViewBag.GvList = rolls;

        // ── Use the SAME SPs as the client dashboard ──────────────────
        var rollDataTasks = rolls
            .ToDictionary(
                r => r.Source,
                r => _dashboardService.GetRollDataAsync(r.Source, userId, userEmail));

        await Task.WhenAll(rollDataTasks.Values);

        var rollData = rollDataTasks.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Result);

        var adminFullName =
    User.FindFirstValue("FullName")
    ?? HttpContext.Session.GetString("AdminFullName")
    ?? string.Empty;

        var adminPosition =
            User.FindFirstValue("Position")
            ?? HttpContext.Session.GetString("AdminPosition")
            ?? string.Empty;

        var sapFull =
            User.FindFirstValue("SAPNumber")
            ?? HttpContext.Session.GetString("AdminSapNumber")
            ?? SapNumber
            ?? string.Empty;

        var sapNumeric = sapFull.Contains('\\')
            ? sapFull.Split('\\').Last()
            : sapFull;


        // ── Build view model ──────────────────────────────────────────
        var vm = new AdminDashboardViewModel
        {
            UserId = userId,
            AdminEmail = AdminEmail,

            SapNumber = sapFull,
            SapNumeric = sapNumeric,
            AdminFullName = adminFullName,
            AdminPosition = adminPosition,

            Announcement = _announcement.GetAnnouncement(),
            Rolls = rolls,
            RollData = rollData,
            RollDates = _rollDates.Dates,
            AttributesData = attributesData,
            AttributesLinked = attributesLinked,
        };

        try
        {
            vm.Rebates = await _rebates.GetDashboardAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[AdminDashboard] Rebates failed for {UserId}", userId);
            vm.Rebates = new();
        }

        await _audit.LogAsync(AdminEmail, AuditActions.ViewDashboard,
            SapNumber, ipAddress: ClientIp);

        return View(vm);
    }

    // ══════════════════════════════════════════════════════════════════
    //  GET /admin/search  — Unified search page
    // ══════════════════════════════════════════════════════════════════
    [HttpGet]
    [Route("admin/search")]
    public async Task<IActionResult> Search()
    {
        if (!IsAdmin()) return View("_NoAccess");

        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();
        ViewBag.Townships = await _search.GetTownshipsAsync();
        ViewBag.Schemes = await _search.GetSchemesAsync();
        return View();
    }

    // POST /admin/search/ref — search by reference number
    [HttpPost]
    [Route("admin/search/ref")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SearchByRef(
        string refNo,
        string? rollSource,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return View("_NoAccess");

        if (string.IsNullOrWhiteSpace(refNo))
        {
            TempData["SearchError"] = "Please enter a reference number.";
            return RedirectToAction(nameof(Search));
        }

        var result = await _adminService.SearchByReferenceAsync(
            refNo.Trim(),
            rollSource,
            cancellationToken);

        await _audit.LogAsync(AdminEmail, AuditActions.Search, SapNumber,
            rollSource: rollSource ?? "All",
            searchValue: refNo, ipAddress: ClientIp);

        return View("SearchResults", result);
    }

    // POST /admin/search/link-property
    // Links an already-found roll/LIS property to the resolved client account.
    // The reference is resolved again so posted UserIDs and property keys can
    // never redirect ownership to the logged-in Administrator.
    [HttpPost]
    [Route("admin/search/link-property")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkEnquiryProperty(
        string referenceNumber,
        string rollSource,
        string idProperty,
        string propertyFrom,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return View("_NoAccess");

        if (string.IsNullOrWhiteSpace(referenceNumber)
            || string.IsNullOrWhiteSpace(rollSource)
            || string.IsNullOrWhiteSpace(idProperty))
        {
            TempData["SearchError"] =
                "The property could not be linked because required information was missing.";
            return RedirectToAction(nameof(Search));
        }

        var foundation = await _adminReferenceResolver.ResolveAsync(
            referenceNumber.Trim(),
            null,
            cancellationToken);

        if (foundation is null)
        {
            TempData["SearchError"] =
                "The original submission reference could not be resolved again.";
            return RedirectToAction(nameof(Search));
        }

        foundation.AccountInformation =
            await _adminAccountInformationService.GetAsync(
                foundation,
                cancellationToken);

        var account = foundation.AccountInformation.SubmittingAccount;

        if (!account.CanOwnLinkedProperties)
        {
            TempData["SearchError"] = account.IsAdministrativeAccount
                ? "The property was not linked because the resolved account belongs to Administration. Resolve the genuine client account first."
                : "The property was not linked because no genuine client account was resolved.";

            var blockedResult = await _adminService.SearchByReferenceAsync(
                referenceNumber,
                null,
                cancellationToken);
            return View("SearchResults", blockedResult);
        }

        var rollInformation = await _adminRollInformationService.GetAsync(
            foundation,
            rollSource,
            cancellationToken);

        var verifiedProperty = rollInformation.Properties.FirstOrDefault(x =>
            x.RollSource.Equals(rollSource, StringComparison.OrdinalIgnoreCase)
            && x.IdProperty.Equals(idProperty, StringComparison.OrdinalIgnoreCase)
            && x.PropertyFrom.Equals(propertyFrom, StringComparison.OrdinalIgnoreCase));

        if (verifiedProperty is null || !verifiedProperty.CanLink)
        {
            TempData["SearchError"] =
                verifiedProperty?.LinkUnavailableReason
                ?? "The property link was rejected because the posted property did not match the resolved roll record.";

            var rejectedResult = await _adminService.SearchByReferenceAsync(
                referenceNumber,
                null,
                cancellationToken);
            return View("SearchResults", rejectedResult);
        }

        var link = await _search.LinkPropertyAsync(
            verifiedProperty.RollSource,
            verifiedProperty.IdProperty,
            account.UserId,
            verifiedProperty.PropertyFrom);

        if (link.Success)
        {
            TempData["SearchSuccess"] =
                $"Property linked successfully to {account.DisplayName}'s account on {verifiedProperty.RollName}.";

            await _audit.LogAsync(
                AdminEmail,
                "AdminLinkClientProperty",
                SapNumber,
                rollSource: verifiedProperty.RollSource,
                searchValue: referenceNumber,
                entityRef: account.UserId,
                details:
                    $"Linked property '{verifiedProperty.PropertyDescription}' to client account {account.UserId}.",
                ipAddress: ClientIp);
        }
        else
        {
            TempData["SearchError"] = link.ErrorMessage
                ?? "The property could not be linked.";
        }

        var result = await _adminService.SearchByReferenceAsync(
            referenceNumber,
            null,
            cancellationToken);
        return View("SearchResults", result);
    }

    // POST /admin/search/property — redirect to the GET results page
    // (Post/Redirect/Get), so "Back" from a property returns to the results
    // instead of "Confirm Form Resubmission".
    [HttpPost]
    [Route("admin/search/property")]
    [ValidateAntiForgeryToken]
    public IActionResult SearchByProperty(
        string? TownName, string? Stand, string? Address,
        string? Scheme, string? Unit, string? rollSource)
    {
        if (!IsAdmin()) return View("_NoAccess");

        if (string.IsNullOrWhiteSpace(TownName))
        {
            TempData["SearchError"] =
                "Please select a Township before searching for a property.";
            return RedirectToAction(nameof(Search));
        }

        TempData["AuditPropertySearch"] = "1";   // audit once, not on every Back
        return RedirectToAction(nameof(SearchByPropertyResults), new
        {
            TownName = TownName?.Trim(), Stand = Stand?.Trim(), Address = Address?.Trim(),
            Scheme = Scheme?.Trim(), Unit = Unit?.Trim(), rollSource
        });
    }

    // GET /admin/search/property?TownName=… — results page
    [HttpGet]
    [Route("admin/search/property")]
    public async Task<IActionResult> SearchByPropertyResults(
        string? TownName, string? Stand, string? Address,
        string? Scheme, string? Unit, string? rollSource,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return View("_NoAccess");

        if (string.IsNullOrWhiteSpace(TownName))
            return RedirectToAction(nameof(Search));

        var result = await _adminService.SearchByPropertyAsync(
            TownName,
            Stand,
            Address,
            Scheme,
            Unit,
            rollSource,
            cancellationToken);

        if (TempData["AuditPropertySearch"] as string == "1")
        {
            await _audit.LogAsync(AdminEmail, AuditActions.Search, SapNumber,
                rollSource: rollSource ?? "All",
                searchValue: result.SearchInput, ipAddress: ClientIp);
        }

        return View("SearchResults", result);
    }

    [HttpPost]
    [Route("admin/search/property/open")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenPropertySearchResult(
        string rollSource,
        string propertyFrom,
        string propertyDescription,
        string unitKey,
        string valuationKey,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return View("_NoAccess");

        if (string.IsNullOrWhiteSpace(rollSource)
            || string.IsNullOrWhiteSpace(unitKey)
            || string.IsNullOrWhiteSpace(valuationKey))
        {
            TempData["SearchError"] =
                "The selected property could not be opened because its stable property keys were missing.";
            return RedirectToAction(nameof(Search));
        }

        var result = await _adminService.OpenPropertyAsync(
            rollSource.Trim(),
            propertyFrom?.Trim() ?? string.Empty,
            propertyDescription?.Trim() ?? string.Empty,
            unitKey?.Trim() ?? string.Empty,
            valuationKey?.Trim() ?? string.Empty,
            cancellationToken);

        await _audit.LogAsync(
            AdminEmail,
            "OpenPropertySearchResult",
            SapNumber,
            rollSource: rollSource,
            searchValue: propertyDescription,
            entityRef: $"{unitKey}|{valuationKey}",
            ipAddress: ClientIp);

        return View("SearchResults", result);
    }

    // ══════════════════════════════════════════════════════════════════
    //  GET /admin/client-account
    //  Phase 2: load the full client property account by resolved UserID.
    // ══════════════════════════════════════════════════════════════════
    [HttpGet]
    [Route("admin/client-account")]
    public async Task<IActionResult> ClientAccount(
        string userId,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return View("_NoAccess");

        if (string.IsNullOrWhiteSpace(userId))
        {
            TempData["SearchError"] =
                "The client account could not be opened because no UserID was supplied.";

            return RedirectToAction(nameof(Search));
        }

        var model =
            await _adminClientAccountService.GetClientAccountAsync(
                userId,
                cancellationToken);

        if (model is null)
        {
            TempData["SearchError"] =
                "The selected UserID was not found in AspNetUsers.";

            return RedirectToAction(nameof(Search));
        }

        ViewBag.ReturnUrl =
            !string.IsNullOrWhiteSpace(returnUrl)
            && Url.IsLocalUrl(returnUrl)
                ? returnUrl
                : Url.Action(nameof(Search), "Admin");

        await _audit.LogAsync(
            AdminEmail,
            AuditActions.Search,
            SapNumber,
            rollSource: "ClientAccount",
            searchValue: model.UserId,
            entityRef: model.Email,
            details:
                $"Opened the complete client property account for {model.DisplayName}.",
            ipAddress: ClientIp);

        return View("ClientAccount", model);
    }

    // ══════════════════════════════════════════════════════════════════
    //  GET /admin/client-account/property
    //  Phase 3: support workspace for the selected property.
    // ══════════════════════════════════════════════════════════════════
    [HttpGet]
    [Route("admin/client-account/property")]
    public async Task<IActionResult> PropertyEnquiry(
        string userId,
        string propertyKey,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return View("_NoAccess");

        var model =
            await _adminPropertyEnquiryService.GetAsync(
                userId,
                propertyKey,
                cancellationToken);

        if (model is null)
        {
            TempData["SearchError"] =
                "The selected property enquiry could not be loaded.";

            return RedirectToAction(
                nameof(ClientAccount),
                new { userId });
        }

        ViewBag.ReturnUrl =
            !string.IsNullOrWhiteSpace(returnUrl)
            && Url.IsLocalUrl(returnUrl)
                ? returnUrl
                : Url.Action(
                    nameof(ClientAccount),
                    "Admin",
                    new { userId });

        await _audit.LogAsync(
            AdminEmail,
            "OpenPropertyEnquiry",
            SapNumber,
            rollSource:
                model.Property.RollSource,
            searchValue:
                model.Property.PropertyDescription,
            entityRef:
                model.Client.Email,
            details:
                $"Opened the support workspace for property '{model.Property.PropertyDescription}'.",
            ipAddress:
                ClientIp);

        return View(
            "PropertyEnquiry",
            model);
    }

    // ══════════════════════════════════════════════════════════════════
    //  GET /admin/client-account/acknowledgement
    //  Generates the acknowledgement using the client's UserID.
    // ══════════════════════════════════════════════════════════════════
    [HttpGet]
    [Route("admin/client-account/acknowledgement")]
    public async Task<IActionResult> DownloadClientAcknowledgement(
        string userId,
        string referenceNumber,
        string? rollSource,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return View("_NoAccess");

        try
        {
            var generated =
                await _acknowledgementDownloadService.GenerateAsync(
                    referenceNumber,
                    rollSource,
                    userId,
                    cancellationToken: cancellationToken);

            await _audit.LogAsync(
                AdminEmail,
                "DownloadClientAcknowledgement",
                SapNumber,
                rollSource:
                    rollSource,
                searchValue:
                    referenceNumber,
                entityRef:
                    userId,
                details:
                    $"Generated and downloaded the {generated.SubmissionType} acknowledgement.",
                ipAddress:
                    ClientIp);

            return File(
                generated.PdfBytes,
                "application/pdf",
                generated.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Admin acknowledgement generation failed for {ReferenceNumber}.",
                referenceNumber);

            TempData["SearchError"] =
                "The acknowledgement could not be generated.";

            if (!string.IsNullOrWhiteSpace(returnUrl)
                && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToAction(nameof(Search));
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  GET /admin/client-account/notice-download
    //  Downloads only a notice already resolved for this client.
    // ══════════════════════════════════════════════════════════════════
    [HttpGet]
    [Route("admin/client-account/notice-download")]
    public async Task<IActionResult> DownloadClientNotice(
        string userId,
        string path,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin())
            return View("_NoAccess");

        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("The notice path is required.");

        var decoded =
            Uri.UnescapeDataString(path);

        var belongsToClient =
            await _adminPropertyEnquiryService
                .NoticeBelongsToClientAsync(
                    userId,
                    decoded,
                    cancellationToken);

        if (!belongsToClient)
            return Forbid();

        if (!System.IO.File.Exists(decoded))
            return NotFound("The notice or email copy was not found.");

        await _audit.LogAsync(
            AdminEmail,
            "DownloadClientNotice",
            SapNumber,
            searchValue:
                Path.GetFileName(decoded),
            entityRef:
                userId,
            details:
                "Downloaded a client notice or email copy from the property support workspace.",
            ipAddress:
                ClientIp);

        var extension =
            Path.GetExtension(decoded)
                .ToLowerInvariant();

        var contentType =
            extension == ".eml"
                ? "message/rfc822"
                : "application/pdf";

        return File(
            await System.IO.File.ReadAllBytesAsync(
                decoded,
                cancellationToken),
            contentType,
            Path.GetFileName(decoded));
    }

    // ══════════════════════════════════════════════════════════════════
    //  GET /admin/notices  — View any client's notices
    // ══════════════════════════════════════════════════════════════════
    [HttpGet]
    [Route("admin/notices")]
    public IActionResult Notices()
    {
        if (!IsAdmin()) return View("_NoAccess");
        return View();
    }

    [HttpPost]
    [Route("admin/notices/search")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NoticesSearch(string clientEmail)
    {
        if (!IsAdmin()) return View("_NoAccess");

        if (string.IsNullOrWhiteSpace(clientEmail))
        {
            ViewBag.Error = "Please enter a client email address.";
            return View("Notices");
        }

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.NormalizedEmail ==
                                      clientEmail.Trim().ToUpper());

        if (user is null)
        {
            ViewBag.Error = $"No account found for: {clientEmail}";
            return View("Notices");
        }

        var vm = await _noticeService.GetNoticesDashboardAsync(
            user.Id, user.Email ?? clientEmail);

        await _audit.LogAsync(AdminEmail, "ViewClientNotices", SapNumber,
            entityRef: clientEmail, ipAddress: ClientIp);

        return View("Notices", vm);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Legacy search endpoints (kept for backward compatibility)
    // ══════════════════════════════════════════════════════════════════
    [HttpPost]
    [Route("admin/search/objection")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DashBoard1(string searchValue, string? rollSource)
    {
        if (!IsAdmin()) return View("_NoAccess");

        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();
        TempData["SearchValue"] = searchValue;

        var rolls = string.IsNullOrEmpty(rollSource)
            ? (await _db.GvList.Where(r => !r.IsQuery).ToListAsync()).Select(r => r.Source)
            : new[] { rollSource };

        var results = new Dictionary<string, List<AdminObjectionResult>>();
        foreach (var roll in rolls)
            results[roll] = await _adminService.SearchObjectionsAsync(roll, searchValue);

        await _audit.LogAsync(AdminEmail, AuditActions.Search, SapNumber,
            rollSource: rollSource ?? "All", searchValue: searchValue, ipAddress: ClientIp);

        ViewBag.SearchResults = results;
        ViewBag.SearchValue = searchValue;
        ViewBag.SearchType = "Objection";
        return View("SearchResults");
    }

    [HttpPost]
    [Route("admin/search/appeal")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DashBoard2(string searchAppealValue, string? rollSource)
    {
        if (!IsAdmin()) return View("_NoAccess");

        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();
        TempData["SearchAppealValue"] = searchAppealValue;

        var rolls = string.IsNullOrEmpty(rollSource)
            ? (await _db.GvList.Where(r => !r.IsQuery).ToListAsync()).Select(r => r.Source)
            : new[] { rollSource };

        var results = new Dictionary<string, List<AdminAppealResult>>();
        foreach (var roll in rolls)
            results[roll] = await _adminService.SearchAppealsAsync(roll, searchAppealValue);

        await _audit.LogAsync(AdminEmail, AuditActions.SearchAppeal, SapNumber,
            rollSource: rollSource ?? "All", searchValue: searchAppealValue, ipAddress: ClientIp);

        ViewBag.SearchResults = results;
        ViewBag.SearchValue = searchAppealValue;
        ViewBag.SearchType = "Appeal";
        return View("SearchResults");
    }

    // ── JS fire-and-forget audit log ──────────────────────────────────
    [HttpPost]
    [Route("admin/log-action")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LogAction([FromBody] AdminActionRequest req)
    {
        if (!IsAdmin()) return Forbid();
        await _audit.LogAsync(AdminEmail, req.Action, SapNumber,
            rollSource: req.RollSource, entityRef: req.EntityRef, ipAddress: ClientIp);
        return Ok();
    }
    [HttpGet]
    [Authorize(Roles = "Admin")]
    [Route("admin/enquiry/form-view")]
    public async Task<IActionResult> FormView(
    string referenceNo,
    string rollSource,
    string? propertyType,
    string appealStatus = "False",
    bool isQuery = false)
    {
        var isAppeal =
            appealStatus.Equals("True", StringComparison.OrdinalIgnoreCase)
            || referenceNo.StartsWith("APP", StringComparison.OrdinalIgnoreCase);

        if (rollSource.Equals("Objection_Query", StringComparison.OrdinalIgnoreCase)
            || rollSource.Equals("Query", StringComparison.OrdinalIgnoreCase)
            || referenceNo.Contains("Que", StringComparison.OrdinalIgnoreCase)
            || referenceNo.Contains("Review", StringComparison.OrdinalIgnoreCase))
        {
            isQuery = true;
        }

        var result = await _adminFormViewService.GetFormViewAsync(
            referenceNo,
            rollSource,
            propertyType,
            isAppeal,
            isQuery);

        if (!result.Success)
        {
            TempData["SearchError"] = result.Error;
            return RedirectToAction("Search", "Admin");
        }

        ViewBag.IsAdminView = true;
        ViewBag.ReadOnly = true;

        return View("FormDataView", result);
    }
}

public record AdminActionRequest(string Action, string RollSource, string EntityRef);

