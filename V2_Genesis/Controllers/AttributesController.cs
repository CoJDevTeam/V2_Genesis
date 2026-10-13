using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Claims;
using V2_Genesis.Data;
using V2_Genesis.Models;
using V2_Genesis.Models.Attributes;
using V2_Genesis.Models.ViewModels.Attributes;
using V2_Genesis.Services.Implementations;
using V2_Genesis.Services.Interfaces;

namespace V2_Genesis.Controllers;

[Authorize]
public class AttributesController : Controller
{
    private static readonly ConcurrentDictionary<string, byte>
        ActiveAttributeSubmissions = new(StringComparer.OrdinalIgnoreCase);

    private readonly IAttributesSearchService _attrSearch;
    private readonly IPropertySearchService _propSearch;
    private readonly ApplicationDbContext _db;
    private readonly AttributesDbContext _attrDb;
    private readonly ILogger<AttributesController> _logger;
    private readonly IAttributeSubmissionService _attributeService;
    private readonly IEvidenceService _evidenceService;
    private readonly IEmailService _emailService;
    public AttributesController(
        IAttributesSearchService attrSearch,
        IPropertySearchService propSearch,
        ApplicationDbContext db, AttributesDbContext attributesDb,
        ILogger<AttributesController> logger, IAttributeSubmissionService attributeService,
        IEvidenceService evidenceSe, IEmailService emailService)
    {
        _attrSearch = attrSearch;
        _propSearch = propSearch;
        _db = db;
        _attrDb = attributesDb;
        _logger = logger;
        _attributeService = attributeService;
        _evidenceService = evidenceSe;
        _emailService = emailService;
    }

    [HttpGet("attributes/correct/{attrId:long}")]
    public async Task<IActionResult> CorrectReturned(
        long attrId,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        var model = await _attributeService.GetReturnedCorrectionAsync(
            attrId,
            userId,
            cancellationToken);

        if (model is null)
        {
            TempData["AttributeError"] =
                "This submission is not available for correction or does not belong to your account.";
            return RedirectToAction("Index", "Dashboard", new { openRoll = "attributes" });
        }

        return View("CorrectReturned", model);
    }

    [HttpPost("attributes/correct/{attrId:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CorrectReturned(
        long attrId,
        ReturnedAttributeCorrectionViewModel model,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Challenge();

        // Load the trusted database model once. Do not repeatedly call the GET
        // builder from each validation/catch branch because that can overwrite
        // the posted correction values and display metadata.
        var current = await _attributeService.GetReturnedCorrectionAsync(
            attrId,
            userId,
            cancellationToken);

        if (current is null)
        {
            TempData["AttributeError"] =
                "This submission is not available for correction or does not belong to your account.";
            return RedirectToAction("Index", "Dashboard", new { openRoll = "attributes" });
        }

        model.AttrId = attrId;

        // PropertyDescription and the nested property-identification fields are
        // server-owned display values. They must never participate in POST
        // validation or be trusted from hidden inputs.
        RemoveDisplayOnlyCorrectionValidation();

        if (!ModelState.IsValid)
        {
            return View("CorrectReturned", RebuildReturnedCorrectionModel(current, model));
        }

        try
        {
            await _attributeService.ResubmitReturnedCorrectionAsync(
                model,
                userId,
                User.Identity?.Name ?? userId,
                cancellationToken);

            TempData["AttributeSuccess"] =
                "Your corrected attribute submission was resubmitted to the valuer successfully.";

            return RedirectToAction("Index", "Dashboard", new { openRoll = "attributes" });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("CorrectReturned", RebuildReturnedCorrectionModel(current, model));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to resubmit returned attribute {AttrId} for user {UserId}.",
                attrId,
                userId);

            ModelState.AddModelError(
                string.Empty,
                "The corrections could not be saved. Please try again or contact Valuation Services.");

            return View("CorrectReturned", RebuildReturnedCorrectionModel(current, model));
        }
    }

    private void RemoveDisplayOnlyCorrectionValidation()
    {
        var displayOnlyKeys = new[]
        {
            nameof(ReturnedAttributeCorrectionViewModel.PropertyDescription),
            nameof(ReturnedAttributeCorrectionViewModel.AttrNo),
            nameof(ReturnedAttributeCorrectionViewModel.FormType),
            "Submission.PropertyDetails",
            "Submission.PropertyDetails.PropertyDesc",
            "Submission.PropertyDetails.PropertyDescription",
            "Submission.AttrNo",
            "Submission.FormType"
        };

        foreach (var key in displayOnlyKeys)
        {
            ModelState.Remove(key);
        }
    }

    private static ReturnedAttributeCorrectionViewModel RebuildReturnedCorrectionModel(
        ReturnedAttributeCorrectionViewModel current,
        ReturnedAttributeCorrectionViewModel posted)
    {
        // Preserve the user's entered correction values, but restore all trusted
        // property/reference metadata from the database model.
        var postedSubmission = posted.Submission ?? new AttributeSubmissionViewModel();
        postedSubmission.AttrId = current.Submission.AttrId;
        postedSubmission.AttrNo = current.Submission.AttrNo;
        postedSubmission.FormType = current.Submission.FormType;
        postedSubmission.PropertyDetails = current.Submission.PropertyDetails;

        current.Submission = postedSubmission;
        current.AttrId = current.AttrId;
        current.RevisionComment = posted.RevisionComment;

        return current;
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("attributes/about")]
    public async Task<IActionResult> About()
    {
        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();
        return View();
    }

    // GET /attributes/search[?SearchTownName=…] — the results page is a GET
    // (Post/Redirect/Get), so "Back" from a property returns to the results
    // instead of Chrome's "Confirm Form Resubmission".
    [HttpGet]
    [AllowAnonymous]
    [Route("attributes/search")]
    public async Task<IActionResult> Search(
     string? SearchTownName, string? SearchStand,
     string? SearchAddress, string? SearchScheme, string? SearchUnit)
    {
        var hasCriteria =
            !string.IsNullOrWhiteSpace(SearchTownName) || !string.IsNullOrWhiteSpace(SearchStand) ||
            !string.IsNullOrWhiteSpace(SearchAddress) || !string.IsNullOrWhiteSpace(SearchScheme) ||
            !string.IsNullOrWhiteSpace(SearchUnit);

        if (hasCriteria)
            return await RunAttributeSearch(SearchTownName, SearchStand, SearchAddress, SearchScheme, SearchUnit);

        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();
        ViewBag.Townships = await _attrSearch.GetTownshipsAsync();
        ViewBag.Schemes = await _attrSearch.GetSchemesAsync();
        return View();
    }

    // POST /attributes/search — redirect to the GET results page.
    [HttpPost]
    [AllowAnonymous]
    [Route("attributes/search")]
    [ValidateAntiForgeryToken]
    [ActionName("Search")]
    public async Task<IActionResult> SearchPost(
     string? SearchTownName, string? SearchStand,
     string? SearchAddress, string? SearchScheme, string? SearchUnit)
    {
        var hasCriteria =
            !string.IsNullOrWhiteSpace(SearchTownName) || !string.IsNullOrWhiteSpace(SearchStand) ||
            !string.IsNullOrWhiteSpace(SearchAddress) || !string.IsNullOrWhiteSpace(SearchScheme) ||
            !string.IsNullOrWhiteSpace(SearchUnit);

        if (!hasCriteria)
            return await RunAttributeSearch(null, null, null, null, null);

        return RedirectToAction(nameof(Search), new
        {
            SearchTownName = SearchTownName?.Trim(),
            SearchStand = SearchStand?.Trim(),
            SearchAddress = SearchAddress?.Trim(),
            SearchScheme = SearchScheme?.Trim(),
            SearchUnit = SearchUnit?.Trim()
        });
    }

