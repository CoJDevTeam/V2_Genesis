using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Text.RegularExpressions;
using V2_Genesis.Data;
using V2_Genesis.Models.Attributes;
using V2_Genesis.Models.ViewModels.Attributes;
using V2_Genesis.Services.Interfaces;

namespace V2_Genesis.Services.Implementations
{
    public class AttributeSubmissionService : IAttributeSubmissionService
    {
        private readonly AttributesDbContext _context;

        private readonly IAttributeDocumentService _documentService;
        private readonly IEmailService _emailService;
        private readonly ILogger<AttributeSubmissionService> _logger;
        private readonly bool _bypassAdminEvidenceWindow;
        private readonly int _attributeEvidenceWindowMinutes;
        public AttributeSubmissionService(
            AttributesDbContext context,
           IAttributeDocumentService documentService,
           IEmailService emailService,
           ILogger<AttributeSubmissionService> logger,
           IConfiguration configuration)
        {
            _context = context;

            _documentService = documentService;
            _emailService = emailService;
            _logger = logger;
            _bypassAdminEvidenceWindow = configuration.GetValue<bool>(
                "AttributeRouting:BypassEvidenceWindow");


            // "EvidenceWindowHours": 48 (normal). "EvidenceWindowMinutes" is
            // only for short tests; Hours wins when both are set.
            var windowHours = configuration.GetValue<int?>(
                "AttributeRouting:EvidenceWindowHours");
            var windowMinutes = configuration.GetValue<int?>(
                "AttributeRouting:EvidenceWindowMinutes");

            _attributeEvidenceWindowMinutes =
                Math.Max(
                    1,
                    windowHours.HasValue
                        ? windowHours.Value * 60
                        : windowMinutes ?? (48 * 60));
        }

        public AttributeSubmissionViewModel CreateNew(string formType)
        {
            formType = NormalizeFormType(formType);

            var model = new AttributeSubmissionViewModel
            {
                FormType = formType,
                ContactInfos = new List<AttributeContactInfoVm>
        {
            new AttributeContactInfoVm
            {
                ContactType = "Owner",
                IsCompany = false
            }
        },
                Access = new AttributeAccessVm
                {
                    AccessType = null,
                    PermissionStatus = null,
                    Comments = null
                },
                ValuationDetails = new AttributeValuationDetailsVm
                {
                    IsMixedUse = false
                }
            };

            if (formType == "BusinessCommercial")
            {
                for (int i = 0; i < 5; i++)
                {
                    model.BusinessBuildings.Add(new AttributeBusinessBuildingVm());
                    model.BusinessSections.Add(new AttributeBusinessSectionVm());
                }
            }

            if (formType == "DRCMethod")
            {
                for (int i = 0; i < 5; i++)
                {
                    model.DrcBuildings.Add(new AttributeDrcBuildingVm());
                    model.DrcImprovements.Add(new AttributeDrcImprovementVm());
                    model.DrcVacantLands.Add(new AttributeDrcVacantLandVm());
                }
            }

            return model;
        }

        private static string NormalizeFormType(string? formType)
        {
            return formType?.Trim() switch
            {
                "Residential" => "Residential",
                "ResidentialST" => "ResidentialST",
                "BusinessCommercial" => "BusinessCommercial",
                "DRCMethod" => "DRCMethod",
                "Business" => "BusinessCommercial",
                "DRC" => "DRCMethod",
                "Residential-ST" => "ResidentialST",
                _ => "Residential"
            };
        }

        public async Task<long> SubmitAsync(
      AttributeSubmissionViewModel model,
      string userId,
      string userName,
      string? userEmail,
      string? userPhone,
      string? capturerSapNumber = null)

        {

            ValidateAndCleanSubmission(model);

            var submissionSector = await ResolveSectorByTownshipAsync(
                model.PropertyDetails.Township);

            if (string.IsNullOrWhiteSpace(submissionSector))
            {
                throw new InvalidOperationException(
                    $"No sector mapping is configured for township '{model.PropertyDetails.Township}'. " +
                    "Please contact the administrator before submitting.");
            }

            model.PropertyDetails.Sector = submissionSector;
            await using var transaction = await _context.Database.BeginTransactionAsync();

            var now = DateTime.Now;

            var firstContact = model.ContactInfos?
                .FirstOrDefault(x =>
                    !string.IsNullOrWhiteSpace(x.Email) ||
                    !string.IsNullOrWhiteSpace(x.CellNo) ||
                    !string.IsNullOrWhiteSpace(x.HomePhoneNo) ||
                    !string.IsNullOrWhiteSpace(x.WorkPhoneNo));

            var isRepresentativeSubmission =
                model.RepresentativeDetails?.IsRepresentative == true;

            // The submitter email reflects the party who submitted the form.
            // Owner submissions use the owner contact email. Representative
            // submissions use the representative email; the owner remains a CC
            // recipient for Attribute notifications.
            var submittedByEmail = isRepresentativeSubmission &&
                                   !string.IsNullOrWhiteSpace(model.RepresentativeDetails?.Rep_Email)
                ? model.RepresentativeDetails.Rep_Email!.Trim()
                : !string.IsNullOrWhiteSpace(firstContact?.Email)
                    ? firstContact.Email.Trim()
                    : userEmail?.Trim();

            var submittedByPhone =
                !string.IsNullOrWhiteSpace(firstContact?.CellNo)
                    ? firstContact.CellNo.Trim()
                    : !string.IsNullOrWhiteSpace(firstContact?.HomePhoneNo)
                        ? firstContact.HomePhoneNo.Trim()
                        : !string.IsNullOrWhiteSpace(firstContact?.WorkPhoneNo)
                            ? firstContact.WorkPhoneNo.Trim()
                            : userPhone?.Trim();

            var propertyDetails = new AttrPropertyDetails
            {
                FormType = model.FormType,

                HArea = model.PropertyDetails.HArea,
                DataController = model.PropertyDetails.DataController,
                CollectionBlock = model.PropertyDetails.CollectionBlock,
                DataCollector = model.PropertyDetails.DataCollector,
                SGNumber = model.PropertyDetails.SGNumber,
                Centroid = model.PropertyDetails.Centroid,
                Erf = model.PropertyDetails.Erf,
                Extent = model.PropertyDetails.Extent,
                SectionalTitle = model.PropertyDetails.SectionalTitle,
                LandUseFinancials = model.PropertyDetails.LandUseFinancials,
                Municipality = model.PropertyDetails.Municipality,
                Ward = model.PropertyDetails.Ward,
                Township = model.PropertyDetails.Township,
                Zoning = model.PropertyDetails.Zoning,
                Sources = model.PropertyDetails.Sources,
                Address = model.PropertyDetails.Address,

                CreatedBy = userId,
                CreatedDate = now
            };

            _context.AttrPropertyDetails.Add(propertyDetails);
            await _context.SaveChangesAsync();

            var propertyInfo = new AttrPropertyInfo
            {
                Attr_PropertyDetailsId = propertyDetails.Id,

                Objector_Type = isRepresentativeSubmission
                    ? "Representative"
                    : model.ContactInfos != null && model.ContactInfos.Count > 0
                        ? model.ContactInfos[0].ContactType
                        : "Owner",

                Property_Type = model.FormType,
                Property_Desc = model.PropertyDetails.PropertyDesc,
                Premise_id = model.PropertyDetails.PremiseId,
                Unit_key = model.PropertyDetails.UnitKey,
                Property_id = model.PropertyDetails.PropertyId,
                Valuation_Key = model.PropertyDetails.ValuationKey,
                Sector = submissionSector,
                RollType = model.PropertyDetails.RollType,
                RollDescription = model.PropertyDetails.RollDescription,

                SubmittedByUserId = userId,
                SubmittedByName = userName,
                SubmittedByEmail = submittedByEmail,
                SubmittedByPhone = submittedByPhone,
                SubmissionSource = "Genesis",
                SubmissionDateTime = now,
                ClientComment = model.ClientComment,

                Capturer = string.IsNullOrWhiteSpace(capturerSapNumber)
         ? null
         : capturerSapNumber.Trim(),

                Attr_Status = "EvidenceOpen",
                IsActive = true,

                // Keep the remaining existing properties.
            };

            _context.AttrPropertyInfo.Add(propertyInfo);
            await _context.SaveChangesAsync();

            // Required so computed Attr_No is available immediately.
            await _context.Entry(propertyInfo).ReloadAsync();

            await SaveCommonSectionsAsync(model, propertyDetails.Id, userId);
            await SaveFormSpecificSectionsAsync(model, propertyDetails.Id, userId);

            var evidencePin = GenerateEvidencePin();
            var isAdminCaptured = !string.IsNullOrWhiteSpace(capturerSapNumber);

            // Demo/testing bypass:
            // Admin-captured Attributes can be routed to AIVS immediately instead of
            // waiting for the normal 48-hour client evidence window.
            var bypassEvidenceWindow =
                _bypassAdminEvidenceWindow && isAdminCaptured;

            var evidenceDeadline = bypassEvidenceWindow
      ? now
      : now.AddMinutes(_attributeEvidenceWindowMinutes);

            model.GeneratedEvidencePin = evidencePin;
            model.GeneratedEvidenceDeadline = evidenceDeadline;

            // Generate documents after PIN/deadline is available,
            // so the acknowledgement PDF can display them.
            var documentResult = await _documentService.CreateSubmissionPackageAsync(
                model,
                propertyInfo);

            _context.AttrDeclarations.Add(new AttrDeclaration
            {
                Attr_ID = propertyInfo.Attr_ID,
                Attr_No = propertyInfo.Attr_No,
                Attr_Ref_Signature = propertyInfo.Attr_No,

                Declaration_Text = model.Declaration.DeclarationText,
                Declaration_Accepted = model.Declaration.DeclarationAccepted,
                Declaration_Date = now,

                Signature_Picture = model.Declaration.SignaturePicture,
                Signature_Name = model.Declaration.SignatureName,

                RandomPin = evidencePin,
                EvidencePin = evidencePin,

                PinGeneratedDateTime = now,
                PinExpiryDateTime = evidenceDeadline,
                PinIsActive = true,

                AdditionalEvidenceAllowed = true,
                AdditionalEvidenceDeadline = evidenceDeadline,

                DeclaredByUserId = userId,
                DeclaredByName = userName,
                DeclaredByRole = model.RepresentativeDetails?.IsRepresentative == true
                    ? "Representative"
                    : "Client",

                CreatedBy = userId,
                CreatedDate = now
            });

            _context.AttrFiles.Add(new AttrFiles
            {
                Attr_ID = propertyInfo.Attr_ID,
                Attr_No = propertyInfo.Attr_No,
                Attr_Ref_Files = propertyInfo.Attr_No,

                Files1 = documentResult.Files1,
                Files2 = documentResult.Files2,
                Files3 = documentResult.Files3,
                Files4 = documentResult.Files4,
                Files5 = documentResult.Files5,
                Files6 = documentResult.Files6,
                Files7 = documentResult.Files7,
                Files8 = documentResult.Files8,
                Files9 = documentResult.Files9,
                Files10 = documentResult.Files10,

                Rep_Letter = documentResult.RepLetterFileName,
                Bulk_File_Name = documentResult.PdfFileName,
                Acknowledgement_FileName = documentResult.AcknowledgementFileName,

                Evidence_Count = documentResult.EvidenceCount,
                RootFolder = documentResult.AttrFolderPath,

                UploadedByUserId = userId,
                UploadedByName = userName,
                UploadedByRole = "Client",
                UploadedDateTime = now,

                IsActive = true,
                IsDeleted = false,

                CreatedBy = userId,
                CreatedDate = now,
                UpdatedBy = userId,
                UpdatedDate = now
            });

            propertyInfo.Evidence_Count = documentResult.EvidenceCount;
            propertyInfo.Has_Client_Evidence = documentResult.EvidenceCount > 0;
            propertyInfo.Last_Evidence_Uploaded_DateTime =
                documentResult.EvidenceCount > 0 ? now : null;

            propertyInfo.ClientEvidencePath = documentResult.AttrFolderPath;
            propertyInfo.UpdatedBy = userId;
            propertyInfo.UpdatedDate = now;

            if (model.RepresentativeDetails?.IsRepresentative == true &&
                !string.IsNullOrWhiteSpace(model.RepresentativeDetails.Representative_Name))
            {
                _context.AttrRepresentatives.Add(new AttrRepresentative
                {
                    Attr_ID = propertyInfo.Attr_ID,
                    Attr_No = propertyInfo.Attr_No,
                    IDProperty = model.PropertyDetails.UnitKey
                        ?? model.PropertyDetails.PropertyId
                        ?? model.PropertyDetails.PremiseId,

                    UserID = userId,

                    Representative_Name = model.RepresentativeDetails.Representative_Name,
                    Rep_Postal_1 = model.RepresentativeDetails.Rep_Postal_1,
                    Rep_Postal_2 = model.RepresentativeDetails.Rep_Postal_2,
                    Rep_Postal_3 = model.RepresentativeDetails.Rep_Postal_3,
                    Rep_Postal_4 = model.RepresentativeDetails.Rep_Postal_4,
                    Rep_Postal_5 = model.RepresentativeDetails.Rep_Postal_5,

                    Rep_Home_Phone = model.RepresentativeDetails.Rep_Home_Phone,
                    Rep_Cell_Phone = model.RepresentativeDetails.Rep_Cell_Phone,
                    Rep_Work_Phone = model.RepresentativeDetails.Rep_Work_Phone,
                    Rep_Fax_Phone = model.RepresentativeDetails.Rep_Fax_Phone,
                    Rep_Email = model.RepresentativeDetails.Rep_Email,

                    Auth_Letter_FileName = documentResult.RepLetterFileName,

                    CreatedBy = userId,
                    CreatedDate = now
                });
            }

            await AddAuditAsync(
                propertyInfo.Attr_ID,
                propertyInfo.Attr_No,
                "Submitted",
                null,
                "EvidenceOpen",
                userId,
                userName,
                "Client",
                bypassEvidenceWindow
                    ? "Attribute captured by Administration. Demo evidence-window bypass enabled; submission will route to AIVS immediately."
                    : "Client submitted attribute property information. Evidence upload window is open for 48 hours.");

            await AddAuditAsync(
                propertyInfo.Attr_ID,
                propertyInfo.Attr_No,
                "PDF and Evidence Saved",
                "EvidenceOpen",
                "EvidenceOpen",
                userId,
                userName,
                "Client",
                $"Acknowledgement saved as {documentResult.AcknowledgementFileName}. Evidence files uploaded: {documentResult.EvidenceCount}.");

            await AddAuditAsync(
                propertyInfo.Attr_ID,
                propertyInfo.Attr_No,
                "Declaration Submitted",
                "EvidenceOpen",
                "EvidenceOpen",
                userId,
                userName,
                "Client",
                bypassEvidenceWindow
                    ? "Declaration and signature captured. Administration demo bypass enabled; no 48-hour wait is required."
                    : "Client accepted declaration and signature was captured. Evidence PIN generated for 48 hours.");

            var unitKey = model.PropertyDetails.UnitKey
                ?? model.PropertyDetails.PropertyId
                ?? model.PropertyDetails.PremiseId;

            if (!string.IsNullOrWhiteSpace(unitKey))
            {
                var linkedRecord = await _context.LinkedProperties
                    .FirstOrDefaultAsync(lp =>
                        lp.IDProperty == unitKey &&
                        lp.UserID == userId);

                if (linkedRecord != null)
                    _context.LinkedProperties.Remove(linkedRecord);
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            try
            {
                if (!string.IsNullOrWhiteSpace(submittedByEmail))
                {
                    var acknowledgementPdf = await File.ReadAllBytesAsync(
                        documentResult.AcknowledgementFullPath);

                    var submittedFormPdf = await File.ReadAllBytesAsync(
                        documentResult.PdfFullPath);

                    var clientName = ResolveClientName(model, userName);

                    await _emailService.SendAttributeAcknowledgementAsync(
                        submittedByEmail,
                        clientName,
                        propertyInfo.Attr_No ?? $"ATTR-GV23-{propertyInfo.Attr_ID}",
                        propertyInfo.Property_Desc ?? model.PropertyDetails.PropertyDesc ?? "Property",
                        evidencePin,
                        evidenceDeadline,
                        acknowledgementPdf,
                        submittedFormPdf,
                        documentResult.AcknowledgementFileName,
                        documentResult.PdfFileName,
                        documentResult.AttrFolderPath);
                }
                else
                {
                    _logger.LogWarning(
                        "[Attributes] No client email found for {AttrNo}. Acknowledgement email was not sent.",
                        propertyInfo.Attr_No);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[Attributes] Attribute submission saved, but acknowledgement email failed for {AttrNo}",
                    propertyInfo.Attr_No);
            }

            if (bypassEvidenceWindow)
            {
                try
                {
                    await RouteExpiredEvidenceSubmissionsAsync(
                        $"Administration Demo Bypass ({capturerSapNumber})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[Attributes] Admin demo bypass could not route {AttrNo} immediately. The routing worker will retry.",
                        propertyInfo.Attr_No);
                }
            }

            return propertyInfo.Attr_ID;
        }
        private static string ResolveClientName(
    AttributeSubmissionViewModel model,
    string fallbackName)
        {
            var firstContact = model.ContactInfos?.FirstOrDefault();

            if (firstContact == null)
                return fallbackName;

            if (firstContact.IsCompany &&
                !string.IsNullOrWhiteSpace(firstContact.CompanyName))
            {
                return firstContact.CompanyName.Trim();
            }

            var fullName = string.Join(" ",
                new[]
                {
            firstContact.FirstNames?.Trim(),
            firstContact.LastName?.Trim()
                }.Where(x => !string.IsNullOrWhiteSpace(x)));

            return string.IsNullOrWhiteSpace(fullName)
                ? fallbackName
                : fullName;
        }
        private static string GenerateEvidencePin()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            var random = new Random();

            return new string(Enumerable
                .Repeat(chars, 10)
                .Select(s => s[random.Next(s.Length)])
                .ToArray());
        }
        private async Task<string?> ResolveSectorByTownshipAsync(string? township)
        {
            if (string.IsNullOrWhiteSpace(township))
                return null;

            var cleanedTownship = township.Trim().ToUpper();

            var sector = await _context.Sectors
                .Where(x => x.TOWN_NAME_DESC != null &&
                            x.TOWN_NAME_DESC.Trim().ToUpper() == cleanedTownship)
                .Select(x => x.SECTOR)
                .FirstOrDefaultAsync();

            return string.IsNullOrWhiteSpace(sector)
                ? null
                : sector.Trim();
        }


