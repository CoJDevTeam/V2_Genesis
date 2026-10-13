using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using QuestPDF.Infrastructure;
using Serilog;
using Serilog.Events;
using V2_Genesis.Data;
using V2_Genesis.Filters;
using V2_Genesis.Models;
using V2_Genesis.Models.Configuration;
using V2_Genesis.Models.Emails;
using V2_Genesis.Models.Entities;
using V2_Genesis.Middleware;
using V2_Genesis.Services;
using V2_Genesis.Services.Attributes;
using V2_Genesis.Services.Implementations;
using V2_Genesis.Services.Interfaces;
using V2_Genesis.Services.Logging;
using V2_Genesis.Services.Notice;
using V2_Genesis.Services.Objection;
using V2_Genesis.Services.PropertySearch;


var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

QuestPDF.Settings.License = LicenseType.Community;
V2_Genesis.Helpers.QuestPdfFonts.Configure();   // QuestPDF 2026.9+: Arial for all PDFs

var genesisLogging =
    cfg.GetSection("GenesisLogging")
       .Get<GenesisLoggingSettings>()
    ?? new GenesisLoggingSettings();

if (genesisLogging.Enabled)
{
    Directory.CreateDirectory(
        genesisLogging.RootPath);

    Log.Logger =
        new LoggerConfiguration()
            .MinimumLevel.Information()

            .MinimumLevel.Override(
                "Microsoft",
                LogEventLevel.Warning)

            .MinimumLevel.Override(
                "Microsoft.AspNetCore",
                LogEventLevel.Warning)

            .MinimumLevel.Override(
                "Microsoft.EntityFrameworkCore",
                LogEventLevel.Warning)

            // EF logs every failed query once, then the app logs the same
            // exception again with the real context ("Could not generate
            // acknowledgement for ..."). Keep only the app's entry.
            .MinimumLevel.Override(
                "Microsoft.EntityFrameworkCore.Query",
                LogEventLevel.Fatal)

            .Enrich.FromLogContext()

            .Enrich.WithProperty(
                "Application",
                "Genesis")

            .Enrich.WithProperty(
                "Environment",
                builder.Environment.EnvironmentName)

            .Enrich.WithProperty(
                "MachineName",
                Environment.MachineName)

            .WriteTo.Console()

            .WriteTo.Sink(
                new YearMonthFileSink(
                    genesisLogging.RootPath,
                    genesisLogging.FileSizeLimitMB))

            // Errors only: C:\Genesis Log\Errors\<year>\<month>\genesis-<date>.log
            // Open this file first to see what went wrong, with the full
            // stack trace, without scrolling through every request.
            .WriteTo.Logger(errors => errors
                .Filter.ByIncludingOnly(e => e.Level >= LogEventLevel.Error)
                .WriteTo.Sink(
                    new YearMonthFileSink(
                        Path.Combine(genesisLogging.RootPath, "Errors"),
                        genesisLogging.FileSizeLimitMB)))

            .CreateLogger();

    builder.Host.UseSerilog();
}
// ── MVC ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<
        GenesisActionLoggingFilter>();
});

// ── Session ───────────────────────────────────────────────────────────────────
var sessionMins = cfg.GetValue<int>("Session:TimeoutMinutes", 480);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromMinutes(sessionMins);
    o.Cookie.Name = "V2Genesis.Session";
    o.Cookie.HttpOnly = true;
    o.Cookie.IsEssential = true;
    // Secure on HTTPS (https://objections.joburg.org.za), but still stored
    // on the internal HTTP addresses (http://168.89.201.36:75, UAT :50).
    // "Always" made the browser DROP the cookie on HTTP, so the admin step
    // (e-mail + password → Windows sign-in) lost its session and the login
    // page just reloaded.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.Cookie.SameSite = SameSiteMode.Lax;
});

// ── Database ─────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<ApplicationDbContext>(o =>
    o.UseSqlServer(cfg.GetConnectionString("DefaultConnection")));

builder.Services.AddDbContext<RebateDBContext>(o =>
    o.UseSqlServer(cfg.GetConnectionString("DefaultConnection")));

builder.Services.AddDbContext<AttributesDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("AttributesConnection")));

builder.Services.AddDbContext<QueryDbContext>(o =>
              o.UseSqlServer(builder.Configuration
                 .GetConnectionString("QueryConnection")));