    private async Task<IActionResult> RunAttributeSearch(
     string? SearchTownName, string? SearchStand,
     string? SearchAddress, string? SearchScheme, string? SearchUnit)
    {
        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();
        ViewBag.Townships = await _attrSearch.GetTownshipsAsync();
        ViewBag.Schemes = await _attrSearch.GetSchemesAsync();
        ViewBag.IsAuth = User.Identity?.IsAuthenticated == true;

        if (string.IsNullOrWhiteSpace(SearchTownName) &&
            string.IsNullOrWhiteSpace(SearchStand) &&
            string.IsNullOrWhiteSpace(SearchAddress) &&
            string.IsNullOrWhiteSpace(SearchScheme) &&
            string.IsNullOrWhiteSpace(SearchUnit))
        {
            ViewBag.Error = "Please enter at least one search field.";
            return View("Search");
        }

        var p = new PropertySearchParams
        {
            TownName = SearchTownName?.Trim() ?? string.Empty,
            Stand = string.IsNullOrWhiteSpace(SearchStand) ? null : SearchStand.Trim(),
            Address = string.IsNullOrWhiteSpace(SearchAddress) ? null : SearchAddress.Trim(),
            Scheme = string.IsNullOrWhiteSpace(SearchScheme) ? null : SearchScheme.Trim(),
            Unit = string.IsNullOrWhiteSpace(SearchUnit) ? null : SearchUnit.Trim(),
        };

        var results = await _attrSearch.SearchAsync(p);
        ViewBag.SearchParams = p;
        ViewBag.Results = results;
        ViewBag.ResultCount = results.Count;
        return View("Search");
    }