        public async Task RouteExpiredEvidenceSubmissionsAsync(string performedBy = "System")
        {
            var now = DateTime.Now;

            // The 48-hour period is based on the signed declaration date.
            var expiredDeclarationIds = await _context.AttrDeclarations
                .Where(x =>
                    x.AdditionalEvidenceAllowed == true &&
                    x.AdditionalEvidenceDeadline <= now)
                .Select(x => x.Attr_ID)
                .Distinct()
                .ToListAsync();

            if (expiredDeclarationIds.Count == 0)
                return;

            var expiredItems = await _context.AttrPropertyInfo
                .Include(x => x.PropertyDetails)
                .Where(x =>
                    x.IsActive == true &&
                    x.Attr_Status == "EvidenceOpen" &&
                    expiredDeclarationIds.Contains(x.Attr_ID))
                .ToListAsync();

            foreach (var item in expiredItems)
            {
                var oldStatus = item.Attr_Status;
                var township = item.PropertyDetails?.Township;

                item.EvidenceLockedDateTime = now;

                var sector = !string.IsNullOrWhiteSpace(item.Sector)
                    ? item.Sector.Trim()
                    : await ResolveSectorByTownshipAsync(township);

                if (string.IsNullOrWhiteSpace(sector))
                {
                    item.Attr_Status = "SectorRoutingException";
                    item.RoutingError = $"No sector mapping found for township: {township ?? "NULL"}";
                    item.UpdatedBy = performedBy;
                    item.UpdatedDate = now;

                    await AddAuditAsync(
                        item.Attr_ID,
                        item.Attr_No,
                        "Sector Routing Failed",
                        oldStatus,
                        "SectorRoutingException",
                        performedBy,
                        performedBy,
                        "System",
                        item.RoutingError);

                    continue;
                }

                item.Sector = sector;
                item.RoutedSector = sector;
                item.RoutedToSectorDateTime = now;
                item.Attr_Status = "SectorInbox";
                item.RoutingError = null;
                item.UpdatedBy = performedBy;
                item.UpdatedDate = now;

                await AddAuditAsync(
                    item.Attr_ID,
                    item.Attr_No,
                    "Routed To Sector Inbox",
                    oldStatus,
                    "SectorInbox",
                    performedBy,
                    performedBy,
                    "System",
                    $"Evidence window locked after 48 hours. Township '{township}' routed to sector '{sector}'.");
            }

            var expiredDeclarations = await _context.AttrDeclarations
                .Where(x => expiredDeclarationIds.Contains(x.Attr_ID))
                .ToListAsync();

            foreach (var declaration in expiredDeclarations)
            {
                declaration.AdditionalEvidenceAllowed = false;
                declaration.PinIsActive = false;
            }

            await _context.SaveChangesAsync();
        }
        private async Task SaveCommonSectionsAsync(AttributeSubmissionViewModel model, int propertyDetailsId, string userId)
        {
            _context.AttrValuationDetails.Add(new AttrValuationDetails
            {
                PropertyDetailsId = propertyDetailsId,
                ValuationCategoryOnRoll = model.ValuationDetails.ValuationCategoryOnRoll,
                ActualUse = model.ValuationDetails.ActualUse,
                IsMixedUse = model.ValuationDetails.IsMixedUse,
                AlternateUsages = model.ValuationDetails.AlternateUsages,
                OwnersTitleDeeds = model.ValuationDetails.OwnersTitleDeeds,
                OwnersFinancials = model.ValuationDetails.OwnersFinancials,
                CreatedBy = userId,
                CreatedDate = DateTime.Now
            });

            _context.AttrAccess.Add(new AttrAccess
            {
                PropertyDetailsId = propertyDetailsId,
                AccessType = model.Access.AccessType,
                PermissionStatus = model.Access.PermissionStatus,
                Comments = model.Access.Comments,
                CreatedBy = userId,
                CreatedDate = DateTime.Now
            });

            foreach (var contact in model.ContactInfos)
            {
                if (IsEmptyContact(contact))
                    continue;

                _context.AttrContactInfo.Add(new AttrContactInfo
                {
                    PropertyDetailsId = propertyDetailsId,
                    ContactType = contact.ContactType,
                    IsCompany = contact.IsCompany,
                    CompanyName = contact.CompanyName,
                    CompanyRegistrationNumber = contact.CompanyRegistrationNumber,
                    FirstNames = contact.FirstNames,
                    LastName = contact.LastName,
                    MaidenName = contact.MaidenName,
                    IDNumber = contact.IDNumber,
                    DateOfBirth = contact.DateOfBirth,
                    Gender = contact.Gender,
                    MaritalStatus = contact.MaritalStatus,
                    Citizenship = contact.Citizenship,
                    PhysicalAddress = contact.PhysicalAddress,
                    PostalAddress = contact.PostalAddress,
                    Email = contact.Email,
                    HomePhoneNo = contact.HomePhoneNo,
                    WorkPhoneNo = contact.WorkPhoneNo,
                    CellNo = contact.CellNo,
                    FaxNo = contact.FaxNo,
                    Interviewed = contact.Interviewed,
                    Comments = contact.Comments,
                    CreatedBy = userId,
                    CreatedDate = DateTime.Now
                });
            }

            _context.AttrPrimaryAttributes.Add(new AttrPrimaryAttributes
            {
                PropertyDetailsId = propertyDetailsId,
                Tla1 = model.PrimaryAttributes.Tla1,
                Tla2 = model.PrimaryAttributes.Tla2,
                Tla3 = model.PrimaryAttributes.Tla3,
                Garage = model.PrimaryAttributes.Garage,
                CarportCp = model.PrimaryAttributes.CarportCp,
                GrannyFlatGf = model.PrimaryAttributes.GrannyFlatGf,
                StaffQuartersSq = model.PrimaryAttributes.StaffQuartersSq,
                Storage = model.PrimaryAttributes.Storage,
                AdjustmentFactor = model.PrimaryAttributes.AdjustmentFactor,
                STMain = model.PrimaryAttributes.STMain,
                CreatedBy = userId,
                CreatedDate = DateTime.Now
            });

            _context.AttrSecondaryAttributes.Add(new AttrSecondaryAttributes
            {
                PropertyDetailsId = propertyDetailsId,
                Storeys = model.SecondaryAttributes.Storeys,
                Security = model.SecondaryAttributes.Security,
                Noise = model.SecondaryAttributes.Noise,
                Topography = model.SecondaryAttributes.Topography,
                Quality = model.SecondaryAttributes.Quality,
                Condition = model.SecondaryAttributes.Condition,
                SwimmingPool = model.SecondaryAttributes.SwimmingPool,
                TennisCourt = model.SecondaryAttributes.TennisCourt,
                STCondition = model.SecondaryAttributes.STCondition,
                STFloor = model.SecondaryAttributes.STFloor,
                CreatedBy = userId,
                CreatedDate = DateTime.Now
            });

            _context.AttrCalculations.Add(new AttrCalculations
            {
                PropertyDetailsId = propertyDetailsId,
                CalcUpdateTla = model.Calculations.CalcUpdateTla,
                Tla = model.Calculations.Tla,
                CalcUpdateWgba = model.Calculations.CalcUpdateWgba,
                AdjustedWgba = model.Calculations.AdjustedWgba,
                TotalValueNonRes = model.Calculations.TotalValueNonRes,
                TotalValueUnutilisedLand = model.Calculations.TotalValueUnutilisedLand,
                DRCFinalValue = model.Calculations.DRCFinalValue,
                CalculationStatus = model.Calculations.CalculationStatus,
                CreatedBy = userId,
                CreatedDate = DateTime.Now
            });

            await _context.SaveChangesAsync();
        }