// ── Identity ─────────────────────────────────────────────────────────────────
var idCfg = cfg.GetSection("Identity");
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.SignIn.RequireConfirmedEmail = idCfg.GetValue<bool>("RequireConfirmedEmail", true);
        o.Password.RequiredLength = idCfg.GetValue<int>("PasswordMinLength", 6);
        o.Password.RequireDigit = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = idCfg.GetValue<int>("MaxFailedAccessAttempts", 5);
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(
                                                    idCfg.GetValue<int>("LockoutMinutes", 15));
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddAuthentication().AddNegotiate();

// All admin staff share one Identity account. When Identity re-checks a
// cookie (every 30 minutes) it rebuilds it from that shared account, which
// would drop the person's Windows user / SAP number / name. Keep the
// person's own admin claims so each admin stays themselves.
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
{
    o.OnRefreshingPrincipal = ctx =>
    {
        V2_Genesis.Helpers.AdminLoginClaims.CopyTo(ctx.CurrentPrincipal, ctx.NewPrincipal);
        return Task.CompletedTask;
    };
});
// ── Cookie paths (clean URLs, no /Account/Login) ─────────────────────────────
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/login";
    o.LogoutPath = "/logout";
    o.AccessDeniedPath = "/access-denied";

    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;

    o.Cookie.Name = "V2Genesis.Auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.IsEssential = true;
    // Same as the session cookie: Secure on HTTPS, still works on the
    // internal HTTP address. On Always+HTTP it is never stored, so every
    // request after a "successful" login looks signed-out again.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.Cookie.SameSite = SameSiteMode.Lax;
});

// ── Settings POCOs ────────────────────────────────────────────────────────────
builder.Services.Configure<AppSettings>(cfg.GetSection("AppSettings"));
builder.Services.Configure<EmailSettings>(cfg.GetSection("Email"));
builder.Services.Configure<ReCaptchaSettings>(cfg.GetSection("ReCaptcha"));
builder.Services.Configure<SessionSettings>(cfg.GetSection("Session"));
builder.Services.Configure<ValuationRollSettings>(cfg.GetSection("ValuationRoll"));
builder.Services.Configure<DisclaimerSettings>(cfg.GetSection("Disclaimer"));
builder.Services.Configure<RollDatesSettings>(opts => builder.Configuration.GetSection("RollDates").Bind(opts.Dates));
builder.Services.Configure<ObjectionRollSettings>(cfg => builder.Configuration.Bind(cfg));
builder.Services.Configure<NoticeRollSettings>(opts => builder.Configuration.GetSection("NoticeRolls").Bind(opts.NoticeRolls));
builder.Services.Configure<AttributeStorageOptions>(builder.Configuration.GetSection("AttributeStorage"));
builder.Services.Configure<ValuerPhotoStorageSettings>(builder.Configuration.GetSection("ValuerPhotoStorage"));
builder.Services.Configure<AttributeStorageSettings>(builder.Configuration.GetSection("AttributeStorage"));
builder.Services.Configure<GenesisLoggingSettings>(cfg.GetSection("GenesisLogging"));
// ── Custom Services ───────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<V2_Genesis.Services.IPortalLinks, V2_Genesis.Services.PortalLinks>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IUserManagementService, UserManagementService>();
builder.Services.AddHttpClient<IReCaptchaService, ReCaptchaService>()
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        // ReCaptcha:ProxyUrl – only when the server reaches Google through a proxy.
        var proxyUrl = cfg["ReCaptcha:ProxyUrl"];
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new System.Net.WebProxy(proxyUrl) { UseDefaultCredentials = true };
            handler.UseProxy = true;
        }
        return handler;
    });
