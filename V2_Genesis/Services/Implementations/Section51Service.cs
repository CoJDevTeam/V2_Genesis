using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using V2_Genesis.Data;
using System.Text.RegularExpressions;
using V2_Genesis.Models;
using V2_Genesis.Models.Emails;
using V2_Genesis.Models.Section51;
using V2_Genesis.Services.Interfaces;
using V2_Genesis.Services.Section51;

namespace V2_Genesis.Services.Implementations;

public class Section51Service : ISection51Service
{
    private readonly IConfiguration _config;
    private readonly ILogger<Section51Service> _logger;
    private readonly IReadOnlyDictionary<string, Section51RollConfig> _registry;
    private readonly IWebHostEnvironment _environment;
    private readonly IEmailService _emailService;
    private readonly IPropertySearchService _propertySearch;

    private const int MAX_FILES = 10;
    private const int MAX_FILE_MB = 3;

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png"
        };

    public Section51Service(
        IConfiguration config,
        ILogger<Section51Service> logger,
        IWebHostEnvironment environment,
        IEmailService emailService,
        IPropertySearchService propertySearch)
    {
        _propertySearch = propertySearch;
        _config = config;
        _logger = logger;
        _environment = environment;
        _emailService = emailService;
        _registry = Section51RollRegistry.Build(config);
    }

    // ── Validate PIN + limit check ────────────────────────────────────
    public async Task<Section51ValidateResult> ValidateAsync(
        string rollSource, string objectionNo, string pin)
    {
        if (!_registry.TryGetValue(rollSource, out var cfg))
            return Section51ValidateResult.Fail("Invalid roll source.");

        try
        {
            var connStr = _config.GetConnectionString(cfg.ConnectionKey)!;
            await using var conn = new SqlConnection(connStr);

            // Step 1 — validate objection + PIN
            var rows = await conn.QueryAsync(
                cfg.ValidateSp,
                new { Objection_No = objectionNo.Trim(), Pin = pin.Trim() },
                commandType: CommandType.StoredProcedure);

            if (!rows.Any())
                return Section51ValidateResult.Fail(
                    "Invalid objection number or PIN. Please check and try again.");

            // Step 2 — check if evidence was already submitted.
            //
            // Do not use checkRows.Any() here. Some legacy Section51Check
            // procedures return a status row even when the answer is "No",
            // which caused Genesis to treat every reference as already done.
            var alreadyDone = await conn.ExecuteScalarAsync<int?>(
                @"SELECT TOP (1) 1
                  FROM dbo.Obj_Section_51_Uploads
                  WHERE LTRIM(RTRIM(Objection_Ref_51)) = @Objection_No;",
                new { Objection_No = objectionNo.Trim() }) == 1;

            // UAT needs to exercise the full Section 51 workflow even when the
            // statutory production deadline has passed. The bypass is explicit
            // and is never honoured in Production.
            var bypassDeadline =
                _config.GetValue<bool>("Section51:BypassDeadline")
                && !_environment.IsProduction();

            // Each objection has its own 30-day window (Section51Table.ClosingDate,
            // written when the Section 51 notice is sent). Older batch notices
            // without a row fall back to the roll deadline in appsettings.
            // Never let the audit-table lookup break validation: if the table
            // or a column differs on this roll, log it and use the roll deadline.
            var closingDate = await GetClosingDateAsync(conn, objectionNo.Trim(), rollSource);

            var pastDeadline =
                !bypassDeadline &&
                (closingDate.HasValue
                    ? DateTime.Now > EndOfDay(closingDate.Value)
                    : DateTime.UtcNow > cfg.DeadlineUtc);

            if (bypassDeadline)
            {
                _logger.LogWarning(
                    "[Section51] Deadline bypass is enabled in {Environment} for {ObjNo} on {Roll}.",
                    _environment.EnvironmentName,
                    objectionNo,
                    rollSource);
            }

            if (alreadyDone || pastDeadline)
                return Section51ValidateResult.Limit(alreadyDone, pastDeadline);

            return Section51ValidateResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Section51] Validate failed for {ObjNo} on {Roll} (ConnectionKey {ConnectionKey}, ValidateSp {ValidateSp}): {Message}",
                objectionNo, rollSource, cfg.ConnectionKey, cfg.ValidateSp, ex.Message);
            return Section51ValidateResult.Fail(
                "A system error occurred. Please try again.");
        }
    }

    private async Task<DateTime?> GetClosingDateAsync(
        SqlConnection conn, string objectionNo, string rollSource)
    {
        try
        {
            // Dynamic SQL so a missing table/column is a runtime error we can
            // catch, not a compile error for the whole batch.
            return await conn.ExecuteScalarAsync<DateTime?>(
                @"IF OBJECT_ID('dbo.Section51Table') IS NULL
                      SELECT CAST(NULL AS datetime);
                  ELSE
                      EXEC sp_executesql
                          N'SELECT MAX(TRY_CONVERT(datetime, ClosingDate))
                              FROM dbo.Section51Table
                             WHERE LTRIM(RTRIM(ObjectionNo)) = @Objection_No',
                          N'@Objection_No nvarchar(100)',
                          @Objection_No;",
                new { Objection_No = objectionNo });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Section51] Could not read ClosingDate from Section51Table for {ObjNo} on {Roll}; using the roll deadline.",
                objectionNo, rollSource);
            return null;
        }
    }

    // ── Upload files ──────────────────────────────────────────────────
    public async Task<(bool Success, string? Error, int FileCount, List<string> FileNames)>
        UploadAsync(string rollSource, string objectionNo, List<IFormFile> files)
    {
        if (!_registry.TryGetValue(rollSource, out var cfg))
            return (false, "Invalid roll source.", 0, new());

        if (files.Count > MAX_FILES)
            return (false,
                $"Maximum {MAX_FILES} files allowed.", 0, new());

        if (files.Any(f => f is null || f.Length == 0))
            return (false, "One or more selected files are empty.", 0, new());

        // Validate every file before creating anything on disk.
        foreach (var file in files)
        {
            if (file.Length > MAX_FILE_MB * 1024 * 1024)
                return (false,
                    $"File '{file.FileName}' exceeds {MAX_FILE_MB} MB limit.",
                    0, new());

            var extension = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(extension))
                return (false,
                    $"File '{file.FileName}' is not an allowed file type. Allowed: PDF, JPG, JPEG, PNG.",
                    0, new());
        }

        // Owner evidence lives in the objection folder, next to the
        // acknowledgements and the Section 51 Notice:
        //   ObjectionRolls:{roll}:FileRootPath\{ObjNo}\Section 51 Owner Evidence
        // (falls back to Section51Rolls:{roll}:FileRootPath if not configured).
        var objectionRoot = _config[$"ObjectionRolls:{rollSource}:FileRootPath"];
        if (string.IsNullOrWhiteSpace(objectionRoot))
            objectionRoot = cfg.FileRootPath;

        var baseFolder = Path.Combine(objectionRoot, objectionNo.Trim());
        var evidenceFolder = Path.Combine(baseFolder, V2_Genesis.Services.Objection.PackFolders.Section51OwnerEvidence(_config));
        Directory.CreateDirectory(evidenceFolder);

        var savedNames = new List<string>();

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file.FileName);
            var path = Path.Combine(evidenceFolder, fileName);

            if (System.IO.File.Exists(path))
                return (false,
                    $"A file named '{fileName}' has already been uploaded for this objection.",
                    savedNames.Count, savedNames);

            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            await file.CopyToAsync(stream);
            savedNames.Add(fileName);
        }

        // Save record to DB
        try
        {
            var connStr = _config.GetConnectionString(cfg.ConnectionKey)
                ?? throw new InvalidOperationException(
                    $"Connection string '{cfg.ConnectionKey}' was not found.");

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(connStr)
                .Options;

            await using var db = new ApplicationDbContext(options);

            var upload = new Obj_Section_51_Uploads
            {
                Objection_Ref_51 = objectionNo.Trim(),
                Files1 = savedNames.ElementAtOrDefault(0),
                Files2 = savedNames.ElementAtOrDefault(1),
                Files3 = savedNames.ElementAtOrDefault(2),
                Files4 = savedNames.ElementAtOrDefault(3),
                Files5 = savedNames.ElementAtOrDefault(4),
                Files6 = savedNames.ElementAtOrDefault(5),
                Files7 = savedNames.ElementAtOrDefault(6),
                Files8 = savedNames.ElementAtOrDefault(7),
                Files9 = savedNames.ElementAtOrDefault(8),
                Files10 = savedNames.ElementAtOrDefault(9),
                Evidence_count = savedNames.Count
            };

            await db.Obj_Section_51_Uploads.AddAsync(upload);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Section51] DB insert failed for {ObjNo}", objectionNo);

            // Keep the database and physical evidence in sync.
            foreach (var savedName in savedNames)
            {
                try
                {
                    var savedPath = Path.Combine(evidenceFolder, savedName);
                    if (System.IO.File.Exists(savedPath))
                        System.IO.File.Delete(savedPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(
                        cleanupEx,
                        "[Section51] Could not clean up {FileName} after DB failure for {ObjNo}.",
                        savedName,
                        objectionNo);
                }
            }

            return (false,
                "The Section 51 submission could not be recorded. Please try again.",
                0, new());
        }

        return (true, null, savedNames.Count, savedNames);
    }

    private static DateTime EndOfDay(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero
            ? value.Date.AddDays(1).AddTicks(-1)
            : value;

    // ════════════════════════════════════════════════════════════════
    //  SECTION 51 NOTICE — sent when a Third-Party objection is submitted
    //
    //  1. Owner ADDR1–ADDR5 + EMAIL_ADDR from the roll's postal table
    //     (Section51Rolls:{roll}:PostalAddressTable, by PREMISE_ID).
    //  2. Notice PDF → {objection folder}\Section 51 Notice\
    //        {Objection_No}_{Property_Desc}_Section 51.pdf
    //  3. Owner has an email → email with the PDF to the owner, CC Valuation
    //     Enquiries, BCC the roll's tracking mailbox (TrackingEmail),
    //     .eml copy saved next to the PDF:
    //        email_{Objection_No}_{Property_Desc}_{Owner_Email}.eml
    //     No email → the email goes to the tracking mailbox only, and the
    //     PDF is printed and posted to the owner.
    //  4. Row in dbo.Section51Table (BatchName GENESIS, ClosingDate = +30 days)
    //  5. Obj_Property_Info.Section51_Emailed = 'Y' / 'N'
    //
    //  The Third Party is not notified. Nothing here fails the objection.
    // ════════════════════════════════════════════════════════════════
    public async Task<Section51NoticeResult> SendThirdPartyNoticeAsync(Section51NoticeRequest request)
    {
        var result = new Section51NoticeResult();

        if (request is null || string.IsNullOrWhiteSpace(request.ObjectionNo))
        {
            result.Error = "The objection number is missing.";
            return result;
        }

        var objectionNo = request.ObjectionNo.Trim();

        if (!_registry.TryGetValue(request.RollSource, out var cfg))
        {
            result.Error = $"Roll '{request.RollSource}' is not configured in Section51Rolls.";
            _logger.LogError("[S51 Notice] {Error} Objection={ObjectionNo}", result.Error, objectionNo);
            return result;
        }

        var connStr = _config.GetConnectionString(cfg.ConnectionKey);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            result.Error = $"Connection string '{cfg.ConnectionKey}' was not found.";
            _logger.LogError("[S51 Notice] {Error} Objection={ObjectionNo}", result.Error, objectionNo);
            return result;
        }

        var settings = _config.GetSection("Section51:Notice");
        var daysOpen = settings.GetValue<int?>("DaysOpen") ?? 30;
        var batchName = settings["BatchName"] is { Length: > 0 } b ? b.Trim() : "GENESIS";
        // External address for the owner (Portal:ExternalUrl, then the old
        // Section51:Notice:PortalUrl); internal address for City staff.
        var portalUrl = _config["Portal:ExternalUrl"] is { Length: > 0 } ext ? ext.Trim().TrimEnd('/')
            : settings["PortalUrl"] is { Length: > 0 } u ? u.Trim()
            : V2_Genesis.Services.PortalLinks.DefaultExternal;
        var staffPortalUrl = _config["Portal:InternalUrl"] is { Length: > 0 } inn ? inn.Trim().TrimEnd('/')
            : V2_Genesis.Services.PortalLinks.DefaultInternal;
        var ccAddress = settings["CcAddress"];
        var testMode = settings.GetValue<bool?>("TestMode") ?? true;
        var testRecipient = settings["TestRecipient"];

        var letterDate = request.SubmittedAt;
        var closingDate = request.SubmittedAt.Date.AddDays(daysOpen);
        var propertyDescription = string.IsNullOrWhiteSpace(request.PropertyDescription)
            ? "Property"
            : request.PropertyDescription.Trim();

        await using var conn = new SqlConnection(connStr);

        // ── 1. Owner address + email ─────────────────────────────────
        OwnerPostalRow? owner = null;
        try
        {
            owner = await LoadOwnerPostalAsync(conn, cfg.PostalAddressTable, request.PremiseId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[S51 Notice] Could not read owner details from {Table} for {ObjectionNo} (PREMISE_ID {PremiseId}).",
                cfg.PostalAddressTable, objectionNo, request.PremiseId);
        }

        var ownerEmail = FirstValidEmail(owner?.Email);
        result.OwnerEmail = ownerEmail;

        // ── 1b. With Effective Date from the roll ───────────────────
        var wefDate = await GetRollWefDateAsync(request);

        // ── 2. Notice PDF ────────────────────────────────────────────
        var s6 = request.Section6;
        // Where the notice is saved (appsettings → Section51Rolls:{roll}):
        //   NoticePdfPath  register of all Section 51 PDFs of the roll
        //   EmailCopyPath  register of all Section 51 e-mails (.eml) of the roll
        //   Objection Pack ObjectionRolls:{roll}:FileRootPath\{ObjNo}\Section 51 Notice
        //                  (PDF + .eml — the dashboard "Section 51" notice reads
        //                  this folder).
        var packFolder = string.IsNullOrWhiteSpace(request.ObjectionFolder)
            ? string.Empty
            : Path.Combine(request.ObjectionFolder, V2_Genesis.Services.Objection.PackFolders.Section51Notice(_config));
        var noticeFolder = FirstFolder(cfg.NoticePdfPath, cfg.FileRootPath, packFolder);
        var emailFolder = FirstFolder(cfg.EmailCopyPath, noticeFolder);
        var pdfFileName = $"{SafeFileName(objectionNo)}_{SafeFileName(propertyDescription)}_Section 51.pdf";
        byte[] pdf;

        try
        {
            pdf = Section51PdfBuilder.BuildNotice(
                new Section51NoticeData
                {
                    RollName = cfg.RollName,
                    ObjectionNo = objectionNo,
                    Section51Pin = request.Section51Pin,
                    PropertyFrom = request.PropertyFrom,
                    ValuationKey = request.ValuationKey,
                    Addr1 = owner?.Addr1,
                    Addr2 = owner?.Addr2,
                    Addr3 = owner?.Addr3,
                    Addr4 = owner?.Addr4,
                    Addr5 = owner?.Addr5,
                    PropertyDesc = propertyDescription,
                    IsMulti = request.IsMulti,
                    Section6 = new Section6Row
                    {
                        Old_Category = s6?.Old_Category,
                        Old_Extent = s6?.Old_Extent,
                        Old_Market_Value = s6?.Old_Market_Value,
                        New_Category = s6?.New_Category,
                        New_Extent = s6?.New_Extent,
                        New_Market_Value = s6?.New_Market_Value,
                        Old2_Category = s6?.Old2_Category,
                        Old2_Extent = s6?.Old2_Extent,
                        Old2_Market_Value = s6?.Old2_Market_Value,
                        New2_Category = s6?.New2_Category,
                        New2_Extent = s6?.New2_Extent,
                        New2_Market_Value = s6?.New2_Market_Value,
                        Old3_Category = s6?.Old3_Category,
                        Old3_Extent = s6?.Old3_Extent,
                        Old3_Market_Value = s6?.Old3_Market_Value,
                        New3_Category = s6?.New3_Category,
                        New3_Extent = s6?.New3_Extent,
                        New3_Market_Value = s6?.New3_Market_Value,
                        // "With Effective Date" = WefDate of the property on the
                        // roll (e.g. [Objection_Supp4].[dbo].[Sup4].WefDate).
                        // Empty when the roll has no WefDate for this property.
                        WithEffectDate = wefDate
                    }
                },
                new Section51NoticeContext
                {
                    HeaderImagePath = Path.Combine(_environment.WebRootPath, "Images", "Obj_Header.PNG"),
                    LetterDate = letterDate,
                    SubmissionsCloseDate = closingDate,
                    PortalUrl = portalUrl,
                    EnquiriesLine = settings["EnquiriesLine"],
                    SignOffName = settings["SignOffName"],
                    SignOffTitle = settings["SignOffTitle"]
                });

            Directory.CreateDirectory(noticeFolder);
            result.PdfPath = Path.Combine(noticeFolder, pdfFileName);
            await System.IO.File.WriteAllBytesAsync(result.PdfPath, pdf);

            // Same PDF in the Objection Pack. A failure here is logged only.
            if (!string.IsNullOrWhiteSpace(packFolder) &&
                !packFolder.Equals(noticeFolder, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(packFolder);
                    await System.IO.File.WriteAllBytesAsync(Path.Combine(packFolder, pdfFileName), pdf);
                }
                catch (Exception packEx)
                {
                    _logger.LogError(packEx,
                        "[S51 Notice] Copy to the Objection Pack {Folder} failed for {ObjectionNo}.",
                        packFolder, objectionNo);
                }
            }

            _logger.LogInformation(
                "[S51 Notice] Notice PDF saved for {ObjectionNo} → {Path}",
                objectionNo, result.PdfPath);
        }
        catch (Exception ex)
        {
            result.Error = "The Section 51 notice PDF could not be created.";
            _logger.LogError(ex, "[S51 Notice] Notice PDF failed for {ObjectionNo}.", objectionNo);
            await RecordAsync(conn, cfg, request, owner, ownerEmail, batchName, letterDate, closingDate, emailed: false);
            return result;
        }

        // ── 3. Email ─────────────────────────────────────────────────
        //  Owner email    → owner, CC Valuation Enquiries, BCC roll tracking mailbox
        //  No owner email → roll tracking mailbox (e.g. GV23Supp4@joburg.org.za),
        //                   so the notice is tracked; the PDF is still posted.
        //  Test mode      → test recipient, CC as above.
        var trackingEmail =
            _config[$"Section51Rolls:{request.RollSource}:TrackingEmail"] is { Length: > 0 } rollTracking
                ? rollTracking
                : settings["TrackingEmail"];

        var plan = V2_Genesis.Services.Section51.Section51Recipients.Resolve(
            ownerEmail, testMode, testRecipient, ccAddress, trackingEmail);

        if (!plan.OwnerHasEmail)
        {
            _logger.LogWarning(
                "[S51 Notice] No owner email in {Table} for {ObjectionNo} (PREMISE_ID {PremiseId}). PDF saved for printing: {Path}. Tracking mailbox: {Tracking}",
                cfg.PostalAddressTable, objectionNo, request.PremiseId, result.PdfPath,
                string.IsNullOrWhiteSpace(trackingEmail) ? "(not set)" : trackingEmail);
        }

        if (!plan.CanSend)
        {
            _logger.LogError(
                testMode
                    ? "[S51 Notice] Section51:Notice:TestMode is on but TestRecipient is empty; notice not emailed for {ObjectionNo}."
                    : "[S51 Notice] No owner email and no TrackingEmail for this roll; notice not emailed for {ObjectionNo}.",
                objectionNo);
        }
        else
        {
            try
            {
                await _emailService.SendSection51NoticeAsync(new Section51NoticeEmail
                {
                    ObjectionNo = objectionNo,
                    PropertyDescription = propertyDescription,
                    ValuationKey = request.ValuationKey,
                    RollName = cfg.RollName,
                    Section51Pin = request.Section51Pin,
                    SubmissionsCloseDate = closingDate,
                    PortalUrl = portalUrl,
                    StaffPortalUrl = staffPortalUrl,
                    OwnerEmail = ownerEmail ?? string.Empty,
                    OwnerHasEmail = plan.OwnerHasEmail,
                    ToAddress = plan.To!,
                    CcAddresses = plan.Cc.ToList(),
                    BccAddresses = plan.Bcc.ToList(),
                    IsTest = testMode,
                    PdfBytes = pdf,
                    PdfFileName = pdfFileName,
                    EmlFolderPath = emailFolder,
                    EmlExtraFolders = string.IsNullOrWhiteSpace(packFolder)
                        ? new List<string>()
                        : new List<string> { packFolder },
                    EmlFileName =
                        $"email_{SafeFileName(objectionNo)}_{SafeFileName(propertyDescription)}_{SafeFileName(plan.OwnerHasEmail ? ownerEmail! : "No owner email")}.eml"
                });

                // Section51_Emailed = 'Y' only when the OWNER got it. A notice that
                // only went to the tracking mailbox still has to be posted.
                result.Emailed = plan.OwnerHasEmail;
            }
            catch (Exception ex)
            {
                result.Error = "The Section 51 notice could not be emailed; the PDF was saved for printing.";
                _logger.LogError(ex,
                    "[S51 Notice] Email failed for {ObjectionNo} (to {To}). PDF kept at {Path}.",
                    objectionNo, plan.To, result.PdfPath);
            }
        }

        // ── 4 + 5. Section51Table + Obj_Property_Info flag ──────────
        await RecordAsync(conn, cfg, request, owner, ownerEmail, batchName, letterDate, closingDate, result.Emailed);

        return result;
    }

    /// WefDate of the property on the roll (property detail of the roll,
    /// e.g. [Objection_Supp4].[dbo].[Sup4]). Returns null when the roll has
    /// no WefDate for this property, so the notice shows it empty.
    private async Task<string?> GetRollWefDateAsync(Section51NoticeRequest request)
    {
        try
        {
            var unitKey = V2_Genesis.Helpers.FloatKeyHelper.Normalize(request.UnitKey);
            var valuationKey = V2_Genesis.Helpers.FloatKeyHelper.Normalize(request.ValuationKey);

            if (string.IsNullOrWhiteSpace(unitKey) && string.IsNullOrWhiteSpace(valuationKey))
                return null;

            var rows = await _propertySearch.GetPropertyDetailsAsync(
                request.RollSource,
                unitKey ?? string.Empty,
                valuationKey ?? string.Empty);

            var row = rows.FirstOrDefault(r =>
                          !string.IsNullOrWhiteSpace(r.WefDate) &&
                          string.Equals(
                              V2_Genesis.Helpers.FloatKeyHelper.Normalize(r.ValuationKey),
                              valuationKey,
                              StringComparison.OrdinalIgnoreCase))
                      ?? rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.WefDate));

            var raw = row?.WefDate?.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                return null;

            // Day-first, written in full: "10 January 2025".
            return V2_Genesis.Helpers.WefDateFormatter.Format(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[S51 Notice] Could not read WefDate from the roll for {ObjectionNo} ({Roll}).",
                request.ObjectionNo, request.RollSource);
            return null;
        }
    }

    private static string FirstFolder(params string?[] folders) =>
        folders.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))?.Trim() ?? string.Empty;

    private sealed class OwnerPostalRow
    {
        public string? Addr1 { get; set; }
        public string? Addr2 { get; set; }
        public string? Addr3 { get; set; }
        public string? Addr4 { get; set; }
        public string? Addr5 { get; set; }
        public string? Email { get; set; }
    }

    private static readonly Regex SqlIdentifier =
        new(@"^(?:[A-Za-z0-9_]+\.)?[A-Za-z0-9_]+$", RegexOptions.Compiled);

    private static async Task<OwnerPostalRow?> LoadOwnerPostalAsync(
        SqlConnection conn,
        string table,
        string? premiseId)
    {
        if (string.IsNullOrWhiteSpace(premiseId))
            return null;

        table = table.Trim().Replace("[", "").Replace("]", "");

        // The table name comes from appsettings; only plain names are allowed.
        if (!SqlIdentifier.IsMatch(table))
            throw new InvalidOperationException($"Invalid PostalAddressTable name '{table}'.");

        var parts = table.Split('.');
        var qualified = parts.Length == 2
            ? $"[{parts[0]}].[{parts[1]}]"
            : $"[dbo].[{parts[0]}]";

        var hasWefDate = await conn.ExecuteScalarAsync<int?>(
            "SELECT COL_LENGTH(@Table, 'WEF_DATE');",
            new { Table = qualified }) is > 0;

        var orderBy = hasWefDate ? "WEF_DATE DESC" : "(SELECT NULL)";

        var sql = $@"
SELECT TOP (1)
    CAST(ADDR1 AS nvarchar(255))      AS Addr1,
    CAST(ADDR2 AS nvarchar(255))      AS Addr2,
    CAST(ADDR3 AS nvarchar(255))      AS Addr3,
    CAST(ADDR4 AS nvarchar(255))      AS Addr4,
    CAST(ADDR5 AS nvarchar(255))      AS Addr5,
    CAST(EMAIL_ADDR AS nvarchar(255)) AS Email
FROM {qualified}
WHERE LTRIM(RTRIM(CAST(PREMISE_ID AS nvarchar(100)))) = @PremiseId
ORDER BY {orderBy};";

        return await conn.QueryFirstOrDefaultAsync<OwnerPostalRow>(
            sql,
            new { PremiseId = premiseId.Trim() });
    }

    private async Task RecordAsync(
        SqlConnection conn,
        Section51RollConfig cfg,
        Section51NoticeRequest request,
        OwnerPostalRow? owner,
        string? ownerEmail,
        string batchName,
        DateTime batchDate,
        DateTime closingDate,
        bool emailed)
    {
        var objectionNo = request.ObjectionNo.Trim();
        var s6 = request.Section6;

        try
        {
            await conn.ExecuteAsync(@"
IF OBJECT_ID('dbo.Section51Table') IS NOT NULL
INSERT INTO dbo.Section51Table
(
    RollId, BatchName, BatchDate, ClosingDate,
    ObjectionNo, PremiseId, ValuationKey, PropertyDescription,
    ADDR1, ADDR2, ADDR3, ADDR4, ADDR5, Email,
    Section51Pin, RandomPin,
    OldCategory, OldCategory1, OldCategory2,
    OldMarketValue, OldMarketValue1, OldMarketValue2,
    OldExtent, OldExtent1, OldExtent2,
    NewCategory, NewCategory1, NewCategory2,
    NewMarketValue, NewMarketValue1, NewMarketValue2,
    NewExtent, NewExtent1, NewExtent2,
    WEFDATE, CreatedAtUtc
)
VALUES
(
    @RollId, @BatchName, @BatchDate, @ClosingDate,
    @ObjectionNo, @PremiseId, @ValuationKey, @PropertyDescription,
    @Addr1, @Addr2, @Addr3, @Addr4, @Addr5, @Email,
    @Section51Pin, @RandomPin,
    @OldCategory, @OldCategory1, @OldCategory2,
    @OldMarketValue, @OldMarketValue1, @OldMarketValue2,
    @OldExtent, @OldExtent1, @OldExtent2,
    @NewCategory, @NewCategory1, @NewCategory2,
    @NewMarketValue, @NewMarketValue1, @NewMarketValue2,
    @NewExtent, @NewExtent1, @NewExtent2,
    NULL, @CreatedAtUtc
);",
                new
                {
                    cfg.RollId,
                    BatchName = batchName,
                    BatchDate = batchDate,
                    ClosingDate = closingDate,
                    ObjectionNo = objectionNo,
                    PremiseId = request.PremiseId,
                    ValuationKey = request.ValuationKey,
                    PropertyDescription = request.PropertyDescription,
                    owner?.Addr1,
                    owner?.Addr2,
                    owner?.Addr3,
                    owner?.Addr4,
                    owner?.Addr5,
                    Email = ownerEmail,
                    request.Section51Pin,
                    request.RandomPin,
                    OldCategory = s6?.Old_Category,
                    OldCategory1 = s6?.Old2_Category,
                    OldCategory2 = s6?.Old3_Category,
                    OldMarketValue = s6?.Old_Market_Value,
                    OldMarketValue1 = s6?.Old2_Market_Value,
                    OldMarketValue2 = s6?.Old3_Market_Value,
                    OldExtent = s6?.Old_Extent,
                    OldExtent1 = s6?.Old2_Extent,
                    OldExtent2 = s6?.Old3_Extent,
                    NewCategory = s6?.New_Category,
                    NewCategory1 = s6?.New2_Category,
                    NewCategory2 = s6?.New3_Category,
                    NewMarketValue = s6?.New_Market_Value,
                    NewMarketValue1 = s6?.New2_Market_Value,
                    NewMarketValue2 = s6?.New3_Market_Value,
                    NewExtent = s6?.New_Extent,
                    NewExtent1 = s6?.New2_Extent,
                    NewExtent2 = s6?.New3_Extent,
                    CreatedAtUtc = DateTime.UtcNow
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[S51 Notice] Could not insert the Section51Table row for {ObjectionNo}.",
                objectionNo);
        }

        try
        {
            await conn.ExecuteAsync(@"
IF COL_LENGTH('dbo.Obj_Property_Info', 'Section51_Emailed') IS NOT NULL
    EXEC sp_executesql
        N'UPDATE dbo.Obj_Property_Info
             SET Section51_Emailed = @Flag
           WHERE LTRIM(RTRIM(Objection_No)) = @ObjectionNo',
        N'@Flag char(1), @ObjectionNo nvarchar(100)',
        @Flag, @ObjectionNo;",
                new { Flag = emailed ? "Y" : "N", ObjectionNo = objectionNo });

            _logger.LogInformation(
                "[S51 Notice] {ObjectionNo}: Section51_Emailed = {Flag}",
                objectionNo, emailed ? "Y" : "N");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[S51 Notice] Could not update Section51_Emailed for {ObjectionNo}.",
                objectionNo);
        }
    }

    private static string? FirstValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value
            .Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x =>
                x.Contains('@') &&
                x.IndexOf('.', x.IndexOf('@')) > x.IndexOf('@') + 1 &&
                System.Net.Mail.MailAddress.TryCreate(x, out _));
    }

    private static string SafeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "NA";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 90 ? cleaned[..90] : cleaned;
    }
}