        private async Task SaveFormSpecificSectionsAsync(AttributeSubmissionViewModel model, int propertyDetailsId, string userId)
        {
            if (model.FormType == "BusinessCommercial")
            {
                foreach (var item in model.BusinessBuildings)
                {
                    if (IsEmptyBusinessBuilding(item))
                        continue;

                    _context.AttrBusinessBuildings.Add(new AttrBusinessBuildings
                    {
                        PropertyDetailsId = propertyDetailsId,
                        BuildingNr = item.BuildingNr,
                        Quality = item.Quality,
                        Condition = item.Condition,
                        YearBuilt = item.YearBuilt,
                        Storeys = item.Storeys,
                        Depreciation = item.Depreciation,
                        GBA = item.GBA,
                        Cost = item.Cost,
                        DRC = item.DRC,
                        CreatedBy = userId,
                        CreatedDate = DateTime.Now
                    });
                }

                foreach (var item in model.BusinessSections)
                {
                    if (IsEmptyBusinessSection(item))
                        continue;

                    _context.AttrBusinessSections.Add(new AttrBusinessSections
                    {
                        PropertyDetailsId = propertyDetailsId,
                        BuildingNr = item.BuildingNr,
                        Usage = item.Usage,
                        MarketGroup = item.MarketGroup,
                        Quality = item.Quality,
                        GBA = item.GBA,
                        NLA = item.NLA,
                        CostRate = item.CostRate,
                        Cost = item.Cost,
                        Rental = item.Rental,
                        Vac = item.Vac,
                        Exp = item.Exp,
                        Cap = item.Cap,
                        Gross = item.Gross,
                        Normalised = item.Normalised,
                        Nett = item.Nett,
                        Value = item.Value,
                        CreatedBy = userId,
                        CreatedDate = DateTime.Now
                    });
                }

                _context.AttrBusinessGeneral.Add(new AttrBusinessGeneral
                {
                    PropertyDetailsId = propertyDetailsId,
                    UnutilisedLandExtent = model.BusinessGeneral.UnutilisedLandExtent,
                    UnutilisedLandRate = model.BusinessGeneral.UnutilisedLandRate,
                    CreatedBy = userId,
                    CreatedDate = DateTime.Now
                });
            }

            if (model.FormType == "DRCMethod")
            {
                foreach (var item in model.DrcBuildings)
                {
                    if (IsEmptyDrcBuilding(item))
                        continue;

                    _context.AttrDrcBuildings.Add(new AttrDrcBuildings
                    {
                        PropertyDetailsId = propertyDetailsId,
                        BuildingDescription = item.BuildingDescription,
                        Quality = item.Quality,
                        GrossBuildingArea = item.GrossBuildingArea,
                        Condition = item.Condition,
                        DepreciationPercentage = item.DepreciationPercentage,
                        RatePerSQM = item.RatePerSQM,
                        DepreciatedRate = item.DepreciatedRate,
                        ReplacementCost = item.ReplacementCost,
                        CreatedBy = userId,
                        CreatedDate = DateTime.Now
                    });
                }

                foreach (var item in model.DrcImprovements)
                {
                    if (IsEmptyDrcImprovement(item))
                        continue;

                    _context.AttrDrcImprovements.Add(new AttrDrcImprovements
                    {
                        PropertyDetailsId = propertyDetailsId,
                        ImprovementDescription = item.ImprovementDescription,
                        Quality = item.Quality,
                        AreaUnit = item.AreaUnit,
                        Condition = item.Condition,
                        DepreciationPercentage = item.DepreciationPercentage,
                        RatePerSQM = item.RatePerSQM,
                        DepreciatedRate = item.DepreciatedRate,
                        ReplacementCost = item.ReplacementCost,
                        CreatedBy = userId,
                        CreatedDate = DateTime.Now
                    });
                }

                foreach (var item in model.DrcVacantLands)
                {
                    if (IsEmptyDrcVacantLand(item))
                        continue;

                    _context.AttrDrcVacantLand.Add(new AttrDrcVacantLand
                    {
                        PropertyDetailsId = propertyDetailsId,
                        Region = item.Region,
                        MinRatePerSQM = item.MinRatePerSQM,
                        MidRatePerSQM = item.MidRatePerSQM,
                        MaxRatePerSQM = item.MaxRatePerSQM,
                        Area = item.Area,
                        Rate = item.Rate,
                        VacantLandCost = item.VacantLandCost,
                        CreatedBy = userId,
                        CreatedDate = DateTime.Now
                    });
                }

                _context.AttrDrcMarketValueDemolition.Add(new AttrDrcMarketValueDemolition
                {
                    PropertyDetailsId = propertyDetailsId,
                    DemolitionRate = model.DrcMarketValueDemolition.DemolitionRate,
                    MarketValue = model.DrcMarketValueDemolition.MarketValue,
                    MarketValueAfterDemolition = model.DrcMarketValueDemolition.MarketValueAfterDemolition,
                    CreatedBy = userId,
                    CreatedDate = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();
        }




        public async Task<AttributeSubmissionViewModel?> GetForReviewAsync(long attrId)
        {
            var info = await _context.AttrPropertyInfo
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.ContactInfos)
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.BusinessBuildings)
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.BusinessSections)
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.DrcBuildings)
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.DrcImprovements)
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.DrcVacantLands)
                .FirstOrDefaultAsync(x => x.Attr_ID == attrId);

            if (info?.PropertyDetails == null)
                return null;

            var pd = info.PropertyDetails;

            var vm = new AttributeSubmissionViewModel
            {
                AttrId = info.Attr_ID,
                AttrNo = info.Attr_No,
                FormType = pd.FormType,
                ClientComment = info.ClientComment,

                PropertyDetails = new AttributePropertyDetailsVm
                {
                    HArea = pd.HArea,
                    DataController = pd.DataController,
                    CollectionBlock = pd.CollectionBlock,
                    DataCollector = pd.DataCollector,
                    SGNumber = pd.SGNumber,
                    Centroid = pd.Centroid,
                    Erf = pd.Erf,
                    Extent = pd.Extent,
                    SectionalTitle = pd.SectionalTitle,
                    LandUseFinancials = pd.LandUseFinancials,
                    Municipality = pd.Municipality,
                    Ward = pd.Ward,
                    Township = pd.Township,
                    Zoning = pd.Zoning,
                    Sources = pd.Sources,
                    Address = pd.Address,
                    PropertyDesc = info.Property_Desc,
                    PremiseId = info.Premise_id,
                    UnitKey = info.Unit_key,
                    PropertyId = info.Property_id,
                    ValuationKey = info.Valuation_Key,
                    Sector = info.Sector,
                    RollType = info.RollType,
                    RollDescription = info.RollDescription
                }
            };

            return vm;
        }

        public async Task AssignToValuerAsync(long attrId, string valuerUserId, string valuerName, string assignedBy, string? comment)
        {
            var item = await _context.AttrPropertyInfo.FirstOrDefaultAsync(x => x.Attr_ID == attrId);

            if (item == null)
                throw new InvalidOperationException("Attribute submission was not found.");

            var oldStatus = item.Attr_Status;

            item.Task_Assigned_To_UserId = valuerUserId;
            item.Task_Assigned_To = valuerName;
            item.Task_Assigned_DateTime = DateTime.Now;
            item.Task_Assigner = assignedBy;
            item.TaskAssignerComment = comment;
            item.Attr_Status = "Assigned";
            item.UpdatedBy = assignedBy;
            item.UpdatedDate = DateTime.Now;

            await AddAuditAsync(
                item.Attr_ID,
                item.Attr_No,
                "Assigned",
                oldStatus,
                "Assigned",
                assignedBy,
                assignedBy,
                "Manager",
                comment ?? $"Assigned to {valuerName}");

            await _context.SaveChangesAsync();
        }

        public async Task ValuerDecisionAsync(
            long attrId,
            string decision,
            string valuerUserId,
            string valuerName,
            string? comment,
            string? rejectionReason)
        {
            var item = await _context.AttrPropertyInfo.FirstOrDefaultAsync(x => x.Attr_ID == attrId);

            if (item == null)
                throw new InvalidOperationException("Attribute submission was not found.");

            if (decision == "Rejected" && string.IsNullOrWhiteSpace(rejectionReason))
                throw new InvalidOperationException("Rejection reason is required.");

            var oldStatus = item.Attr_Status;

            item.Valuer = valuerName;
            item.ValuerUserId = valuerUserId;
            item.ValuerComment = comment;
            item.ValuerDecision = decision;
            item.RejectionReason = rejectionReason;
            item.ValuerDecisionDateTime = DateTime.Now;
            item.Attr_Status = decision;

            if (decision == "Approved")
            {
                item.ReadyForOvvioExtract = true;
                item.OvvioExtractStatus = "Pending";
            }

            item.UpdatedBy = valuerUserId;
            item.UpdatedDate = DateTime.Now;

            await AddAuditAsync(
                item.Attr_ID,
                item.Attr_No,
                decision,
                oldStatus,
                decision,
                valuerUserId,
                valuerName,
                "Valuer",
                decision == "Rejected" ? rejectionReason : comment);

            await _context.SaveChangesAsync();
        }

        public async Task WithdrawAsync(long attrId, string userId, string userName, string reason)
        {
            var item = await _context.AttrPropertyInfo.FirstOrDefaultAsync(x => x.Attr_ID == attrId);

            if (item == null)
                throw new InvalidOperationException("Attribute submission was not found.");

            var oldStatus = item.Attr_Status;

            item.IsWithdrawn = true;
            item.WithdrawnByUserId = userId;
            item.WithdrawnByName = userName;
            item.WithdrawnDateTime = DateTime.Now;
            item.WithdrawalReason = reason;
            item.Attr_Status = "Withdrawn";
            item.UpdatedBy = userId;
            item.UpdatedDate = DateTime.Now;

            _context.AttrWithdrawals.Add(new AttrWithdrawals
            {
                Attr_ID = item.Attr_ID,
                Attr_No = item.Attr_No,
                Attribute_Withdrawn = "Full Submission",
                WithdrawalReason = reason,
                WithdrawnByUserId = userId,
                WithdrawnByName = userName,
                WithdrawnByRole = "Client",
                WithdrawalStatus = "Withdrawn",
                DateWithdrawn = DateTime.Now
            });

            await AddAuditAsync(
                item.Attr_ID,
                item.Attr_No,
                "Withdrawn",
                oldStatus,
                "Withdrawn",
                userId,
                userName,
                "Client",
                reason);

            await _context.SaveChangesAsync();
        }

        private async Task AddAuditAsync(
            long attrId,
            string? attrNo,
            string action,
            string? oldStatus,
            string? newStatus,
            string? userId,
            string? userName,
            string role,
            string? comment)
        {
            _context.AttrPropertyInfoAuditTrail.Add(new AttrPropertyInfoAuditTrail
            {
                Attr_ID = attrId,
                Attr_No = attrNo,
                Action = action,
                OldStatus = oldStatus,
                NewStatus = newStatus,
                ActionByUserId = userId,
                ActionByName = userName,
                ActionRole = role,
                Comment = comment,
                ActionDateTime = DateTime.Now
            });

            await Task.CompletedTask;
        }

        private static bool IsEmptyContact(AttributeContactInfoVm x)
        {
            return string.IsNullOrWhiteSpace(x.FirstNames)
                   && string.IsNullOrWhiteSpace(x.LastName)
                   && string.IsNullOrWhiteSpace(x.CompanyName)
                   && string.IsNullOrWhiteSpace(x.IDNumber)
                   && string.IsNullOrWhiteSpace(x.Email);
        }

        private static bool IsEmptyBusinessBuilding(AttributeBusinessBuildingVm x)
        {
            return string.IsNullOrWhiteSpace(x.BuildingNr)
                   && string.IsNullOrWhiteSpace(x.Quality)
                   && string.IsNullOrWhiteSpace(x.Condition)
                   && x.GBA == null
                   && x.Cost == null;
        }

        private static bool IsEmptyBusinessSection(AttributeBusinessSectionVm x)
        {
            return string.IsNullOrWhiteSpace(x.BuildingNr)
                   && string.IsNullOrWhiteSpace(x.Usage)
                   && string.IsNullOrWhiteSpace(x.MarketGroup)
                   && x.GBA == null
                   && x.NLA == null
                   && x.Value == null;
        }

        private static bool IsEmptyDrcBuilding(AttributeDrcBuildingVm x)
        {
            return string.IsNullOrWhiteSpace(x.BuildingDescription)
                   && x.GrossBuildingArea == null
                   && x.ReplacementCost == null;
        }

        private static bool IsEmptyDrcImprovement(AttributeDrcImprovementVm x)
        {
            return string.IsNullOrWhiteSpace(x.ImprovementDescription)
                   && x.AreaUnit == null
                   && x.ReplacementCost == null;
        }

        private static bool IsEmptyDrcVacantLand(AttributeDrcVacantLandVm x)
        {
            return string.IsNullOrWhiteSpace(x.Region)
                   && x.Area == null
                   && x.Rate == null
                   && x.VacantLandCost == null;
        }


        public async Task<AttributeAcknowledgementVm?> GetAcknowledgementAsync(long attrId)
        {
            var info = await _context.AttrPropertyInfo
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.ValuationDetails)
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.Calculations)
                .FirstOrDefaultAsync(x => x.Attr_ID == attrId);

            if (info == null)
                return null;

            var declaration = await _context.AttrDeclarations
                .FirstOrDefaultAsync(x => x.Attr_ID == attrId);

            var files = await _context.AttrFiles
                .FirstOrDefaultAsync(x => x.Attr_ID == attrId);

            var uploadedDocs = new List<string>();

            if (!string.IsNullOrWhiteSpace(files?.Files1)) uploadedDocs.Add(files.Files1);
            if (!string.IsNullOrWhiteSpace(files?.Files2)) uploadedDocs.Add(files.Files2);
            if (!string.IsNullOrWhiteSpace(files?.Files3)) uploadedDocs.Add(files.Files3);
            if (!string.IsNullOrWhiteSpace(files?.Files4)) uploadedDocs.Add(files.Files4);
            if (!string.IsNullOrWhiteSpace(files?.Files5)) uploadedDocs.Add(files.Files5);
            if (!string.IsNullOrWhiteSpace(files?.Files6)) uploadedDocs.Add(files.Files6);
            if (!string.IsNullOrWhiteSpace(files?.Files7)) uploadedDocs.Add(files.Files7);
            if (!string.IsNullOrWhiteSpace(files?.Files8)) uploadedDocs.Add(files.Files8);
            if (!string.IsNullOrWhiteSpace(files?.Files9)) uploadedDocs.Add(files.Files9);
            if (!string.IsNullOrWhiteSpace(files?.Files10)) uploadedDocs.Add(files.Files10);

            var submission = await BuildSubmittedAttributeViewModelAsync(attrId);

            return new AttributeAcknowledgementVm
            {
                AttrId = info.Attr_ID,
                AttrNo = info.Attr_No,

                PropertyDescription = info.Property_Desc,
                PropertyCategory = info.PropertyDetails?.ValuationDetails?.ValuationCategoryOnRoll,
                PhysicalAddress = info.PropertyDetails?.Address,

                MarketValue = info.PropertyDetails?.Calculations?.Tla?.ToString(),
                Extent = info.PropertyDetails?.Extent,

                OwnerName = info.PropertyDetails?.ValuationDetails?.OwnersFinancials
                            ?? info.PropertyDetails?.ValuationDetails?.OwnersTitleDeeds,

                Pin = declaration?.EvidencePin ?? declaration?.RandomPin,

                SubmissionDate = info.SubmissionDateTime,

                EvidenceDeadline = declaration?.AdditionalEvidenceDeadline
                                   ?? info.SubmissionDateTime.AddHours(48),

                EvidenceCount = files?.Evidence_Count ?? 0,

                AcknowledgementFileName = files?.Acknowledgement_FileName,

                AcknowledgementPath = files == null || string.IsNullOrWhiteSpace(files.Acknowledgement_FileName)
                    ? null
                    : Path.Combine(files.RootFolder ?? "", files.Acknowledgement_FileName),

                UploadedDocuments = uploadedDocs,

                // New: full submitted form data for the HTML acknowledgement display
                Submission = submission
            };

        }
        public async Task<AttributeSubmissionViewModel?> GetSubmittedViewAsync(
            string attrNo,
            string userId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(attrNo)
                || string.IsNullOrWhiteSpace(userId))
            {
                return null;
            }

            var submission = await _context.AttrPropertyInfo
                .AsNoTracking()
                .Where(x =>
                    x.Attr_No == attrNo.Trim()
                    && x.SubmittedByUserId == userId
                    && x.IsActive)
                .Select(x => new
                {
                    x.Attr_ID
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (submission is null)
                return null;

            return await BuildSubmittedAttributeViewModelAsync(
                submission.Attr_ID,
                cancellationToken);
        }

        private async Task<AttributeSubmissionViewModel?> BuildSubmittedAttributeViewModelAsync(
            long attrId,
            CancellationToken cancellationToken = default)
        {
            var info = await _context.AttrPropertyInfo
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.ValuationDetails)
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.Calculations)
                .FirstOrDefaultAsync(x => x.Attr_ID == attrId, cancellationToken);

            if (info?.PropertyDetails == null)
                return null;

            var property = info.PropertyDetails;
            var propertyDetailsId = property.Id;

            var valuation = property.ValuationDetails;
            var calculations = property.Calculations;

            var access = await _context.AttrAccess
                .FirstOrDefaultAsync(x => x.PropertyDetailsId == propertyDetailsId, cancellationToken);

            var declaration = await _context.AttrDeclarations
                .FirstOrDefaultAsync(x => x.Attr_ID == attrId, cancellationToken);

            var contacts = await _context.AttrContactInfo
                .Where(x => x.PropertyDetailsId == propertyDetailsId)
                .ToListAsync(cancellationToken);

            var primary = await _context.AttrPrimaryAttributes
                .FirstOrDefaultAsync(x => x.PropertyDetailsId == propertyDetailsId, cancellationToken);

            var secondary = await _context.AttrSecondaryAttributes
                .FirstOrDefaultAsync(x => x.PropertyDetailsId == propertyDetailsId, cancellationToken);

            var businessBuildings = await _context.AttrBusinessBuildings
                .Where(x => x.PropertyDetailsId == propertyDetailsId)
                .ToListAsync(cancellationToken);

            var businessSections = await _context.AttrBusinessSections
                .Where(x => x.PropertyDetailsId == propertyDetailsId)
                .ToListAsync(cancellationToken);

            var businessGeneral = await _context.AttrBusinessGeneral
                .FirstOrDefaultAsync(x => x.PropertyDetailsId == propertyDetailsId, cancellationToken);

            var drcBuildings = await _context.AttrDrcBuildings
                .Where(x => x.PropertyDetailsId == propertyDetailsId)
                .ToListAsync(cancellationToken);

            var drcImprovements = await _context.AttrDrcImprovements
                .Where(x => x.PropertyDetailsId == propertyDetailsId)
                .ToListAsync(cancellationToken);

            var drcVacantLands = await _context.AttrDrcVacantLand
                .Where(x => x.PropertyDetailsId == propertyDetailsId)
                .ToListAsync(cancellationToken);

            var drcMarketValue = await _context.AttrDrcMarketValueDemolition
                .FirstOrDefaultAsync(x => x.PropertyDetailsId == propertyDetailsId, cancellationToken);

            var storedFiles =
    await _context.AttrFiles
        .AsNoTracking()
        .FirstOrDefaultAsync(
            x =>
                x.Attr_ID == attrId &&
                x.IsActive,
            cancellationToken);

            var storedUploadedFileNames =
                new[]
                {
        storedFiles?.Rep_Letter,
        storedFiles?.Files1,
        storedFiles?.Files2,
        storedFiles?.Files3,
        storedFiles?.Files4,
        storedFiles?.Files5,
        storedFiles?.Files6,
        storedFiles?.Files7,
        storedFiles?.Files8,
        storedFiles?.Files9,
        storedFiles?.Files10
                }
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();


            var model = new AttributeSubmissionViewModel
            {
                AttrId = info.Attr_ID,
                AttrNo = info.Attr_No,

                // In your SubmitAsync, FormType is saved on AttrPropertyDetails.FormType
                // and also Property_Type on AttrPropertyInfo.
                FormType = property.FormType ?? info.Property_Type,

                ClientComment = info.ClientComment,

                PropertyDetails = new AttributePropertyDetailsVm
                {
                    PropertyId = info.Property_id,
                    PremiseId = info.Premise_id,
                    UnitKey = info.Unit_key,
                    ValuationKey = info.Valuation_Key,
                    Sector = info.Sector,
                    RollType = info.RollType,
                    RollDescription = info.RollDescription,

                    HArea = property.HArea,
                    CollectionBlock = property.CollectionBlock,
                    DataController = property.DataController,
                    DataCollector = property.DataCollector,
                    SGNumber = property.SGNumber,
                    Centroid = property.Centroid,
                    Erf = property.Erf,
                    Extent = property.Extent,
                    SectionalTitle = property.SectionalTitle,
                    LandUseFinancials = property.LandUseFinancials,
                    Municipality = property.Municipality,
                    Ward = property.Ward,
                    Township = property.Township,
                    Zoning = property.Zoning,
                    Sources = property.Sources,
                    Address = property.Address,
                    PropertyDesc = info.Property_Desc
                },

                ValuationDetails = new AttributeValuationDetailsVm
                {
                    ValuationCategoryOnRoll = valuation?.ValuationCategoryOnRoll,
                    ActualUse = valuation?.ActualUse,
                    IsMixedUse = valuation?.IsMixedUse ?? false,
                    AlternateUsages = valuation?.AlternateUsages,
                    OwnersTitleDeeds = valuation?.OwnersTitleDeeds,
                    OwnersFinancials = valuation?.OwnersFinancials
                },

                Access = new AttributeAccessVm
                {
                    AccessType = access?.AccessType,
                    PermissionStatus = access?.PermissionStatus,
                    Comments = access?.Comments
                },

                PrimaryAttributes = new AttributePrimaryAttributesVm
                {
                    Tla1 = primary?.Tla1,
                    Tla2 = primary?.Tla2,
                    Tla3 = primary?.Tla3,
                    Garage = primary?.Garage,
                    CarportCp = primary?.CarportCp,
                    GrannyFlatGf = primary?.GrannyFlatGf,
                    StaffQuartersSq = primary?.StaffQuartersSq,
                    Storage = primary?.Storage,
                    AdjustmentFactor = primary?.AdjustmentFactor,
                    STMain = primary?.STMain
                },

                SecondaryAttributes = new AttributeSecondaryAttributesVm
                {
                    Storeys = secondary?.Storeys,
                    Security = secondary?.Security,
                    Noise = secondary?.Noise,
                    Topography = secondary?.Topography,
                    Quality = secondary?.Quality,
                    Condition = secondary?.Condition,
                    SwimmingPool = secondary?.SwimmingPool,
                    TennisCourt = secondary?.TennisCourt,
                    STCondition = secondary?.STCondition,
                    STFloor = secondary?.STFloor
                },

                Calculations = new AttributeCalculationsVm
                {
                    CalcUpdateTla = calculations?.CalcUpdateTla,
                    Tla = calculations?.Tla,
                    CalcUpdateWgba = calculations?.CalcUpdateWgba,
                    AdjustedWgba = calculations?.AdjustedWgba,
                    TotalValueNonRes = calculations?.TotalValueNonRes,
                    TotalValueUnutilisedLand = calculations?.TotalValueUnutilisedLand,
                    DRCFinalValue = calculations?.DRCFinalValue,
                    CalculationStatus = calculations?.CalculationStatus
                },

                BusinessGeneral = new AttributeBusinessGeneralVm
                {
                    UnutilisedLandExtent = businessGeneral?.UnutilisedLandExtent,
                    UnutilisedLandRate = businessGeneral?.UnutilisedLandRate
                },

                DrcMarketValueDemolition = new AttributeDrcMarketValueDemolitionVm
                {
                    DemolitionRate = drcMarketValue?.DemolitionRate,
                    MarketValue = drcMarketValue?.MarketValue,
                    MarketValueAfterDemolition = drcMarketValue?.MarketValueAfterDemolition
                },

                Declaration = new AttributeDeclarationVm
                {
                    DeclarationAccepted = declaration?.Declaration_Accepted ?? false,
                    SignatureName = declaration?.Signature_Name,
                    SignaturePicture = declaration?.Signature_Picture,
                    DeclarationText = declaration?.Declaration_Text
                },

                ContactInfos = contacts.Select(c => new AttributeContactInfoVm
                {
                    ContactType = c.ContactType,
                    IsCompany = c.IsCompany,
                    CompanyName = c.CompanyName,
                    CompanyRegistrationNumber = c.CompanyRegistrationNumber,
                    FirstNames = c.FirstNames,
                    LastName = c.LastName,
                    MaidenName = c.MaidenName,
                    IDNumber = c.IDNumber,
                    DateOfBirth = c.DateOfBirth,
                    Gender = c.Gender,
                    MaritalStatus = c.MaritalStatus,
                    Citizenship = c.Citizenship,
                    PhysicalAddress = c.PhysicalAddress,
                    PostalAddress = c.PostalAddress,
                    Email = c.Email,
                    HomePhoneNo = c.HomePhoneNo,
                    WorkPhoneNo = c.WorkPhoneNo,
                    CellNo = c.CellNo,
                    FaxNo = c.FaxNo,
                    Interviewed = c.Interviewed,
                    Comments = c.Comments
                }).ToList(),

                BusinessBuildings = businessBuildings.Select(b => new AttributeBusinessBuildingVm
                {
                    BuildingNr = b.BuildingNr,
                    Quality = b.Quality,
                    Condition = b.Condition,
                    YearBuilt = b.YearBuilt,
                    Storeys = b.Storeys,
                    Depreciation = b.Depreciation,
                    GBA = b.GBA,
                    Cost = b.Cost,
                    DRC = b.DRC
                }).ToList(),

                BusinessSections = businessSections.Select(s => new AttributeBusinessSectionVm
                {
                    BuildingNr = s.BuildingNr,
                    Usage = s.Usage,
                    MarketGroup = s.MarketGroup,
                    Quality = s.Quality,
                    GBA = s.GBA,
                    NLA = s.NLA,
                    CostRate = s.CostRate,
                    Cost = s.Cost,
                    Rental = s.Rental,
                    Vac = s.Vac,
                    Exp = s.Exp,
                    Cap = s.Cap,
                    Gross = s.Gross,
                    Normalised = s.Normalised,
                    Nett = s.Nett,
                    Value = s.Value
                }).ToList(),

                DrcBuildings = drcBuildings.Select(b => new AttributeDrcBuildingVm
                {
                    BuildingDescription = b.BuildingDescription,
                    Quality = b.Quality,
                    GrossBuildingArea = b.GrossBuildingArea,
                    Condition = b.Condition,
                    DepreciationPercentage = b.DepreciationPercentage,
                    RatePerSQM = b.RatePerSQM,
                    DepreciatedRate = b.DepreciatedRate,
                    ReplacementCost = b.ReplacementCost
                }).ToList(),

                DrcImprovements = drcImprovements.Select(i => new AttributeDrcImprovementVm
                {
                    ImprovementDescription = i.ImprovementDescription,
                    Quality = i.Quality,
                    AreaUnit = i.AreaUnit,
                    Condition = i.Condition,
                    DepreciationPercentage = i.DepreciationPercentage,
                    RatePerSQM = i.RatePerSQM,
                    DepreciatedRate = i.DepreciatedRate,
                    ReplacementCost = i.ReplacementCost
                }).ToList(),

                DrcVacantLands = drcVacantLands.Select(v => new AttributeDrcVacantLandVm
                {
                    Region = v.Region,
                    MinRatePerSQM = v.MinRatePerSQM,
                    MidRatePerSQM = v.MidRatePerSQM,
                    MaxRatePerSQM = v.MaxRatePerSQM,
                    Area = v.Area,
                    Rate = v.Rate,
                    VacantLandCost = v.VacantLandCost
                }).ToList(),
                Files = new AttributeFilesVm
                {
                    UploadedFileNames =
        storedUploadedFileNames
                },



            };

            return model;
        }

        public async Task<ReturnedAttributeCorrectionViewModel?> GetReturnedCorrectionAsync(
        long attrId,
        string userId,
        CancellationToken cancellationToken = default)
        {
            if (attrId <= 0 || string.IsNullOrWhiteSpace(userId))
                return null;

            var info = await _context.AttrPropertyInfo
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Attr_ID == attrId
                         && x.SubmittedByUserId == userId
                         && x.IsActive
                         && x.Attr_Status == "ReturnedToClient"
                         && x.RevisionRequired,
                    cancellationToken);

            if (info is null)
                return null;

            var review = await _context.AttrValuerReviews
                .AsNoTracking()
                .Where(x =>
                    x.Attr_ID == attrId &&
                    x.ReviewStatus == "ReturnedToClient")
                .OrderByDescending(x => x.CompletedAt ?? x.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (review is null)
                return null;

            // Old / section-based correction flow.
            var sections = await _context.AttrValuerReviewSections
                .AsNoTracking()
                .Where(x =>
                    x.ReviewId == review.Id &&
                    (x.RequiresCorrection ||
                     x.SectionDecision == "Needs correction"))
                .OrderBy(x => x.Id)
                .Select(x => new ReturnedAttributeCorrectionSectionVm
                {
                    Code = x.SectionCode,
                    Name = x.SectionName,
                    Comment = x.SectionComment ?? string.Empty
                })
                .ToListAsync(cancellationToken);

            // New AIVS field-based correction flow.
            var fields = await _context.AttrValuerReviewFieldCorrections
                .AsNoTracking()
                .Where(x =>
                    x.ReviewId == review.Id &&
                    x.Attr_ID == attrId &&
                    x.IsActive)
                .OrderBy(x => x.SectionCode)
                .ThenBy(x => x.Id)
                .Select(x => new ReturnedAttributeCorrectionFieldVm
                {
                    SectionCode = x.SectionCode,
                    FieldCode = x.FieldCode,
                    FieldLabel = x.FieldLabel,
                    CityValue = x.CityValue,
                    ClientValue = x.ClientValue
                })
                .ToListAsync(cancellationToken);

            // AIVS now returns individual fields. Create their parent sections
            // so CorrectReturned.cshtml can render them.
            foreach (var sectionCode in fields
                .Select(x => x.SectionCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (sections.Any(x =>
                    string.Equals(
                        x.Code,
                        sectionCode,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                sections.Add(new ReturnedAttributeCorrectionSectionVm
                {
                    Code = sectionCode,
                    Name = GetCorrectionSectionDisplayName(sectionCode),
                    Comment = review.FinalComment ?? string.Empty
                });
            }

            // Only reject the request when AIVS supplied neither a returned
            // section nor an individual returned field.
            if (sections.Count == 0 && fields.Count == 0)
                return null;

            var submission = await BuildSubmittedAttributeViewModelAsync(
                attrId,
                cancellationToken);

            if (submission is null)
                return null;

            return new ReturnedAttributeCorrectionViewModel
            {
                AttrId = info.Attr_ID,
                AttrNo = info.Attr_No ?? string.Empty,
                PropertyDescription = info.Property_Desc ?? string.Empty,
                FormType = info.Property_Type,

                RevisionReason =
                    info.RevisionReason
                    ?? review.FinalComment
                    ?? string.Empty,

                RequestedAt = info.RevisionRequestedDateTime,
                RequestedBy = info.RevisionRequestedBy ?? string.Empty,

                Submission = submission,

                Sections = sections
                    .OrderBy(x => x.Name)
                    .ToList(),

                Fields = fields
            };
        }
        public async Task ResubmitReturnedCorrectionAsync(
            ReturnedAttributeCorrectionViewModel model,
            string userId,
            string userName,
            CancellationToken cancellationToken = default)
        {
            if (model.AttrId <= 0)
                throw new InvalidOperationException("Invalid attribute submission.");

            if (string.IsNullOrWhiteSpace(model.RevisionComment))
                throw new InvalidOperationException("Please explain what you corrected.");

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var info = await _context.AttrPropertyInfo
                .Include(x => x.PropertyDetails)
                .FirstOrDefaultAsync(
                    x => x.Attr_ID == model.AttrId
                         && x.SubmittedByUserId == userId
                         && x.IsActive,
                    cancellationToken);

            if (info?.PropertyDetails is null)
                throw new InvalidOperationException("The attribute submission could not be found.");

            if (!string.Equals(info.Attr_Status, "ReturnedToClient", StringComparison.OrdinalIgnoreCase)
                || !info.RevisionRequired)
            {
                throw new InvalidOperationException("This submission is no longer available for correction.");
            }

            var review = await _context.AttrValuerReviews
                .Where(x => x.Attr_ID == info.Attr_ID && x.ReviewStatus == "ReturnedToClient")
                .OrderByDescending(x => x.CompletedAt ?? x.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (review is null)
                throw new InvalidOperationException("The valuer correction request could not be found.");

            var allowedCodeRows = await _context.AttrValuerReviewSections
       .Where(x =>
           x.ReviewId == review.Id &&
           (x.RequiresCorrection ||
            x.SectionDecision == "Needs correction"))
       .Select(x => x.SectionCode)
       .ToListAsync(cancellationToken);

            var returnedFieldRows = await _context
                .AttrValuerReviewFieldCorrections
                .AsNoTracking()
                .Where(x =>
                    x.ReviewId == review.Id &&
                    x.Attr_ID == info.Attr_ID &&
                    x.IsActive)
                .Select(x => new
                {
                    x.SectionCode,
                    x.FieldCode
                })
                .ToListAsync(cancellationToken);

            var allowedCodes = allowedCodeRows
                .Concat(returnedFieldRows.Select(x => x.SectionCode))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (allowedCodes.Count == 0)
            {
                throw new InvalidOperationException(
                    "The valuer did not identify any fields or sections for correction.");
            }

            var propertyId = info.PropertyDetails.Id;
            var posted = model.Submission ?? new AttributeSubmissionViewModel();
            posted.FormType = info.Property_Type;
            CleanSubmission(posted);

            if (posted.ContactInfos.Count > 10
                || posted.BusinessBuildings.Count > 50
                || posted.BusinessSections.Count > 100
                || posted.DrcBuildings.Count > 50
                || posted.DrcImprovements.Count > 50
                || posted.DrcVacantLands.Count > 50)
            {
                throw new InvalidOperationException("The correction contains too many form rows.");
            }

            var now = DateTime.Now;

            if (allowedCodes.Contains("PROPERTY_DETAILS"))
            {
                CopyMatchingValues(posted.PropertyDetails, info.PropertyDetails);
                info.Property_Desc = posted.PropertyDetails.PropertyDesc ?? info.Property_Desc;
                StampUpdated(info.PropertyDetails, userId, now);
            }

            if (allowedCodes.Contains("VALUATION_DETAILS"))
            {
                var row = await _context.AttrValuationDetails
                    .FirstOrDefaultAsync(x => x.PropertyDetailsId == propertyId, cancellationToken);
                if (row is null)
                {
                    row = new AttrValuationDetails { PropertyDetailsId = propertyId, CreatedBy = userId, CreatedDate = now };
                    _context.AttrValuationDetails.Add(row);
                }
                CopyMatchingValues(posted.ValuationDetails, row);
                StampUpdated(row, userId, now);
            }

            if (allowedCodes.Contains("CONTACT_INFORMATION"))
                await ReplaceRowsAsync(_context.AttrContactInfo, propertyId, posted.ContactInfos, userId, now, cancellationToken);

            if (allowedCodes.Contains("ACCESS_INFORMATION"))
            {
                var row = await _context.AttrAccess
                    .FirstOrDefaultAsync(x => x.PropertyDetailsId == propertyId, cancellationToken);
                if (row is null)
                {
                    row = new AttrAccess { PropertyDetailsId = propertyId, CreatedBy = userId, CreatedDate = now };
                    _context.AttrAccess.Add(row);
                }
                CopyMatchingValues(posted.Access, row);
                StampUpdated(row, userId, now);
            }

            if (allowedCodes.Contains("PRIMARY_ATTRIBUTES"))
                await UpdateSingleAsync(_context.AttrPrimaryAttributes, propertyId, posted.PrimaryAttributes, userId, now, cancellationToken);

            if (allowedCodes.Contains("SECONDARY_ATTRIBUTES"))
                await UpdateSingleAsync(_context.AttrSecondaryAttributes, propertyId, posted.SecondaryAttributes, userId, now, cancellationToken);

            if (allowedCodes.Contains("BUSINESS_BUILDINGS"))
                await ReplaceRowsAsync(_context.AttrBusinessBuildings, propertyId, posted.BusinessBuildings, userId, now, cancellationToken);

            if (allowedCodes.Contains("BUSINESS_SECTIONS"))
                await ReplaceRowsAsync(_context.AttrBusinessSections, propertyId, posted.BusinessSections, userId, now, cancellationToken);

            if (allowedCodes.Contains("BUSINESS_GENERAL"))
                await UpdateSingleAsync(_context.AttrBusinessGeneral, propertyId, posted.BusinessGeneral, userId, now, cancellationToken);

            if (allowedCodes.Contains("DRC_BUILDINGS"))
                await ReplaceRowsAsync(_context.AttrDrcBuildings, propertyId, posted.DrcBuildings, userId, now, cancellationToken);

            if (allowedCodes.Contains("DRC_IMPROVEMENTS"))
                await ReplaceRowsAsync(_context.AttrDrcImprovements, propertyId, posted.DrcImprovements, userId, now, cancellationToken);

            if (allowedCodes.Contains("DRC_VACANT_LAND"))
                await ReplaceRowsAsync(_context.AttrDrcVacantLand, propertyId, posted.DrcVacantLands, userId, now, cancellationToken);

            if (allowedCodes.Contains("DRC_MARKET_VALUE"))
                await UpdateSingleAsync(_context.AttrDrcMarketValueDemolition, propertyId, posted.DrcMarketValueDemolition, userId, now, cancellationToken);

            if (allowedCodes.Contains("CALCULATIONS"))
                await UpdateSingleAsync(_context.AttrCalculations, propertyId, posted.Calculations, userId, now, cancellationToken);

            if (allowedCodes.Contains("DECLARATION"))
            {
                var declaration = await _context.AttrDeclarations
                    .FirstOrDefaultAsync(x => x.Attr_ID == info.Attr_ID, cancellationToken);
                if (declaration is not null)
                {
                    declaration.Declaration_Accepted = posted.Declaration.DeclarationAccepted;
                    declaration.Declaration_Text = posted.Declaration.DeclarationText;
                    declaration.Signature_Name = posted.Declaration.SignatureName;
                    if (!string.IsNullOrWhiteSpace(posted.Declaration.SignaturePicture))
                        declaration.Signature_Picture = posted.Declaration.SignaturePicture;
                    declaration.Declaration_Date = now;
                    declaration.UpdatedBy = userId;
                    declaration.UpdatedDate = now;
                }
            }

            var oldStatus = info.Attr_Status;
            info.Attr_Status = "Resubmitted";
            info.RevisionRequired = false;
            info.RevisedBy = userName;
            info.RevisedDateTime = now;
            info.RevisionComment = model.RevisionComment.Trim();
            info.UpdatedBy = userId;
            info.UpdatedDate = now;

            _context.AttrPropertyInfoAuditTrail.Add(new AttrPropertyInfoAuditTrail
            {
                Attr_ID = info.Attr_ID,
                Attr_No = info.Attr_No,
                Action = "Client Resubmitted Corrections",
                OldStatus = oldStatus,
                NewStatus = "Resubmitted",
                ActionByUserId = userId,
                ActionByName = userName,
                ActionRole = "Client",
                Comment = model.RevisionComment.Trim(),
                ActionDateTime = now
            });

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Correction acknowledgement is generated only after the correction data
            // is committed successfully. It is separate from the original submission
            // acknowledgement: no new evidence PIN and no new 48-hour window.
            try
            {
                var correctedSubmission = await BuildSubmittedAttributeViewModelAsync(
                    info.Attr_ID,
                    cancellationToken);

                if (correctedSubmission is null)
                {
                    _logger.LogWarning(
                        "[Attributes] Corrections saved for {AttrNo}, but the corrected submission could not be rebuilt for acknowledgement generation.",
                        info.Attr_No);
                    return;
                }

                var correctedSections = allowedCodes
                    .Select(GetCorrectionSectionDisplayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToList();

                var correctionAck = await _documentService
                    .GenerateCorrectionAcknowledgementPdfAsync(
                        correctedSubmission,
                        info,
                        model.RevisionComment.Trim(),
                        correctedSections);

                _context.AttrPropertyInfoAuditTrail.Add(new AttrPropertyInfoAuditTrail
                {
                    Attr_ID = info.Attr_ID,
                    Attr_No = info.Attr_No,
                    Action = "Correction Acknowledgement Generated",
                    OldStatus = "Resubmitted",
                    NewStatus = "Resubmitted",
                    ActionByUserId = userId,
                    ActionByName = userName,
                    ActionRole = "Client",
                    Comment = $"Correction acknowledgement saved as {correctionAck.FileName}.",
                    ActionDateTime = DateTime.Now
                });
                await _context.SaveChangesAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(info.SubmittedByEmail))
                {
                    try
                    {
                        await _emailService.SendAttributeCorrectionAcknowledgementAsync(
                            info.SubmittedByEmail,
                            info.SubmittedByName ?? userName,
                            info.Attr_No ?? $"ATTR-GV23-{info.Attr_ID}",
                            info.Property_Desc ?? correctedSubmission.PropertyDetails?.PropertyDesc ?? "Property",
                            model.RevisionComment.Trim(),
                            correctedSections,
                            correctionAck.Pdf,
                            correctionAck.FileName,
                            correctionAck.FolderPath);

                        _context.AttrPropertyInfoAuditTrail.Add(new AttrPropertyInfoAuditTrail
                        {
                            Attr_ID = info.Attr_ID,
                            Attr_No = info.Attr_No,
                            Action = "Correction Acknowledgement Email Sent",
                            OldStatus = "Resubmitted",
                            NewStatus = "Resubmitted",
                            ActionByUserId = userId,
                            ActionByName = userName,
                            ActionRole = "Client",
                            Comment = $"Correction acknowledgement emailed to {info.SubmittedByEmail} and an .eml copy was saved in the attribute folder.",
                            ActionDateTime = DateTime.Now
                        });
                        await _context.SaveChangesAsync(cancellationToken);
                    }
                    catch (Exception emailEx)
                    {
                        _logger.LogError(
                            emailEx,
                            "[Attributes] Corrections saved and acknowledgement generated, but correction acknowledgement email failed for {AttrNo}.",
                            info.Attr_No);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[Attributes] Corrections saved and acknowledgement generated for {AttrNo}, but no client email is available.",
                        info.Attr_No);
                }
            }
            catch (Exception ackEx)
            {
                // Do not undo a valid correction resubmission because document/email
                // generation failed after the database transaction was committed.
                _logger.LogError(
                    ackEx,
                    "[Attributes] Corrections were saved for {AttrNo}, but the correction acknowledgement could not be generated.",
                    info.Attr_No);
            }
        }

        private static string GetCorrectionSectionDisplayName(string sectionCode)
        {
            return sectionCode?.Trim().ToUpperInvariant() switch
            {
                "PROPERTY_DETAILS" => "Property Details",
                "VALUATION_DETAILS" => "Valuation Details",
                "CONTACT_INFORMATION" => "Contact Information",
                "ACCESS_INFORMATION" => "Access Information",
                "PRIMARY_ATTRIBUTES" => "Primary Attributes",
                "SECONDARY_ATTRIBUTES" => "Secondary Attributes",
                "BUSINESS_BUILDINGS" => "Business Buildings",
                "BUSINESS_SECTIONS" => "Business Sections",
                "BUSINESS_GENERAL" => "Business General",
                "DRC_BUILDINGS" => "DRC Buildings",
                "DRC_IMPROVEMENTS" => "DRC Improvements",
                "DRC_VACANT_LAND" => "DRC Vacant Land",
                "DRC_MARKET_VALUE" => "DRC Market Value",
                "CALCULATIONS" => "Calculations",
                "DECLARATION" => "Declaration",
                _ => sectionCode?.Replace('_', ' ') ?? "Corrected Section"
            };
        }

        private static void CopyMatchingValues<TSource, TTarget>(TSource source, TTarget target)
        {
            if (source is null || target is null) return;

            var targetProperties = typeof(TTarget).GetProperties()
                .Where(x => x.CanWrite && x.Name is not ("Id" or "PropertyDetailsId" or "CreatedBy" or "CreatedDate" or "UpdatedBy" or "UpdatedDate"))
                .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var sourceProperty in typeof(TSource).GetProperties().Where(x => x.CanRead))
            {
                if (!targetProperties.TryGetValue(sourceProperty.Name, out var targetProperty)) continue;
                if (!targetProperty.PropertyType.IsAssignableFrom(sourceProperty.PropertyType)) continue;
                targetProperty.SetValue(target, sourceProperty.GetValue(source));
            }
        }

        private static void StampUpdated(object row, string userId, DateTime now)
        {
            row.GetType().GetProperty("UpdatedBy")?.SetValue(row, userId);
            row.GetType().GetProperty("UpdatedDate")?.SetValue(row, now);
        }

        private async Task UpdateSingleAsync<TEntity, TModel>(
            DbSet<TEntity> set,
            int propertyDetailsId,
            TModel source,
            string userId,
            DateTime now,
            CancellationToken cancellationToken)
            where TEntity : class, new()
        {
            var row = await set.FirstOrDefaultAsync(
                x => EF.Property<int>(x, "PropertyDetailsId") == propertyDetailsId,
                cancellationToken);

            if (row is null)
            {
                row = new TEntity();
                typeof(TEntity).GetProperty("PropertyDetailsId")?.SetValue(row, propertyDetailsId);
                typeof(TEntity).GetProperty("CreatedBy")?.SetValue(row, userId);
                typeof(TEntity).GetProperty("CreatedDate")?.SetValue(row, now);
                set.Add(row);
            }

            CopyMatchingValues(source, row);
            StampUpdated(row, userId, now);
        }

        private async Task ReplaceRowsAsync<TEntity, TModel>(
            DbSet<TEntity> set,
            int propertyDetailsId,
            IEnumerable<TModel>? sources,
            string userId,
            DateTime now,
            CancellationToken cancellationToken)
            where TEntity : class, new()
        {
            var existing = await set
                .Where(x => EF.Property<int>(x, "PropertyDetailsId") == propertyDetailsId)
                .ToListAsync(cancellationToken);
            set.RemoveRange(existing);

            foreach (var source in sources ?? Enumerable.Empty<TModel>())
            {
                var row = new TEntity();
                typeof(TEntity).GetProperty("PropertyDetailsId")?.SetValue(row, propertyDetailsId);
                typeof(TEntity).GetProperty("CreatedBy")?.SetValue(row, userId);
                typeof(TEntity).GetProperty("CreatedDate")?.SetValue(row, now);
                CopyMatchingValues(source, row);
                StampUpdated(row, userId, now);
                set.Add(row);
            }
        }
        public async Task<(byte[] Pdf, string FileName)?> GenerateAcknowledgementPdfAsync(long attrId)
        {
            var info = await _context.AttrPropertyInfo
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.ValuationDetails)
                .Include(x => x.PropertyDetails)
                    .ThenInclude(x => x!.Calculations)
                .FirstOrDefaultAsync(x =>
                    x.Attr_ID == attrId &&
                    x.IsActive == true);

            if (info == null)
                return null;

            var model = await BuildSubmittedAttributeViewModelAsync(attrId);

            if (model == null)
                return null;

            var declaration = await _context.AttrDeclarations
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Attr_ID == attrId);

            model.GeneratedEvidencePin = declaration?.EvidencePin ?? declaration?.RandomPin;

            model.GeneratedEvidenceDeadline =
                declaration?.AdditionalEvidenceDeadline
                ?? declaration?.PinExpiryDateTime
                ?? info.SubmissionDateTime.AddHours(48);

            model.AttrId = info.Attr_ID;
            model.AttrNo = info.Attr_No;

            return await _documentService.GenerateAcknowledgementPdfAsync(
                model,
                info);
        }

        public async Task<(byte[] Pdf, string FileName)?> GenerateAcknowledgementPdfAsync(string attrNo)
        {
            if (string.IsNullOrWhiteSpace(attrNo))
                return null;

            var cleanAttrNo = attrNo.Trim();

            var info = await _context.AttrPropertyInfo
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.Attr_No == cleanAttrNo &&
                    x.IsActive == true);

            if (info == null)
                return null;

            return await GenerateAcknowledgementPdfAsync(info.Attr_ID);
        }
        public async Task<List<AttributeSectorInboxItemVm>> GetSectorInboxAsync(string sector)
        {
            if (string.IsNullOrWhiteSpace(sector))
                return new List<AttributeSectorInboxItemVm>();

            sector = sector.Trim();

            return await _context.AttrPropertyInfo
                .Include(x => x.PropertyDetails)
                .Where(x =>
                    x.IsActive == true &&
                    x.Attr_Status == "SectorInbox" &&
                    x.RoutedSector == sector)
                .OrderBy(x => x.RoutedToSectorDateTime)
                .Select(x => new AttributeSectorInboxItemVm
                {
                    AttrId = x.Attr_ID,
                    AttrNo = x.Attr_No,
                    PropertyDescription = x.Property_Desc,
                    Township = x.PropertyDetails != null ? x.PropertyDetails.Township : null,
                    RoutedSector = x.RoutedSector,
                    SubmittedDate = x.SubmissionDateTime,
                    RoutedDate = x.RoutedToSectorDateTime,
                    EvidenceCount = x.Evidence_Count
                })
                .ToListAsync();
        }
        private static void ValidateAndCleanSubmission(AttributeSubmissionViewModel model)
        {
            if (model == null)
                throw new InvalidOperationException("Attribute submission data could not be found.");

            CleanSubmission(model);

            var errors = new List<string>();

            ValidateBaseSubmission(model, errors);
            ValidateContacts(model, errors);
            ValidateDeclaration(model, errors);
            ValidateFormSpecificData(model, errors);

            if (errors.Any())
            {
                throw new InvalidOperationException(
                    "Please correct the following before submitting:\n- " +
                    string.Join("\n- ", errors.Distinct()));
            }
        }

        private static void CleanSubmission(AttributeSubmissionViewModel model)
        {
            model.FormType = NormalizeFormType(model.FormType);
            model.ClientComment = CleanText(model.ClientComment, 1000);

            model.PropertyDetails ??= new AttributePropertyDetailsVm();
            model.ValuationDetails ??= new AttributeValuationDetailsVm();
            model.PrimaryAttributes ??= new AttributePrimaryAttributesVm();
            model.SecondaryAttributes ??= new AttributeSecondaryAttributesVm();
            model.Calculations ??= new AttributeCalculationsVm();
            model.Access ??= new AttributeAccessVm();
            model.Declaration ??= new AttributeDeclarationVm();

            model.BusinessGeneral ??= new AttributeBusinessGeneralVm();
            model.DrcMarketValueDemolition ??= new AttributeDrcMarketValueDemolitionVm();

            model.ContactInfos ??= new List<AttributeContactInfoVm>();
            model.BusinessBuildings ??= new List<AttributeBusinessBuildingVm>();
            model.BusinessSections ??= new List<AttributeBusinessSectionVm>();
            model.DrcBuildings ??= new List<AttributeDrcBuildingVm>();
            model.DrcImprovements ??= new List<AttributeDrcImprovementVm>();
            model.DrcVacantLands ??= new List<AttributeDrcVacantLandVm>();

            CleanPropertyDetails(model.PropertyDetails);
            CleanValuationDetails(model.ValuationDetails);
            CleanContactDetails(model.ContactInfos);
            CleanResidentialDetails(model.PrimaryAttributes, model.SecondaryAttributes);
            CleanBusinessDetails(model);
            CleanDrcDetails(model);
            CleanDeclaration(model.Declaration);

            if (!model.ValuationDetails.IsMixedUse)
                model.ValuationDetails.AlternateUsages = null;
        }

        private static void ValidateBaseSubmission(
            AttributeSubmissionViewModel model,
            List<string> errors)
        {
            var allowedFormTypes = new[]
            {
        "Residential",
        "ResidentialST",
        "BusinessCommercial",
        "DRCMethod"
    };

            if (!allowedFormTypes.Contains(model.FormType))
                errors.Add("Please select a valid attribute form type.");

            if (model.PropertyDetails == null)
            {
                errors.Add("Property details could not be found.");
                return;
            }

            if (IsBlank(model.PropertyDetails.PropertyDesc))
                errors.Add("Property description is required.");

            if (IsBlank(model.PropertyDetails.UnitKey) &&
                IsBlank(model.PropertyDetails.PropertyId) &&
                IsBlank(model.PropertyDetails.PremiseId) &&
                IsBlank(model.PropertyDetails.ValuationKey))
            {
                errors.Add("Property reference could not be verified. Please go back and select the property again.");
            }

            if (IsBlank(model.PropertyDetails.Township))
                errors.Add("Township is required for sector routing.");

            if (IsBlank(model.PropertyDetails.Municipality))
                model.PropertyDetails.Municipality = "City of Johannesburg";
        }

        private static void ValidateContacts(
            AttributeSubmissionViewModel model,
            List<string> errors)
        {
            var contacts = model.ContactInfos?
                .Where(x => !IsEmptyContact(x))
                .ToList() ?? new List<AttributeContactInfoVm>();

            if (!contacts.Any())
            {
                errors.Add("At least one contact person is required.");
                return;
            }

            for (var i = 0; i < contacts.Count; i++)
            {
                var contact = contacts[i];
                var label = $"Contact {i + 1}";

                // Owner/company identity is intentionally hidden on the online
                // Attributes Create form. Preserve any backend values that were
                // loaded from LIS/SAP, but never make a hidden identity field a
                // client-side submission requirement.
                if (contact.IsCompany)
                {
                    contact.ContactType = "Company";
                }
                else if (IsBlank(contact.ContactType) ||
                         contact.ContactType.Equals("Representative", StringComparison.OrdinalIgnoreCase))
                {
                    contact.ContactType = "Owner";
                }

                if (IsBlank(contact.Email))
                {
                    errors.Add($"{label}: Email address is required.");
                }
                else if (!IsValidEmail(contact.Email))
                {
                    errors.Add($"{label}: Email address is invalid.");
                }

                if (IsBlank(contact.CellNo))
                {
                    errors.Add($"{label}: Cell number is required.");
                }
                else if (!IsValidPhone(contact.CellNo))
                {
                    errors.Add($"{label}: Cell number is invalid. " + V2_Genesis.Helpers.PhoneNumberRules.Hint);
                }

                if (!IsBlank(contact.HomePhoneNo) && !IsValidPhone(contact.HomePhoneNo))
                    errors.Add($"{label}: Home phone number is invalid.");

                if (!IsBlank(contact.WorkPhoneNo) && !IsValidPhone(contact.WorkPhoneNo))
                    errors.Add($"{label}: Work phone number is invalid.");
            }

            model.ContactInfos = contacts;
        }

        private static void ValidateDeclaration(
            AttributeSubmissionViewModel model,
            List<string> errors)
        {
            if (model.Declaration == null)
            {
                errors.Add("Declaration is required.");
                return;
            }

            if (!model.Declaration.DeclarationAccepted)
                errors.Add("You must accept the declaration before submitting.");

            if (IsBlank(model.Declaration.SignatureName))
                errors.Add("Signature name is required.");

            if (IsBlank(model.Declaration.DeclarationText))
            {
                model.Declaration.DeclarationText =
                    "I declare that the information submitted is true and correct to the best of my knowledge.";
            }

            if (model.RepresentativeDetails?.IsRepresentative == true)
            {
                if (IsBlank(model.RepresentativeDetails.Representative_Name))
                    errors.Add("Representative name is required.");

                if (IsBlank(model.RepresentativeDetails.Rep_Email))
                {
                    errors.Add("Representative email is required.");
                }
                else if (!IsValidEmail(model.RepresentativeDetails.Rep_Email))
                {
                    errors.Add("Representative email is invalid.");
                }

                if (IsBlank(model.RepresentativeDetails.Rep_Cell_Phone))
                {
                    errors.Add("Representative cell number is required.");
                }
                else if (!IsValidPhone(model.RepresentativeDetails.Rep_Cell_Phone))
                {
                    errors.Add("Representative cell number is invalid. " + V2_Genesis.Helpers.PhoneNumberRules.Hint);
                }
            }
        }

        private static void ValidateFormSpecificData(
            AttributeSubmissionViewModel model,
            List<string> errors)
        {
            switch (model.FormType)
            {
                case "Residential":
                    ValidateResidential(model, errors);
                    break;

                case "ResidentialST":
                    ValidateResidentialST(model, errors);
                    break;

                case "BusinessCommercial":
                    ValidateBusinessCommercial(model, errors);
                    break;

                case "DRCMethod":
                    ValidateDrcMethod(model, errors);
                    break;
            }
        }

        private static void ValidateResidential(
            AttributeSubmissionViewModel model,
            List<string> errors)
        {
            if (model.SecondaryAttributes == null)
            {
                errors.Add("Residential secondary attributes are required.");
                return;
            }

            if (IsBlank(model.SecondaryAttributes.Quality))
                errors.Add("Residential quality is required.");

            if (IsBlank(model.SecondaryAttributes.Condition))
                errors.Add("Residential condition is required.");

            var hasAnyArea =
                model.PrimaryAttributes?.Tla1 > 0 ||
                model.PrimaryAttributes?.Tla2 > 0 ||
                model.PrimaryAttributes?.Tla3 > 0 ||
                model.PrimaryAttributes?.Garage > 0 ||
                model.PrimaryAttributes?.CarportCp > 0 ||
                model.PrimaryAttributes?.GrannyFlatGf > 0 ||
                model.PrimaryAttributes?.StaffQuartersSq > 0 ||
                model.PrimaryAttributes?.Storage > 0;

            if (!hasAnyArea)
                errors.Add("At least one residential area or attribute value must be captured.");
        }

        private static void ValidateResidentialST(
            AttributeSubmissionViewModel model,
            List<string> errors)
        {
            if (model.PrimaryAttributes?.STMain == null || model.PrimaryAttributes.STMain <= 0)
                errors.Add("Sectional Title main area is required.");

            if (IsBlank(model.SecondaryAttributes?.STCondition.ToString()))
                errors.Add("Sectional Title condition is required.");

            if (model.SecondaryAttributes?.STFloor == null)
                errors.Add("Sectional Title floor is required.");

            if (IsBlank(model.SecondaryAttributes?.Quality))
                errors.Add("Sectional Title quality is required.");
        }

        private static void ValidateBusinessCommercial(
            AttributeSubmissionViewModel model,
            List<string> errors)
        {
            var buildings = model.BusinessBuildings?
                .Where(x => !IsEmptyBusinessBuilding(x))
                .ToList() ?? new List<AttributeBusinessBuildingVm>();

            var sections = model.BusinessSections?
                .Where(x => !IsEmptyBusinessSection(x))
                .ToList() ?? new List<AttributeBusinessSectionVm>();

            if (!buildings.Any() && !sections.Any())
            {
                errors.Add("Business and Commercial form requires at least one building or business section.");
                return;
            }

            for (var i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                var label = $"Business building {i + 1}";

                if (IsBlank(b.BuildingNr))
                    errors.Add($"{label}: Building number is required.");

                if (IsBlank(b.Quality))
                    errors.Add($"{label}: Quality is required.");

                if (IsBlank(b.Condition))
                    errors.Add($"{label}: Condition is required.");

                if (b.GBA == null || b.GBA <= 0)
                    errors.Add($"{label}: GBA must be greater than zero.");
            }

            for (var i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                var label = $"Business section {i + 1}";

                if (IsBlank(s.BuildingNr))
                    errors.Add($"{label}: Building number is required.");

                if (IsBlank(s.Usage))
                    errors.Add($"{label}: Usage is required.");

                if (s.GBA == null || s.GBA <= 0)
                    errors.Add($"{label}: GBA must be greater than zero.");
            }

            model.BusinessBuildings = buildings;
            model.BusinessSections = sections;
        }

        private static void ValidateDrcMethod(
            AttributeSubmissionViewModel model,
            List<string> errors)
        {
            var buildings = model.DrcBuildings?
                .Where(x => !IsEmptyDrcBuilding(x))
                .ToList() ?? new List<AttributeDrcBuildingVm>();

            var improvements = model.DrcImprovements?
                .Where(x => !IsEmptyDrcImprovement(x))
                .ToList() ?? new List<AttributeDrcImprovementVm>();

            var vacantLand = model.DrcVacantLands?
                .Where(x => !IsEmptyDrcVacantLand(x))
                .ToList() ?? new List<AttributeDrcVacantLandVm>();

            if (!buildings.Any() && !improvements.Any() && !vacantLand.Any())
            {
                errors.Add("DRC form requires at least one building, improvement, or vacant land item.");
                return;
            }

            for (var i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                var label = $"DRC building {i + 1}";

                if (IsBlank(b.BuildingDescription))
                    errors.Add($"{label}: Building description is required.");

                if (IsBlank(b.Quality))
                    errors.Add($"{label}: Quality is required.");

                if (IsBlank(b.Condition))
                    errors.Add($"{label}: Condition is required.");

                if (b.GrossBuildingArea == null || b.GrossBuildingArea <= 0)
                    errors.Add($"{label}: Gross building area must be greater than zero.");
            }

            for (var i = 0; i < improvements.Count; i++)
            {
                var imp = improvements[i];
                var label = $"DRC improvement {i + 1}";

                if (IsBlank(imp.ImprovementDescription))
                    errors.Add($"{label}: Improvement description is required.");

                if (IsBlank(imp.Quality))
                    errors.Add($"{label}: Quality is required.");

                if (IsBlank(imp.Condition))
                    errors.Add($"{label}: Condition is required.");

                if (imp.AreaUnit == null || imp.AreaUnit <= 0)
                    errors.Add($"{label}: Area must be greater than zero.");
            }

            for (var i = 0; i < vacantLand.Count; i++)
            {
                var land = vacantLand[i];
                var label = $"DRC vacant land {i + 1}";

                if (IsBlank(land.Region))
                    errors.Add($"{label}: Region is required.");

                if (land.Area == null || land.Area <= 0)
                    errors.Add($"{label}: Area must be greater than zero.");
            }

            model.DrcBuildings = buildings;
            model.DrcImprovements = improvements;
            model.DrcVacantLands = vacantLand;
        }
        private static void CleanPropertyDetails(AttributePropertyDetailsVm p)
        {
            p.HArea = CleanText(p.HArea, 100);
            p.DataController = CleanText(p.DataController, 100);
            p.CollectionBlock = CleanText(p.CollectionBlock, 100);
            p.DataCollector = CleanText(p.DataCollector, 100);
            p.SGNumber = CleanText(p.SGNumber, 100);
            p.Centroid = CleanText(p.Centroid, 100);
            p.Erf = CleanText(p.Erf, 100);
            p.Extent = CleanText(p.Extent, 100);
            p.SectionalTitle = CleanText(p.SectionalTitle, 100);
            p.LandUseFinancials = CleanText(p.LandUseFinancials, 255);
            p.Municipality = CleanText(p.Municipality, 255) ?? "City of Johannesburg";
            p.Ward = CleanText(p.Ward, 100);
            p.Township = CleanText(p.Township, 255);
            p.Zoning = CleanText(p.Zoning, 255);
            p.Sources = CleanText(p.Sources, 255);
            p.Address = CleanText(p.Address, 500);
            p.PropertyDesc = CleanText(p.PropertyDesc, 500);
            p.PremiseId = CleanText(p.PremiseId, 100);
            p.UnitKey = CleanText(p.UnitKey, 100);
            p.PropertyId = CleanText(p.PropertyId, 100);
            p.ValuationKey = CleanText(p.ValuationKey, 100);
            p.Sector = CleanText(p.Sector, 100);
            p.RollType = CleanText(p.RollType, 100);
            p.RollDescription = CleanText(p.RollDescription, 255);
        }

        private static void CleanValuationDetails(AttributeValuationDetailsVm v)
        {
            v.ValuationCategoryOnRoll = CleanText(v.ValuationCategoryOnRoll, 255);
            v.ActualUse = CleanText(v.ActualUse, 255);
            v.AlternateUsages = CleanText(v.AlternateUsages, 500);
            v.OwnersTitleDeeds = CleanText(v.OwnersTitleDeeds, 500);
            v.OwnersFinancials = CleanText(v.OwnersFinancials, 500);
        }

        private static void CleanContactDetails(List<AttributeContactInfoVm> contacts)
        {
            foreach (var c in contacts)
            {
                c.ContactType = CleanText(c.ContactType, 50);
                c.CompanyName = CleanText(c.CompanyName, 255);
                c.CompanyRegistrationNumber = CleanText(c.CompanyRegistrationNumber, 100);
                c.FirstNames = CleanText(c.FirstNames, 255);
                c.LastName = CleanText(c.LastName, 255);
                c.MaidenName = CleanText(c.MaidenName, 255);
                c.IDNumber = CleanDigits(c.IDNumber, 20);
                c.Gender = CleanText(c.Gender, 50);
                c.MaritalStatus = CleanText(c.MaritalStatus, 50);
                c.Citizenship = CleanText(c.Citizenship, 100);
                c.PhysicalAddress = CleanText(c.PhysicalAddress, 500);
                c.PostalAddress = CleanText(c.PostalAddress, 500);
                c.Email = CleanEmail(c.Email);
                c.HomePhoneNo = CleanPhone(c.HomePhoneNo);
                c.WorkPhoneNo = CleanPhone(c.WorkPhoneNo);
                c.CellNo = CleanPhone(c.CellNo);
                c.FaxNo = CleanPhone(c.FaxNo);
                c.Comments = CleanText(c.Comments, 1000);
            }
        }

        private static void CleanResidentialDetails(
            AttributePrimaryAttributesVm p,
            AttributeSecondaryAttributesVm s)
        {
            s.Security = CleanText(s.Security, 100);
            s.Noise = CleanText(s.Noise, 100);
            s.Topography = CleanText(s.Topography, 100);
            s.Quality = CleanText(s.Quality, 100);
            s.Condition = CleanText(s.Condition, 100);
            s.STCondition = int.TryParse(
    CleanText(s.STCondition?.ToString(), 100),
    out var value)
        ? value
        : null;
        }

        private static void CleanBusinessDetails(AttributeSubmissionViewModel model)
        {
            foreach (var b in model.BusinessBuildings)
            {
                b.BuildingNr = CleanText(b.BuildingNr, 50);
                b.Quality = CleanText(b.Quality, 100);
                b.Condition = CleanText(b.Condition, 100);
            }

            foreach (var s in model.BusinessSections)
            {
                s.BuildingNr = CleanText(s.BuildingNr, 50);
                s.Usage = CleanText(s.Usage, 255);
                s.MarketGroup = CleanText(s.MarketGroup, 255);
                s.Quality = CleanText(s.Quality, 100);
            }
        }

        private static void CleanDrcDetails(AttributeSubmissionViewModel model)
        {
            foreach (var b in model.DrcBuildings)
            {
                b.BuildingDescription = CleanText(b.BuildingDescription, 255);
                b.Quality = CleanText(b.Quality, 100);
                b.Condition = CleanText(b.Condition, 100);
            }

            foreach (var i in model.DrcImprovements)
            {
                i.ImprovementDescription = CleanText(i.ImprovementDescription, 255);
                i.Quality = CleanText(i.Quality, 100);
                i.Condition = CleanText(i.Condition, 100);
            }

            foreach (var v in model.DrcVacantLands)
            {
                v.Region = CleanText(v.Region, 255);
            }
        }

        private static void CleanDeclaration(AttributeDeclarationVm d)
        {
            d.SignatureName = CleanText(d.SignatureName, 255);
            d.SignaturePicture = string.IsNullOrWhiteSpace(d.SignaturePicture)
                ? null
                : d.SignaturePicture.Trim();
            d.DeclarationText = CleanText(d.DeclarationText, 2000);
        }
        private static bool IsBlank(string? value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        private static string? CleanText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var cleaned = Regex.Replace(value.Trim(), @"\s+", " ");

            return cleaned.Length <= maxLength
                ? cleaned
                : cleaned[..maxLength];
        }

        private static string? CleanEmail(string? value)
        {
            var cleaned = CleanText(value, 255);

            return cleaned?.ToLowerInvariant();
        }

        private static string? CleanPhone(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var cleaned = Regex.Replace(value.Trim(), @"[^\d+]", "");

            return cleaned.Length <= 20
                ? cleaned
                : cleaned[..20];
        }

        private static string? CleanDigits(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var cleaned = Regex.Replace(value.Trim(), @"\D", "");

            return cleaned.Length <= maxLength
                ? cleaned
                : cleaned[..maxLength];
        }

        private static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                var address = new MailAddress(email);
                return string.Equals(address.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // 0XXXXXXXXX or +27XXXXXXXXX (see Helpers/PhoneNumberRules).
        private static bool IsValidPhone(string? phone) =>
            V2_Genesis.Helpers.PhoneNumberRules.IsValid(phone);
    }
}