builder.Services.AddScoped<IAnnouncementService, AnnouncementService>();
builder.Services.AddScoped<IPropertySearchService, PropertySearchService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IObjectionService, ObjectionService>();
builder.Services.AddScoped<IObjectionFormService, ObjectionFormService>();
builder.Services.AddScoped<IAdminDashboardService, AdminDashboardService>();
builder.Services.AddScoped<IAdminReferenceResolver, AdminReferenceResolver>();
builder.Services.AddScoped<IAdminAccountInformationService, AdminAccountInformationService>();
builder.Services.AddScoped<IAdminRollInformationService, AdminRollInformationService>();
builder.Services.AddScoped<IAdminCaseHistoryService, AdminCaseHistoryService>();
builder.Services.AddScoped<IAdminEnquiryNoticeService, AdminEnquiryNoticeService>();
builder.Services.AddScoped<IAdminPropertyLookupService, AdminPropertyLookupService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<INoticeService, NoticeService>();
builder.Services.AddScoped<ISection53NoticeService, Section53NoticeService>();
builder.Services.AddScoped<IDearJohnnyNoticeService, DearJohnnyNoticeService>();
builder.Services.AddScoped<IInvalidNoticeService, InvalidNoticeService>();
builder.Services.AddScoped<IAppealDecisionNoticeService, AppealDecisionNoticeService>();
builder.Services.AddScoped<IAttributesDashboardService, AttributesDashboardService>();
builder.Services.AddScoped<IEvidenceService, EvidenceService>();
builder.Services.AddScoped<ISection51Service, Section51Service>();
builder.Services.AddScoped<ILisSearchService, LisSearchService>();
builder.Services.AddScoped<IOmissionService, OmissionService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IHomeSearchService, HomeSearchService>();
builder.Services.AddScoped<IAttributesSearchService, AttributesSearchService>();
builder.Services.AddScoped<IAttributeSubmissionService, AttributeSubmissionService>();
builder.Services.AddHostedService<AttributeEvidenceRoutingWorker>();
builder.Services.AddScoped<IAttributeDocumentService, AttributeDocumentService>();
builder.Services.AddScoped<ISection78Service, Section78Service>();
builder.Services.AddScoped<IRebatesService, RebatesService>();
builder.Services.AddScoped<ISubmittedFormPdfService, SubmittedFormPdfService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAdminFormViewService, AdminFormViewService>();
builder.Services.AddScoped<ISupportingDocumentService, SupportingDocumentService>();
builder.Services.AddScoped<IValuerInspectionEvidenceService, ValuerInspectionEvidenceService>();
builder.Services.AddScoped<IPropertyRateCalculatorService, PropertyRateCalculatorService>();
builder.Services.AddScoped<IAcknowledgementDownloadService, AcknowledgementDownloadService>();
builder.Services.AddScoped<ISubmissionViewService, SubmissionViewService>();
builder.Services.AddScoped<IAdminClientAccountService, AdminClientAccountService>();
builder.Services.AddScoped<IAdminPropertyEnquiryService, AdminPropertyEnquiryService>();
builder.Services.AddScoped<GenesisActionLoggingFilter>();
builder.Services.AddScoped<IAttributeInspectionCalendarService, AttributeInspectionCalendarService>();

// Keys that encrypt the sign-in cookie, the session and the form (antiforgery)
// tokens. Kept in a folder so they SURVIVE an app-pool recycle / publish –
// otherwise every recycle signs everyone out and an open login page fails
// with "The antiforgery token could not be decrypted".
// appsettings: "DataProtection": { "KeysPath": "E:\\Genesis Keys" }
var dpKeysPath = cfg["DataProtection:KeysPath"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Genesis");
if (!string.IsNullOrWhiteSpace(dpKeysPath))
{
    Directory.CreateDirectory(dpKeysPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath));
}

// ── App Pipeline ──────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Correlation ID ────────────────────────────────────────────────────────────
// First in the pipeline, so the HTTP request line and any unhandled error
// carry the same ID as the controller log lines of that request.
if (genesisLogging.Enabled)
{
    app.UseMiddleware<CorrelationIdMiddleware>();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// ── Serilog HTTP request logging ─────────────────────────────────────────────
if (genesisLogging.Enabled &&
    genesisLogging.LogRequests)
{
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath}{QueryString} responded {StatusCode} in {Elapsed:0} ms. User={UserName}";

        options.EnrichDiagnosticContext = (diagnostics, http) =>
        {
            diagnostics.Set("QueryString", http.Request.QueryString.Value ?? string.Empty);
            diagnostics.Set(
                "UserName",
                http.User?.Identity?.IsAuthenticated == true
                    ? http.User.Identity!.Name ?? "Authenticated"
                    : "Anonymous");
        };

        // 500s and crashed requests are errors; very slow pages are warnings.
        options.GetLevel = (http, elapsedMs, ex) =>
            ex is not null || http.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : elapsedMs > 10000
                    ? LogEventLevel.Warning
                    : LogEventLevel.Information;
    });
}

app.UseRouting();


app.UseSession();
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();

// Default route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ── Application lifetime logging ─────────────────────────────────────────────
try
{
    Log.Information(
        "Genesis application started. Environment={Environment}, Machine={Machine}",
        app.Environment.EnvironmentName,
        Environment.MachineName);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(
        ex,
        "Genesis application terminated unexpectedly.");

    throw;
}
finally
{
    Log.CloseAndFlush();
}