    [HttpPost]
    [Route("attributes/link")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkProperty(
    AttributePropertyLinkVerificationViewModel model,
    CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { success = false, message = "Please sign in before linking a property." });

        if (!ModelState.IsValid)
            return BadRequest(new { success = false, message = "Enter both the Account Number and Statement PIN." });

        try
        {
            var verified = await _attrSearch.VerifyAccountStatementPinAsync(
                model.IdProperty,
                model.AccountNumber,
                model.StatementPin,
                cancellationToken);

            if (!verified)
            {
                // Never log StatementPin.
                _logger.LogWarning(
                    "[Attributes] Account verification failed. User={UserId}, UnitKey={UnitKey}, Account={Account}",
                    userId, model.IdProperty, model.AccountNumber);

                return BadRequest(new
                {
                    success = false,
                    message = "The Account Number or Statement PIN does not match this property. Please check the municipal account statement."
                });
            }

            var result = await _attrSearch.LinkPropertyAsync(
                model.IdProperty,
                userId,
                "Attributes",
                model.AccountNumber.Trim());

            _logger.LogInformation(
                "[Attributes] Property account verified and link processed. User={UserId}, UnitKey={UnitKey}, Account={Account}, Duplicate={Duplicate}",
                userId, model.IdProperty, model.AccountNumber, result.IsDuplicate);

            var linkMessage = result.IsDuplicate
                ? "This property is already linked to your profile."
                : "Property verified and linked successfully.";

            // Shown as a banner on the Property Attributes page.
            TempData["AttrLinkSuccess"] = linkMessage;

            return Ok(new
            {
                success = true,
                duplicate = result.IsDuplicate,
                message = linkMessage,
                // Back to the Property Attributes page with the Linked
                // Properties table open (not the dashboard home).
                redirectUrl = "/dashboard/attributes-detail?section=linked"
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[Attributes] Secure property link failed. User={UserId}, UnitKey={UnitKey}",
                userId, model.IdProperty);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = "The property could not be linked at this time. Please try again."
            });
        }
    }


    // AttributesController.cs — updated Check + CheckConfirm + Form actions

    // ════════════════════════════════════════════════════════════
    // ATTRIBUTE FORM SELECTION
    //
    // Dashboard -> SelectForm.cshtml -> Representative/Form
    //
    // The old /attributes/check route remains available, but it now
    // renders the SelectForm view instead of the old Check view.
    // ════════════════════════════════════════════════════════════

    [HttpGet]
    [Route("attributes/check")]
    public Task<IActionResult> Check(
        string? idProperty,
        string? formType = null)
    {
        return LoadSelectFormViewAsync(
            idProperty,
            formType);
    }

    [HttpGet]
    [Route("attributes/select-form")]
    public Task<IActionResult> SelectForm(
        string? unitKey,
        string? idProperty,
        string? formType = null)
    {
        var propertyReference =
            !string.IsNullOrWhiteSpace(idProperty)
                ? idProperty
                : unitKey;

        return LoadSelectFormViewAsync(
            propertyReference,
            formType);
    }

    private async Task<IActionResult> LoadSelectFormViewAsync(
        string? propertyReference,
        string? requestedFormType)
    {
        ViewBag.GvList = await _db.GvList
            .OrderBy(x => x.ID)
            .ToListAsync();

        if (string.IsNullOrWhiteSpace(propertyReference))
        {
            TempData["AttrLinkError"] =
                "Property reference was not supplied.";

            return RedirectToAttributesWorkspace();
        }

        propertyReference = propertyReference.Trim();

        var property =
            await _attrSearch.GetPropertyDetailAsync(
                propertyReference);

        if (property is null)
        {
            TempData["AttrLinkError"] =
                "Could not load the linked property. " +
                "Please search and link it again.";

            return RedirectToAttributesWorkspace();
        }

        TempData["Attr_Detail_Json"] =
            System.Text.Json.JsonSerializer.Serialize(property);

        TempData.Keep("Attr_Detail_Json");

        var selectedFormType =
            NormalizeAttributeFormType(
                requestedFormType);

        var model = new AttributeSelectViewModel
        {
            UnitKey = propertyReference,
            PropertyDesc =
                BuildDisplayPropertyDescription(property),
            CatDesc = property.CatDesc,
            TownNameDesc = property.TownNameDesc,
            LisStreetAddress = property.LisStreetAddress,
            MarketValue = property.MarketValue,
            RateableArea =
                property.RateableAreaVal
                ?? property.RateableArea,
            Erf = property.Erf,
            Ptn = property.Ptn,
            Re = property.Re,
            SchemeName = property.SchemeName,
            SchemeNumber = property.SchemeNumber,
            SchemeYear = property.SchemeYear,
            UnitNo = property.UnitNo,
            OwnerName = property.OwnerName,
            ValuationDate = property.ValuationDate,
            Zoning = property.Zoning,
            Reason = property.Reason,

            SuggestedFormType =
                ResolveSuggestedAttributeFormType(
                    property.CatDesc,
                    property.SchemeName,
                    property.UnitNo.ToString()),

            SelectedFormType =
                IsValidAttributeFormType(selectedFormType)
                    ? selectedFormType
                    : string.Empty
        };

        return View("SelectForm", model);
    }

    [HttpPost]
    [Route("attributes/select-form")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectForm(
        AttributeSelectViewModel model)
    {
        ViewBag.GvList = await _db.GvList
            .OrderBy(x => x.ID)
            .ToListAsync();

        if (string.IsNullOrWhiteSpace(model.UnitKey))
        {
            TempData["AttrLinkError"] =
                "Property reference was not supplied.";

            return RedirectToAttributesWorkspace();
        }

        model.UnitKey = model.UnitKey.Trim();

        model.SelectedFormType =
            NormalizeAttributeFormType(
                model.SelectedFormType);

        if (!IsValidAttributeFormType(
                model.SelectedFormType))
        {
            ModelState.AddModelError(
                nameof(model.SelectedFormType),
                "Please select a valid attribute form.");
        }

        if (string.IsNullOrWhiteSpace(
                model.DeclarationType))
        {
            ModelState.AddModelError(
                nameof(model.DeclarationType),
                "Please select whether you are the Owner " +
                "or Representative.");
        }
        else
        {
            model.DeclarationType =
                model.DeclarationType.Trim();

            if (model.DeclarationType is not
                    ("Owner" or "Representative"))
            {
                ModelState.AddModelError(
                    nameof(model.DeclarationType),
                    "Please select a valid submitter type.");
            }
        }

        var detail =
            await _attrSearch.GetPropertyDetailAsync(
                model.UnitKey);

        if (detail is null)
        {
            TempData["AttrLinkError"] =
                "Could not load the linked property. " +
                "Please search and link it again.";

            return RedirectToAttributesWorkspace();
        }

        PopulateAttributeSelectModel(
            model,
            detail);

        if (!ModelState.IsValid)
        {
            return View("SelectForm", model);
        }

        TempData["Attr_Detail_Json"] =
            System.Text.Json.JsonSerializer.Serialize(detail);

        TempData["AttrDeclaration"] =
            model.DeclarationType;

        TempData["AttrRepRequired"] =
            model.DeclarationType == "Representative"
                ? "true"
                : "false";

        TempData.Keep("Attr_Detail_Json");
        TempData.Keep("AttrDeclaration");
        TempData.Keep("AttrRepRequired");

        if (model.DeclarationType == "Representative")
        {
            return RedirectToAction(
                nameof(Representative),
                new
                {
                    idProperty = model.UnitKey,
                    formType = model.SelectedFormType
                });
        }

        return RedirectToAction(
            nameof(Form),
            new
            {
                idProperty = model.UnitKey,
                formType = model.SelectedFormType
            });
    }

    // Compatibility for an old Check form POST.
    [HttpPost]
    [Route("attributes/check")]
    [ValidateAntiForgeryToken]
    public IActionResult CheckConfirm(
        string idProperty,
        string formType,
        string declarationType)
    {
        if (string.IsNullOrWhiteSpace(idProperty))
        {
            TempData["AttrLinkError"] =
                "Property reference was not supplied.";

            return RedirectToAttributesWorkspace();
        }

        formType =
            NormalizeAttributeFormType(formType);

        if (!IsValidAttributeFormType(formType))
        {
            TempData["AttrFormError"] =
                "Please select a valid attribute form.";

            return RedirectToAction(
                nameof(SelectForm),
                new { unitKey = idProperty });
        }

        if (declarationType is not
                ("Owner" or "Representative"))
        {
            TempData["AttrCheckError"] =
                "Please select whether you are the " +
                "Owner or Representative.";

            return RedirectToAction(
                nameof(SelectForm),
                new
                {
                    unitKey = idProperty,
                    formType
                });
        }

        TempData["AttrDeclaration"] =
            declarationType;

        TempData["AttrRepRequired"] =
            declarationType == "Representative"
                ? "true"
                : "false";

        TempData.Keep("Attr_Detail_Json");
        TempData.Keep("AttrDeclaration");
        TempData.Keep("AttrRepRequired");

        if (declarationType == "Representative")
        {
            return RedirectToAction(
                nameof(Representative),
                new { idProperty, formType });
        }

        return RedirectToAction(
            nameof(Form),
            new { idProperty, formType });
    }

    private static void PopulateAttributeSelectModel(
        AttributeSelectViewModel model,
        LisPropertyDetail detail)
    {
        model.PropertyDesc =
            BuildDisplayPropertyDescription(detail);

        model.CatDesc = detail.CatDesc;
        model.TownNameDesc = detail.TownNameDesc;
        model.LisStreetAddress = detail.LisStreetAddress;
        model.MarketValue = detail.MarketValue;
        model.RateableArea =
            detail.RateableAreaVal
            ?? detail.RateableArea;
        model.Erf = detail.Erf;
        model.Ptn = detail.Ptn;
        model.Re = detail.Re;
        model.SchemeName = detail.SchemeName;
        model.SchemeNumber = detail.SchemeNumber;
        model.SchemeYear = detail.SchemeYear;
        model.UnitNo = detail.UnitNo;
        model.OwnerName = detail.OwnerName;
        model.ValuationDate = detail.ValuationDate;
        model.Zoning = detail.Zoning;
        model.Reason = detail.Reason;

        model.SuggestedFormType =
            ResolveSuggestedAttributeFormType(
                detail.CatDesc,
                detail.SchemeName,
                detail.UnitNo.ToString());
    }

    private static string NormalizeAttributeFormType(
        string? formType)
    {
        if (string.IsNullOrWhiteSpace(formType))
            return string.Empty;

        return formType.Trim() switch
        {
            "Business" => "BusinessCommercial",
            "BusinessCommercial" => "BusinessCommercial",
            "NonResidential" => "BusinessCommercial",
            "Non-Residential" => "BusinessCommercial",
            "Non Res" => "BusinessCommercial",

            "DRC" => "DRCMethod",
            "DRCMethod" => "DRCMethod",
            "DRC Method" => "DRCMethod",

            "Residential-ST" => "ResidentialST",
            "Residential ST" => "ResidentialST",
            "ResidentialST" => "ResidentialST",

            "Residential" => "Residential",

            _ => formType.Trim()
        };
    }

    private static bool IsValidAttributeFormType(
        string? formType)
    {
        return NormalizeAttributeFormType(formType) is
            "Residential"
            or "ResidentialST"
            or "BusinessCommercial"
            or "DRCMethod";
    }

    private static string ResolveSuggestedAttributeFormType(
        string? catDesc,
        string? schemeName,
        string? unitNo)
    {
        var category =
            (catDesc ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(schemeName)
            || (!string.IsNullOrWhiteSpace(unitNo)
                && unitNo != "0"))
        {
            return "ResidentialST";
        }

        if (category.Contains("business")
            || category.Contains("commercial")
            || category.Contains("industrial")
            || category.Contains("retail")
            || category.Contains("office"))
        {
            return "BusinessCommercial";
        }

        if (category.Contains("drc")
            || category.Contains("public service")
            || category.Contains("municipal")
            || category.Contains("religious")
            || category.Contains("mining")
            || category.Contains("agricultural")
            || category.Contains("vacant")
            || category.Contains("institutional"))
        {
            return "DRCMethod";
        }

        return "Residential";
    }

    // ─────────────────────────────────────────────────────────────────
    // POPIA — owner details from the roll stay on the server
    // ─────────────────────────────────────────────────────────────────
    private const string OwnerContactSessionKey = "Attr_OwnerContact";

    private sealed class StoredOwnerContact
    {
        public string? PropertyId { get; set; }
        public AttributeContactInfoVm? Contact { get; set; }
    }

    private void KeepOwnerContactOnServer(string? propertyId, AttributeContactInfoVm contact)
    {
        var stored = new StoredOwnerContact
        {
            PropertyId = propertyId,
            Contact = System.Text.Json.JsonSerializer.Deserialize<AttributeContactInfoVm>(
                System.Text.Json.JsonSerializer.Serialize(contact))
        };

        HttpContext.Session.SetString(
            OwnerContactSessionKey,
            System.Text.Json.JsonSerializer.Serialize(stored));
    }

    private static void HideOwnerContactFromPage(AttributeContactInfoVm contact)
    {
        contact.CompanyName = null;
        contact.CompanyRegistrationNumber = null;
        contact.FirstNames = null;
        contact.LastName = null;
        contact.MaidenName = null;
        contact.IDNumber = null;
        contact.DateOfBirth = null;
        contact.Gender = null;
        contact.MaritalStatus = null;
        contact.Citizenship = null;
        contact.PhysicalAddress = null;
        contact.PostalAddress = null;
        contact.PostalCode = null;
        contact.HomePhoneNo = null;
        contact.WorkPhoneNo = null;
        contact.FaxNo = null;
        contact.Email = null;
        contact.CellNo = null;
    }

    private void RestoreOwnerContactFromServer(AttributeSubmissionViewModel model)
    {
        if (model?.ContactInfos is null || model.ContactInfos.Count == 0)
            return;

        var json = HttpContext.Session.GetString(OwnerContactSessionKey);
        if (string.IsNullOrWhiteSpace(json))
            return;

        StoredOwnerContact? stored;
        try
        {
            stored = System.Text.Json.JsonSerializer.Deserialize<StoredOwnerContact>(json);
        }
        catch
        {
            return;
        }

        var saved = stored?.Contact;
        if (saved is null)
            return;

        // Only for the same property the form was opened for.
        var postedPropertyId = model.PropertyDetails?.PropertyId?.Trim();
        if (!string.IsNullOrWhiteSpace(stored!.PropertyId) &&
            !string.IsNullOrWhiteSpace(postedPropertyId) &&
            !string.Equals(stored.PropertyId.Trim(), postedPropertyId, StringComparison.OrdinalIgnoreCase))
            return;

        var contact = model.ContactInfos[0];

        // Identity and addresses always come from the roll copy.
        contact.IsCompany = saved.IsCompany;
        contact.ContactType = saved.ContactType ?? contact.ContactType;
        contact.CompanyName = saved.CompanyName;
        contact.CompanyRegistrationNumber = saved.CompanyRegistrationNumber;
        contact.FirstNames = saved.FirstNames;
        contact.LastName = saved.LastName;
        contact.MaidenName = saved.MaidenName;
        contact.IDNumber = saved.IDNumber;
        contact.DateOfBirth = saved.DateOfBirth;
        contact.Gender = saved.Gender;
        contact.MaritalStatus = saved.MaritalStatus;
        contact.Citizenship = saved.Citizenship;
        contact.PhysicalAddress = saved.PhysicalAddress;
        contact.PostalAddress = saved.PostalAddress;
        contact.PostalCode = saved.PostalCode;
        contact.HomePhoneNo = saved.HomePhoneNo;
        contact.WorkPhoneNo = saved.WorkPhoneNo;
        contact.FaxNo = saved.FaxNo;

        // E-mail and cell: what the client typed wins. A representative does
        // not see these fields, so the owner's details on record are used.
        if (string.IsNullOrWhiteSpace(contact.Email))
            contact.Email = saved.Email;
        if (string.IsNullOrWhiteSpace(contact.CellNo))
            contact.CellNo = saved.CellNo;

        ModelState.Remove("ContactInfos[0].Email");
        ModelState.Remove("ContactInfos[0].CellNo");
    }

    private static string BuildOwnerDisplayName(LisPropertyDetail detail)
    {
        var reconstructedName = NormaliseOwnerName(string.Join(
            " ",
            new[] { detail.OwnerLastName, detail.OwnerFirstNames }
                .Where(value => !string.IsNullOrWhiteSpace(value))));

        // For companies, LIS/SAP splits the leading registered name into the
        // surname field.  Reconstruct from the split fields so UNITY remains
        // in front of ALMA PROP PTY LTD.
        if (LooksLikeCompanyName(reconstructedName))
            return reconstructedName;

        var suppliedOwnerName = NormaliseOwnerName(detail.OwnerName);
        if (!string.IsNullOrWhiteSpace(suppliedOwnerName))
            return suppliedOwnerName;

        return reconstructedName;
    }

    private static bool IsCompanyOwner(
        LisPropertyDetail detail,
        string ownerDisplayName)
    {
        var value = string.Join(
            " ",
            new[]
            {
                ownerDisplayName,
                detail.OwnerFirstNames,
                detail.OwnerLastName
            });

        return LooksLikeCompanyName(value);
    }

    private static bool LooksLikeCompanyName(string? value)
    {
        var searchable = $" {NormaliseOwnerName(value).ToUpperInvariant()} ";
        var companyMarkers = new[]
        {
            " PTY ", " LTD ", " LIMITED ", " PROPRIETARY ", " CC ",
            " NPC ", " INC ", " COMPANY ", " TRUST ", " CORPORATION "
        };

        return companyMarkers.Any(searchable.Contains);
    }

    private static string NormaliseOwnerName(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty).Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));

    private static string BuildDisplayPropertyDescription(
        LisPropertyDetail property)
    {
        if (!string.IsNullOrWhiteSpace(
                property.PropertyDesc))
        {
            return property.PropertyDesc;
        }

        var town =
            property.TownNameDesc
            ?? string.Empty;

        var scheme =
            property.SchemeName
            ?? string.Empty;

        var unitNo = property.UnitNo;
        var erf = property.Erf;

        var portion =
            property.Ptn?.ToString()
            ?? string.Empty;

        var remainder =
            property.Re
            ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(scheme)
            || unitNo != 0)
        {
            var parts = new List<string>();

            if (unitNo != 0)
                parts.Add($"UNIT {unitNo}");

            if (!string.IsNullOrWhiteSpace(scheme))
                parts.Add(scheme);

            if (!string.IsNullOrWhiteSpace(town))
                parts.Add(town);

            return "Scheme " +
                   string.Join(", ", parts);
        }

        if (!string.IsNullOrWhiteSpace(portion)
            && portion != "0"
            && !string.IsNullOrWhiteSpace(town))
        {
            if (remainder.Equals(
                    "RE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return $"RE PORTION {portion} {town}";
            }

            return $"PORTION {portion} {town}";
        }

        if (erf != 0
            && !string.IsNullOrWhiteSpace(town))
        {
            return $"Full Title ERF {erf} {town}";
        }

        return town;
    }

    [HttpGet]
    [Authorize]
    [Route("attributes/form")]
    public IActionResult Form(string idProperty, string formType = "Residential")
    {
        if (string.IsNullOrWhiteSpace(idProperty))
        {
            TempData["AttrLinkError"] = "Property reference was not supplied.";
            return RedirectToAttributesWorkspace();
        }

        var declaration = TempData["AttrDeclaration"]?.ToString();
        if (string.IsNullOrWhiteSpace(declaration))
            return RedirectToAction("Check", new { idProperty, formType });

        formType = formType switch
        {
            "Business" => "BusinessCommercial",
            "DRC" => "DRCMethod",
            "Residential-ST" => "ResidentialST",
            _ => formType
        };

        var model = _attributeService.CreateNew(formType);

        // Pre-fill property details from LIS data
        var detailJson = TempData["Attr_Detail_Json"]?.ToString();
        if (!string.IsNullOrWhiteSpace(detailJson))
        {
            try
            {
                var d = System.Text.Json.JsonSerializer
                    .Deserialize<V2_Genesis.Models.Attributes.LisPropertyDetail>(detailJson);
                if (d is not null)
                {
                    model.PropertyDetails.PropertyId = d.PropertyId ?? idProperty;
                    model.PropertyDetails.UnitKey = d.UnitKey.ToString();
                    model.PropertyDetails.PremiseId = d.PremiseId;
                    model.PropertyDetails.ValuationKey = d.ValuationKey.ToString();
                    model.PropertyDetails.PropertyDesc = d.PropertyDesc;
                    model.PropertyDetails.Township = d.TownNameDesc;
                    model.PropertyDetails.Address = d.LisStreetAddress;
                    model.PropertyDetails.Erf = d.Erf.ToString();
                    model.PropertyDetails.SGNumber = d.SGNumber;
                    model.PropertyDetails.Zoning = d.Zoning;
                    model.PropertyDetails.RollType = d.RollType;
                    model.PropertyDetails.RollDescription = d.RollType;
                    model.PropertyDetails.Extent = d.RateableAreaVal ?? d.RateableArea;
                    model.PropertyDetails.SectionalTitle = d.UnitType;

                    // Pre-fill contact 0 with owner data
                    if (model.ContactInfos.Any())
                    {
                        var contact = model.ContactInfos[0];
                        var ownerDisplayName = BuildOwnerDisplayName(d);
                        var isCompany = IsCompanyOwner(d, ownerDisplayName);

                        contact.FirstNames = d.OwnerFirstNames;
                        contact.LastName = d.OwnerLastName;
                        contact.IsCompany = isCompany;
                        contact.CompanyName = isCompany
                            ? ownerDisplayName
                            : null;
                        contact.IDNumber = d.OwnerId;
                        contact.Email = d.Email;
                        contact.CellNo = d.CellNo;
                        contact.HomePhoneNo = d.TelNo;

                        // Privacy: owner/company identity and addresses are still carried
                        // in the backend model and persisted, but the Create view does not
                        // display them. Only email and cell number are visible/editable.
                        contact.PhysicalAddress = d.LisStreetAddress;
                        contact.PostalAddress = string.Join(", ", new[]
                        {
                            d.ADDR1,
                            d.ADDR2,
                            d.ADDR3,
                            d.ADDR4,
                            d.ADDR5
                        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));

                        contact.ContactType = isCompany ? "Company" : "Owner";

                        // POPIA: keep the owner's details from the roll on the
                        // server only. The page gets an empty contact section;
                        // the details are put back when the form is submitted.
                        KeepOwnerContactOnServer(model.PropertyDetails.PropertyId, contact);
                        HideOwnerContactFromPage(contact);
                    }
                }
            }
            catch { /* open form blank */ }
        }
        else
        {
            model.PropertyDetails.PropertyId = idProperty;
            model.PropertyDetails.UnitKey = idProperty;
        }

        // Pre-fill representative details if they exist in TempData
        var repJson = TempData["AttrRepDetails"]?.ToString();
        if (!string.IsNullOrWhiteSpace(repJson))
        {
            try
            {
                var rep = System.Text.Json.JsonSerializer
                    .Deserialize<System.Text.Json.JsonElement>(repJson);

                model.RepresentativeDetails = new RepresentativeDetailsVm
                {
                    IsRepresentative = true,
                    Representative_Name = rep.GetProperty("Representative_Name").GetString(),
                    Rep_Postal_1 = rep.GetProperty("Rep_Postal_1").GetString(),
                    Rep_Postal_2 = rep.GetProperty("Rep_Postal_2").GetString(),
                    Rep_Postal_3 = rep.GetProperty("Rep_Postal_3").GetString(),
                    Rep_Postal_4 = rep.GetProperty("Rep_Postal_4").GetString(),
                    Rep_Postal_5 = rep.GetProperty("Rep_Postal_5").GetString(),
                    Rep_Home_Phone = rep.GetProperty("Rep_Home_Phone").GetString(),
                    Rep_Cell_Phone = rep.GetProperty("Rep_Cell_Phone").GetString(),
                    Rep_Work_Phone = rep.GetProperty("Rep_Work_Phone").GetString(),
                    Rep_Fax_Phone = rep.GetProperty("Rep_Fax_Phone").GetString(),
                    Rep_Email = rep.GetProperty("Rep_Email").GetString(),
                };
            }
            catch { /* rep section stays empty */ }
        }

        ViewBag.Declaration = declaration;
        ViewBag.RepRequired = declaration == "Representative";

        return View("Create", model);
    }
    // ── Helper — same logic as DashboardService.ResolveFormType ───────
    private static string? ResolveFormType(string? catDesc)
    {
        if (string.IsNullOrWhiteSpace(catDesc)) return null;

        var cat = catDesc.Trim().ToLower();

        if (cat.Contains("sectional") ||
            cat.Contains("residential-st") ||
            cat.Contains("st ") || cat.Contains("unit"))
            return "ResidentialST";

        if (cat.Contains("business") ||
            cat.Contains("commercial") ||
            cat.Contains("industrial") ||
            cat.Contains("retail") ||
            cat.Contains("office"))
            return "BusinessCommercial";

        if (cat.Contains("drc") ||
            cat.Contains("public service") ||
            cat.Contains("institutional"))
            return "DRCMethod";

        return "Residential";
    }


    // ════════════════════════════════════════════════════════════════════
    //  UPDATE Form action — read declaration from TempData
    // ════════════════════════════════════════════════════════════════════





    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AttributeSubmissionViewModel model)
    {
        // POPIA: put back the owner's details from the roll (kept on the
        // server when the form was opened; they are not in the page).
        RestoreOwnerContactFromServer(model);

        if (model.Declaration is null)
        {
            ModelState.AddModelError(
                "Declaration",
                "Declaration details are required.");
        }
        else
        {
            if (!model.Declaration.DeclarationAccepted)
            {
                ModelState.AddModelError(
                    "Declaration.DeclarationAccepted",
                    "You must accept the declaration before submitting.");
            }

            if (string.IsNullOrWhiteSpace(model.Declaration.SignatureName))
            {
                ModelState.AddModelError(
                    "Declaration.SignatureName",
                    "Signature name is required.");
            }
            else
            {
                model.Declaration.SignatureName =
                    model.Declaration.SignatureName.Trim();
            }
        }

        if (!ModelState.IsValid)
        {
            TempData["Error"] =
                "Please correct the highlighted fields before submitting.";

            return View(model);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var submissionKey = string.Join("|",
            userId,
            model.FormType?.Trim(),
            model.PropertyDetails?.ValuationKey?.Trim(),
            model.PropertyDetails?.UnitKey?.Trim(),
            model.PropertyDetails?.PropertyId?.Trim(),
            model.PropertyDetails?.PremiseId?.Trim());

        if (!ActiveAttributeSubmissions.TryAdd(submissionKey, 0))
        {
            ModelState.AddModelError(
                string.Empty,
                "This attribute submission is already being processed. Please wait.");

            return View(model);
        }

        var userName =
            User.FindFirstValue(ClaimTypes.Name)
            ?? User.Identity?.Name
            ?? "Client";

        var userEmail =
            User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue("email");

        var userPhone =
            User.FindFirstValue(ClaimTypes.MobilePhone)
            ?? User.FindFirstValue(ClaimTypes.HomePhone)
            ?? User.FindFirstValue("phone_number");

        // Admin authentication uses the fixed AdministrationEnquiries portal
        // account, while the real Windows/UserManagement identity is carried
        // in SAP claims. Never accept the Capturer value from the form.
        var isAdminCapture =
            User.IsInRole("Admin")
            || User.FindFirstValue("AdminAppEmail")?.Equals(
                "AdministrationEnquiries@Joburg.org.za",
                StringComparison.OrdinalIgnoreCase) == true
            || HttpContext.Session.GetString("AdminAppEmail")?.Equals(
                "AdministrationEnquiries@Joburg.org.za",
                StringComparison.OrdinalIgnoreCase) == true
            || User.FindFirstValue("UMRole")?.Equals(
                "Admin",
                StringComparison.OrdinalIgnoreCase) == true
            || User.FindFirstValue("LoginType")?.Equals(
                "AdminWindows",
                StringComparison.OrdinalIgnoreCase) == true
            || !string.IsNullOrWhiteSpace(
                User.FindFirstValue("SAPNumber"));

        var rawCapturerSapNumber = isAdminCapture
            ? User.FindFirstValue("SAPNumeric")
                ?? HttpContext.Session.GetString("AdminSapNumeric")
                ?? User.FindFirstValue("SAPNumber")
                ?? HttpContext.Session.GetString("AdminSapNumber")
            : null;

        // SAPNumber can contain the Windows domain in compatibility claims
        // (for example JOBURG\10112533). Persist only the numeric SAP value.
        var capturerSapNumber = string.IsNullOrWhiteSpace(rawCapturerSapNumber)
            ? null
            : new string(rawCapturerSapNumber.Where(char.IsDigit).ToArray());

        capturerSapNumber = string.IsNullOrWhiteSpace(capturerSapNumber)
            ? null
            : capturerSapNumber;

        if (isAdminCapture && string.IsNullOrWhiteSpace(capturerSapNumber))
        {
            ActiveAttributeSubmissions.TryRemove(submissionKey, out _);

            ModelState.AddModelError(
                string.Empty,
                "Your SAP number could not be verified. Please sign in again using Windows authentication.");

            return View(model);
        }

        // Some identity configurations store the email in User.Identity.Name.
        if (string.IsNullOrWhiteSpace(userEmail) &&
            !string.IsNullOrWhiteSpace(User.Identity?.Name) &&
            User.Identity.Name.Contains('@'))
        {
            userEmail = User.Identity.Name.Trim();
        }

        try
        {
            var attrId = await _attributeService.SubmitAsync(
                model,
                userId,
                userName,
                userEmail,
                userPhone,
                capturerSapNumber);

            if (attrId <= 0)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "The attribute submission could not be completed.");

                return View(model);
            }

            TempData["Success"] =
                "Attribute submission saved successfully.";

            return RedirectToAction(
                nameof(Details),
                new { id = attrId });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Attribute submission validation failed for user {UserId}.",
                userId);

            ModelState.AddModelError(
                string.Empty,
                ex.Message);

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error submitting attribute request for user {UserId}.",
                userId);

            ModelState.AddModelError(
                string.Empty,
                "An unexpected error occurred while submitting the attribute request.");

            return View(model);
        }
        finally
        {
            ActiveAttributeSubmissions.TryRemove(submissionKey, out _);
        }
    }

    // Compatibility route for older dashboard links such as:
    // /attributes/submission/7
    //
    // The numeric database ID is resolved to the public Attr_No and
    // redirected to the shared submitted-form viewer.
    [HttpGet]
    [Authorize(Roles = "Client")]
    [Route("attributes/submission/{id:long}", Name = "AttributeSubmissionById")]
    public async Task<IActionResult> Submission(
        long id,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var submission = await _attrDb.AttrPropertyInfo
            .AsNoTracking()
            .Where(row =>
                row.Attr_ID == id
                && row.SubmittedByUserId == userId
                && row.IsActive)
            .Select(row => new
            {
                row.Attr_No
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (submission is null)
        {
            TempData["ErrorMessage"] =
                "The attribute submission could not be found, " +
                "or you do not have permission to view it.";

            return RedirectToAction(
                "Index",
                "Dashboard",
                new { openRoll = "attributes" });
        }

        if (string.IsNullOrWhiteSpace(submission.Attr_No))
        {
            TempData["ErrorMessage"] =
                "The attribute submission does not yet have a valid " +
                "attribute reference number.";

            return RedirectToAction(
                "Index",
                "Dashboard",
                new { openRoll = "attributes" });
        }

        var safeReturnUrl =
            !string.IsNullOrWhiteSpace(returnUrl)
            && Url.IsLocalUrl(returnUrl)
                ? returnUrl
                : Url.Action(
                    "Index",
                    "Dashboard",
                    new { openRoll = "attributes" });

        return RedirectToRoute(
            "ViewSubmission",
            new
            {
                submissionType = "Attribute",
                referenceNumber = submission.Attr_No.Trim(),
                rollSource = "Attributes",
                returnUrl = safeReturnUrl
            });
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id)
    {
        var model = await _attributeService.GetAcknowledgementAsync(id);

        if (model == null)
            return NotFound();

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadAcknowledgement(long id)
    {
        var generated = await _attributeService.GenerateAcknowledgementPdfAsync(id);

        if (generated is null || generated.Value.Pdf.Length == 0)
        {
            _logger.LogWarning(
                "[Attributes] On-demand acknowledgement generation returned nothing for Attr_ID={AttrId}",
                id);
            return NotFound("Could not generate the acknowledgement for this submission.");
        }

        return File(generated.Value.Pdf, "application/pdf", generated.Value.FileName);
    }


    [HttpGet]
    [Route("attributes/representative")]
    public async Task<IActionResult> Representative(
    string idProperty, string formType = "Residential")
    {
        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();

        // Guard: must have come through Check page
        var declaration = TempData.Peek("AttrDeclaration")?.ToString();
        if (string.IsNullOrWhiteSpace(declaration) || declaration != "Representative")
        {
            TempData["AttrCheckError"] = "Please complete the check step first.";
            return RedirectToAction("Check", new { idProperty, formType });
        }

        var vm = new RepresentativeViewModel
        {
            IDProperty = idProperty,
            FormType = formType
        };

        // Pre-fill from LIS SAP data stored in TempData
        var json = TempData.Peek("Attr_Detail_Json")?.ToString();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var d = System.Text.Json.JsonSerializer
                    .Deserialize<V2_Genesis.Models.Attributes.LisPropertyDetail>(json);
                if (d is not null)
                {
                    vm.PropertyDesc = d.PropertyDesc;
                    vm.TownNameDesc = d.TownNameDesc;
                    vm.LisStreetAddress = d.LisStreetAddress;
                    vm.CatDesc = d.CatDesc;

                    // POPIA: the representative types their OWN contact details.
                    // The owner's e-mail and phone numbers from the roll are never
                    // shown to (or pre-filled for) a representative.
                }
            }
            catch { /* use empty vm */ }
        }

        return View(vm);
    }

    [HttpPost]
    [Route("attributes/representative")]
    [ValidateAntiForgeryToken]
    public IActionResult RepresentativeSubmit(RepresentativeViewModel vm)
    {
        // Re-validate required fields manually
        if (string.IsNullOrWhiteSpace(vm.Representative_Name))
        {
            ModelState.AddModelError("Representative_Name",
                "Representative name is required.");
            ViewBag.GvList = _db.GvList.OrderBy(r => r.ID).ToList();
            return View("Representative", vm);
        }

        // Store rep details as JSON in TempData
        // They travel through to Form, then into SubmitAsync
        var repData = new
        {
            vm.Representative_Name,
            vm.Rep_Postal_1,
            vm.Rep_Postal_2,
            vm.Rep_Postal_3,
            vm.Rep_Postal_4,
            vm.Rep_Postal_5,
            vm.Rep_Home_Phone,
            vm.Rep_Cell_Phone,
            vm.Rep_Work_Phone,
            vm.Rep_Fax_Phone,
            vm.Rep_Email
        };

        TempData["AttrRepDetails"] = System.Text.Json.JsonSerializer.Serialize(repData);
        TempData.Keep("AttrDeclaration");
        TempData.Keep("Attr_Detail_Json");

        return RedirectToAction("Form",
            new { idProperty = vm.IDProperty, formType = vm.FormType });
    }

    [Authorize]
    [HttpGet(
        "attributes/withdraw/{attrId:long}",
        Name = "AttributeWithdrawalGet")]
    public async Task<IActionResult> WithdrawalGet(
        long attrId,
        string? returnUrl = null)
    {
        var safeReturnUrl = ResolveAttributeWithdrawalReturnUrl(returnUrl);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var model = await BuildAttributeWithdrawalModelAsync(
            attrId,
            userId,
            safeReturnUrl);

        if (model is null)
        {
            TempData["AttributeError"] =
                "The Attributes submission was not found or is not available to your account.";
            return LocalRedirect(safeReturnUrl);
        }

        return View("Withdrawal", model);
    }

    [Authorize]
    [HttpPost(
        "attributes/withdraw",
        Name = "AttributeWithdrawalPost")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawalPost(
        AttributeWithdrawalViewModel model)
    {
        var safeReturnUrl = ResolveAttributeWithdrawalReturnUrl(model.ReturnUrl);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
            return Challenge();

        var trustedModel = await BuildAttributeWithdrawalModelAsync(
            model.AttrId,
            userId,
            safeReturnUrl);

        if (trustedModel is null)
        {
            TempData["AttributeError"] =
                "The Attributes submission was not found or is not available to your account.";
            return LocalRedirect(safeReturnUrl);
        }

        trustedModel.Reason = model.Reason?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trustedModel.Reason))
        {
            ModelState.AddModelError(
                nameof(model.Reason),
                "Please provide a reason for the withdrawal.");
        }
        else if (trustedModel.Reason.Length < 10)
        {
            ModelState.AddModelError(
                nameof(model.Reason),
                "Please provide a withdrawal reason of at least 10 characters.");
        }
        else if (trustedModel.Reason.Length > 1000)
        {
            ModelState.AddModelError(
                nameof(model.Reason),
                "The withdrawal reason cannot exceed 1,000 characters.");
        }

        if (!ModelState.IsValid)
            return View("Withdrawal", trustedModel);

        var userName =
            User.FindFirstValue("FullName")
            ?? User.FindFirstValue(ClaimTypes.Name)
            ?? User.Identity?.Name
            ?? userId;

        try
        {
            await _attributeService.WithdrawAsync(
                trustedModel.AttrId,
                userId,
                userName,
                trustedModel.Reason);

            TempData["AttributeSuccess"] =
                $"Attributes submission {trustedModel.AttrNo} was withdrawn successfully. " +
                "The property is available to submit again.";

            return LocalRedirect(safeReturnUrl);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Attributes withdrawal was rejected for Attr_ID={AttrId}, UserId={UserId}.",
                trustedModel.AttrId,
                userId);

            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Withdrawal", trustedModel);
        }
    }

    private async Task<AttributeWithdrawalViewModel?>
        BuildAttributeWithdrawalModelAsync(
            long attrId,
            string userId,
            string returnUrl)
    {
        if (attrId <= 0)
            return null;

        return await _attrDb.AttrPropertyInfo
            .AsNoTracking()
            .Where(x =>
                x.Attr_ID == attrId &&
                x.SubmittedByUserId == userId &&
                !x.IsWithdrawn &&
                string.IsNullOrWhiteSpace(x.Task_Assigned_To_UserId) &&
                string.IsNullOrWhiteSpace(x.Task_Assigned_To) &&
                string.IsNullOrWhiteSpace(x.ValuerUserId) &&
                string.IsNullOrWhiteSpace(x.Valuer))
            .Select(x => new AttributeWithdrawalViewModel
            {
                AttrId = x.Attr_ID,
                AttrNo = x.Attr_No ?? string.Empty,
                PropertyDescription = x.Property_Desc ?? string.Empty,
                CurrentStatus = x.Attr_Status,
                ReturnUrl = returnUrl
            })
            .FirstOrDefaultAsync();
    }

    private bool IsAdminAttributeSession()
    {
        return User.IsInRole("Admin")
            || User.FindFirstValue("AdminAppEmail")?.Equals(
                "AdministrationEnquiries@Joburg.org.za",
                StringComparison.OrdinalIgnoreCase) == true
            || HttpContext.Session.GetString("AdminAppEmail")?.Equals(
                "AdministrationEnquiries@Joburg.org.za",
                StringComparison.OrdinalIgnoreCase) == true
            || User.FindFirstValue("LoginType")?.Equals(
                "AdminWindows",
                StringComparison.OrdinalIgnoreCase) == true
            || !string.IsNullOrWhiteSpace(User.FindFirstValue("SAPNumber"));
    }

    private string ResolveAttributeWithdrawalReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return returnUrl;

        return "/Dashboard?openRoll=Attributes";
    }

    [HttpGet]
    [Authorize]
    [Route("attributes/unlink")]
    public async Task<IActionResult> Unlink(long id, string? returnUrl = null)
    {
        var safeReturnUrl = ResolveAttributeUnlinkReturnUrl(returnUrl);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId) || id <= 0)
        {
            TempData["AttrLinkError"] = "The linked property could not be identified.";
            return LocalRedirect(safeReturnUrl);
        }

        // The dashboard passes LinkedProperties_Attr.ID, not IDProperty.
        // Keep the UserID condition so one client cannot unlink another
        // client's property by changing the URL value.
        var linked = await _attrDb.LinkedProperties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ID == id && p.UserID == userId);

        if (linked is null)
        {
            TempData["AttrLinkError"] = "Property not found or already removed.";
            return LocalRedirect(safeReturnUrl);
        }

        // Optionally load property details for the confirmation page
        var detail = await _attrSearch.GetPropertyDetailAsync(linked.IDProperty);

        var vm = new UnlinkViewModel
        {
            Id = linked.ID,
            IDProperty = linked.IDProperty,
            PropertyDesc = detail?.PropertyDesc ?? linked.IDProperty,
            TownNameDesc = detail?.TownNameDesc,
            CatDesc = detail?.CatDesc,
            MarketValue = detail?.MarketValue,
            Address = detail?.LisStreetAddress,
            ReturnUrl = safeReturnUrl
        };

        return View(vm);
    }

    // ── POST /attributes/unlink — permanent delete ────────────────────
    [HttpPost]
    [Authorize]
    [Route("attributes/unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlinkConfirm(long id, string? returnUrl = null)
    {
        var safeReturnUrl = ResolveAttributeUnlinkReturnUrl(returnUrl);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId) || id <= 0)
        {
            TempData["AttrLinkError"] = "The linked property could not be identified.";
            return LocalRedirect(safeReturnUrl);
        }

        var linked = await _attrDb.LinkedProperties
            .FirstOrDefaultAsync(p => p.ID == id && p.UserID == userId);

        if (linked is null)
        {
            TempData["AttrLinkError"] = "Property not found or already removed.";
            return LocalRedirect(safeReturnUrl);
        }

        _attrDb.LinkedProperties.Remove(linked);
        await _attrDb.SaveChangesAsync();

        TempData["AttrUnlinkSuccess"] =
            "Property has been removed from your profile. " +
            "You can search and link it again at any time.";

        return LocalRedirect(safeReturnUrl);
    }

    private string ResolveAttributeUnlinkReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return returnUrl;

        return User.IsInRole("Admin")
            ? "/admin?openRoll=attributes"
            : "/Dashboard?openRoll=attributes";
    }
    [HttpGet]
    [AllowAnonymous]
    [Route("attributes/evidence")]
    public async Task<IActionResult> Evidence(string? attrNo)
    {
        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();
        ViewBag.AttrNo = attrNo; // pre-fill from dashboard link

        // Logged-in: load their submissions for dropdown
        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var submissions = await _attrDb.AttrPropertyInfo
                .Where(p => p.SubmittedByUserId == userId && p.IsActive)
                .OrderByDescending(p => p.SubmissionDateTime)
                .Select(p => new { p.Attr_No, p.Property_Desc })
                .ToListAsync();

            ViewBag.Submissions = submissions;
        }

        return View();
    }

    [HttpPost]
    [AllowAnonymous]
    [Route("attributes/evidence")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EvidenceValidate(
    string attrNo, string pin)
    {
        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();
        ViewBag.AttrNo = attrNo;

        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var submissions = await _attrDb.AttrPropertyInfo
                .Where(p => p.SubmittedByUserId == userId && p.IsActive)
                .OrderByDescending(p => p.SubmissionDateTime)
                .Select(p => new { p.Attr_No, p.Property_Desc })
                .ToListAsync();
            ViewBag.Submissions = submissions;
        }

        if (string.IsNullOrWhiteSpace(attrNo) || string.IsNullOrWhiteSpace(pin))
        {
            ViewBag.Error = "Please enter both the Attribute Number and PIN.";
            return View("Evidence");
        }

        var result = await _evidenceService.ValidateAttributeAsync(attrNo, pin);

        if (!result.IsValid)
        {
            ViewBag.Error = result.Error;
            ViewBag.Expired = result.IsExpired;
            return View("Evidence");
        }

        // Valid — store in TempData and show upload form
        TempData["AttrEv_AttrNo"] = result.AttrNo;
        TempData["AttrEv_PropertyDesc"] = result.PropertyDesc;
        TempData["AttrEv_Current"] = result.CurrentCount;
        TempData["AttrEv_Expiry"] = result.ExpiryDate?.ToString("o");
        TempData["AttrEv_Slots"] = result.SlotsRemaining;

        return RedirectToAction("EvidenceUpload");
    }

    // ── GET /attributes/evidence/upload ─────────────────────────────
    [HttpGet]
    [AllowAnonymous]
    [Route("attributes/evidence/upload")]
    public async Task<IActionResult> EvidenceUpload()
    {
        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();

        var attrNo = TempData.Peek("AttrEv_AttrNo")?.ToString();
        if (string.IsNullOrWhiteSpace(attrNo))
            return RedirectToAction("Evidence");

        ViewBag.AttrNo = attrNo;
        ViewBag.PropertyDesc = TempData.Peek("AttrEv_PropertyDesc")?.ToString();
        ViewBag.Current = TempData.Peek("AttrEv_Current") as int? ?? 0;
        ViewBag.Slots = TempData.Peek("AttrEv_Slots") as int? ?? 0;
        ViewBag.Expiry = TempData.Peek("AttrEv_Expiry")?.ToString();

        return View();
    }

    // ── POST /attributes/evidence/upload ────────────────────────────
    [HttpPost]
    [AllowAnonymous]
    [Route("attributes/evidence/upload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EvidenceUpload(List<IFormFile> evidenceFiles)
    {
        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();

        var attrNo = TempData["AttrEv_AttrNo"]?.ToString();
        int current = TempData["AttrEv_Current"] as int? ?? 0;
        int slots = TempData["AttrEv_Slots"] as int? ?? 0;

        if (string.IsNullOrWhiteSpace(attrNo))
            return RedirectToAction("Evidence");

        if (!evidenceFiles.Any())
        {
            ViewBag.Error = "Please select at least one file to upload.";
            ViewBag.AttrNo = attrNo;
            ViewBag.PropertyDesc = TempData["AttrEv_PropertyDesc"]?.ToString();
            ViewBag.Current = current;
            ViewBag.Slots = slots;
            ViewBag.Expiry = TempData["AttrEv_Expiry"]?.ToString();
            TempData.Keep();
            return View();
        }

        if (evidenceFiles.Count > slots)
        {
            ViewBag.Error = $"You selected {evidenceFiles.Count} file(s) " +
                                   $"but only {slots} slot(s) remain. " +
                                   $"Please select fewer files.";
            ViewBag.AttrNo = attrNo;
            ViewBag.PropertyDesc = TempData["AttrEv_PropertyDesc"]?.ToString();
            ViewBag.Current = current;
            ViewBag.Slots = slots;
            ViewBag.Expiry = TempData["AttrEv_Expiry"]?.ToString();
            TempData.Keep();
            return View();
        }

        var (success, error, newCount, savedNames) =
            await _evidenceService.UploadAttributeEvidenceAsync(
                attrNo, current, evidenceFiles);

        if (!success)
        {
            ViewBag.Error = error;
            ViewBag.AttrNo = attrNo;
            ViewBag.PropertyDesc = TempData["AttrEv_PropertyDesc"]?.ToString();
            ViewBag.Current = current;
            ViewBag.Slots = slots;
            ViewBag.Expiry = TempData["AttrEv_Expiry"]?.ToString();
            TempData.Keep();
            return View();
        }
        var uploadedAt = DateTime.Now;
        var remainingSlots = Math.Max(0, 10 - newCount);

        try
        {
            await _emailService.SendAttributeEvidenceUploadConfirmationAsync(
                attrNo,
                savedNames,
                uploadedAt,
                remainingSlots);
        }
        catch (Exception ex)
        {
            // Evidence remains successfully uploaded even when SMTP is unavailable.
            _logger.LogError(ex,
                "[Attribute Evidence Email] Upload succeeded but confirmation email failed for {AttributeNo}",
                attrNo);
            TempData["AttrEv_EmailWarning"] =
                "Your evidence was uploaded successfully, but the confirmation email could not be sent.";
        }

        // Pass acknowledgement data
        TempData["AttrEv_NewCount"] = newCount;
        TempData["AttrEv_Uploaded"] = savedNames.Count;
        TempData["AttrEv_FileNames"] = System.Text.Json.JsonSerializer.Serialize(savedNames);
        TempData["AttrEv_AttrNo"] = attrNo;
        TempData["AttrEv_PropDesc"] = TempData["AttrEv_PropertyDesc"]?.ToString();

        return RedirectToAction("EvidenceAcknowledgement");
    }
    [HttpGet]
    [AllowAnonymous]
    [Route("attributes/evidence/acknowledgement")]
    public async Task<IActionResult> EvidenceAcknowledgement()
    {
        ViewBag.GvList = await _db.GvList.OrderBy(r => r.ID).ToListAsync();

        // Peek — reads without marking for deletion
        ViewBag.AttrNo = TempData.Peek("AttrEv_AttrNo")?.ToString();
        ViewBag.PropDesc = TempData.Peek("AttrEv_PropDesc")?.ToString();
        ViewBag.NewCount = Convert.ToInt32(TempData.Peek("AttrEv_NewCount") ?? 0);
        ViewBag.Uploaded = Convert.ToInt32(TempData.Peek("AttrEv_Uploaded") ?? 0);
        ViewBag.EmailWarning = TempData.Peek("AttrEv_EmailWarning")?.ToString();

        var json = TempData.Peek("AttrEv_FileNames")?.ToString();
        ViewBag.FileNames = string.IsNullOrWhiteSpace(json)
            ? new List<string>()
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);

        if (string.IsNullOrWhiteSpace(ViewBag.AttrNo as string))
            return RedirectToAction("Evidence");

        return View();
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("attributes/acknowledgement/download")]
    public async Task<IActionResult> DownloadAcknowledgement(
        string attrNo,
        string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(attrNo))
            return BadRequest("Attribute number is required.");

        try
        {
            var generated = await _attributeService
                .GenerateAcknowledgementPdfAsync(attrNo.Trim());

            if (generated is null || generated.Value.Pdf.Length == 0)
            {
                _logger.LogWarning(
                    "[Attributes] On-demand acknowledgement generation returned nothing for Attr_No={AttrNo}",
                    attrNo);
                TempData["NoticeError"] =
                    "Could not generate the acknowledgement for this submission.";
                return RedirectAfterAttributeDownload(returnUrl);
            }

            return File(
                generated.Value.Pdf,
                "application/pdf",
                generated.Value.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[Attributes] Acknowledgement download failed for Attr_No={AttrNo}",
                attrNo);
            TempData["NoticeError"] =
                "The attribute acknowledgement could not be downloaded.";
            return RedirectAfterAttributeDownload(returnUrl);
        }
    }

    private IActionResult RedirectAfterAttributeDownload(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return RedirectToAttributesWorkspace();
    }

    private IActionResult RedirectToAttributesWorkspace()
    {
        var isAdmin = User.IsInRole("Admin") ||
            User.FindFirstValue("UMRole")?.Equals(
                "Admin", StringComparison.OrdinalIgnoreCase) == true ||
            User.FindFirstValue("LoginType")?.Equals(
                "AdminWindows", StringComparison.OrdinalIgnoreCase) == true ||
            !string.IsNullOrWhiteSpace(User.FindFirstValue("SAPNumber"));

        return isAdmin
            ? RedirectToAction("Index", "Admin", new { openRoll = "attributes" })
            : LocalRedirect("/dashboard/attributes-detail");
    }
}
