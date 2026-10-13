using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using V2_Genesis.Helpers;
using Microsoft.Extensions.Options;
using System.Data;
using System.Net;
using System.Reflection.Metadata;
using System.Security.Claims;
using System.Web;
using V2_Genesis.Models.Entities;
using V2_Genesis.Models.ViewModels.Account;
using V2_Genesis.Services;
using V2_Genesis.Services.Interfaces;


namespace V2_Genesis.Controllers
{
    [Controller]
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IEmailService _email;
        private readonly IReCaptchaService _captcha;
        private readonly IUserManagementService _umService;
        private readonly AppSettings _app;
        private readonly SessionSettings _session;
        private readonly string _captchaSiteKey;
        private readonly bool _captchaV3;
        private readonly ILogger<AccountController> _logger;
        private readonly INotificationService _notifications;

        private readonly IDataProtector _sapProtector;
        private const string SAP_COOKIE = "adm_sap_ok";          // cookie name
        private const int SAP_HOURS = 8;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            IEmailService emailService,
            IReCaptchaService reCaptcha,
            IUserManagementService umService,
            IOptions<AppSettings> appOpts,
            IOptions<SessionSettings> sessionOpts,
            IOptions<ReCaptchaSettings> captchaOpts,
            ILogger<AccountController> logger,
            IDataProtectionProvider dataProtection,
            INotificationService notifications)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _email = emailService;
            _captcha = reCaptcha;
            _umService = umService;
            _app = appOpts.Value;
            _session = sessionOpts.Value;
            _captchaSiteKey = captchaOpts.Value.SiteKey;
            _captchaV3 = captchaOpts.Value.IsV3;
            _logger = logger;
            _notifications = notifications;
            _sapProtector = dataProtection.CreateProtector("SapRemember.v1");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CLIENT PROFILE
        // ══════════════════════════════════════════════════════════════════════

        [HttpGet]
        [Authorize(Roles = "Client")]
        [Route("my-profile")]
        public async Task<IActionResult> Profile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
                return Challenge();

            return View(BuildProfilePage(user));
        }

        [HttpPost]
        [Authorize(Roles = "Client")]
        [ValidateAntiForgeryToken]
        [Route("my-profile/contact")]
        public async Task<IActionResult> UpdateProfileContact(
            UpdateProfileContactViewModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
                return Challenge();

            if (!ModelState.IsValid)
                return ProfileValidationView(user, "contactModal");

            if (!await _userManager.CheckPasswordAsync(user, model.CurrentPassword))
            {
                ModelState.AddModelError(nameof(model.CurrentPassword),
                    "The current password is incorrect.");
                return ProfileValidationView(user, "contactModal");
            }

            var newEmail = model.Email.Trim();
            var newPhone = string.IsNullOrWhiteSpace(model.PhoneNumber)
                ? null
                : model.PhoneNumber.Trim();

            var emailChanged = !string.Equals(
                user.Email,
                newEmail,
                StringComparison.OrdinalIgnoreCase);
            var phoneChanged = !string.Equals(
                user.PhoneNumber?.Trim(),
                newPhone,
                StringComparison.OrdinalIgnoreCase);

            if (!emailChanged && !phoneChanged)
            {
                TempData["ProfileInfo"] = "No account details were changed.";
                return RedirectToAction(nameof(Profile));
            }

            if (emailChanged)
            {
                var existingUser = await _userManager.FindByEmailAsync(newEmail);
                if (existingUser is not null && existingUser.Id != user.Id)
                {
                    ModelState.AddModelError(nameof(model.Email),
                        "That email address is already registered.");
                    return ProfileValidationView(user, "contactModal");
                }
            }

            var oldEmail = user.Email;
            user.Email = newEmail;
            user.UserName = newEmail;
            user.NormalizedEmail = _userManager.NormalizeEmail(newEmail);
            user.NormalizedUserName = _userManager.NormalizeName(newEmail);
            user.EmailConfirmed = true;
            user.PhoneNumber = newPhone;

            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                foreach (var error in updateResult.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);

                return ProfileValidationView(user, "contactModal");
            }

            await _userManager.UpdateSecurityStampAsync(user);
            await _signInManager.RefreshSignInAsync(user);

            var changedFields = new List<string>();
            if (emailChanged) changedFields.Add("Email address");
            if (phoneChanged) changedFields.Add("Phone number");

            await RecordAccountChangeAsync(
                user,
                "Account details updated",
                $"Your {string.Join(" and ", changedFields).ToLowerInvariant()} was changed successfully.",
                changedFields,
                oldEmail);

            TempData["ProfileSuccess"] = "Your contact details were updated successfully.";
            return RedirectToAction(nameof(Profile));
        }

        [HttpPost]
        [Authorize(Roles = "Client")]
        [ValidateAntiForgeryToken]
        [Route("my-profile/password")]
        public async Task<IActionResult> ChangeProfilePassword(
            ChangeProfilePasswordViewModel model)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
                return Challenge();

            if (!ModelState.IsValid)
                return ProfileValidationView(user, "passwordModal");

            var result = await _userManager.ChangePasswordAsync(
                user,
                model.CurrentPassword,
                model.NewPassword);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);

                return ProfileValidationView(user, "passwordModal");
            }

            await _signInManager.RefreshSignInAsync(user);

            await RecordAccountChangeAsync(
                user,
                "Password changed",
                "Your account password was changed successfully.",
                new[] { "Password" },
                user.Email);

            TempData["ProfileSuccess"] = "Your password was changed successfully.";
            return RedirectToAction(nameof(Profile));
        }

        private IActionResult ProfileValidationView(ApplicationUser user, string modalId)
        {
            ViewBag.OpenProfileModal = modalId;

            if (modalId == "contactModal" && Request.HasFormContentType)
            {
                ViewBag.ContactEmail = Request.Form["Email"].ToString();
                ViewBag.ContactPhone = Request.Form["PhoneNumber"].ToString();
            }

            return View("Profile", BuildProfilePage(user));
        }

        private static ProfilePageViewModel BuildProfilePage(ApplicationUser user) => new()
        {
            DisplayName = user.DisplayName,
            AccountType = user.IsCompany ? "Company Account" : "Individual Account",
            Email = user.Email ?? string.Empty,
            PhoneNumber = user.PhoneNumber,
            CreationDate = user.CreationDate
        };

        private async Task RecordAccountChangeAsync(
            ApplicationUser user,
            string notificationTitle,
            string notificationMessage,
            IReadOnlyCollection<string> changedFields,
            string? previousEmail)
        {
            try
            {
                await _notifications.CreateClientNotificationAsync(
                    user.Id,
                    user.Email,
                    notificationTitle,
                    notificationMessage,
                    null,
                    null,
                    null,
                    "AspNetUsers",
                    "/my-profile",
                    user.Email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Profile updated but notification creation failed for {UserId}",
                    user.Id);
            }

            var profileUrl = Url.Action(
                nameof(Profile),
                "Account",
                values: null,
                protocol: Request.Scheme) ?? "/my-profile";

            var notificationEmails = new[] { user.Email, previousEmail }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var emailAddress in notificationEmails)
            {
                try
                {
                    await _email.SendAccountDetailsChangedAsync(
                        emailAddress,
                        user.DisplayName,
                        changedFields,
                        DateTime.Now,
                        profileUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Profile updated but account-change email failed for {UserId} to {Email}",
                        user.Id,
                        emailAddress);
                    TempData["ProfileWarning"] =
                        "Your account was updated, but one or more confirmation emails could not be sent.";
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  REGISTER
        // ══════════════════════════════════════════════════════════════════════
        private static readonly Regex AdminPattern =
     new(@"^val\.admin(1[0-9]?|[1-9])@joburg\.org\.za$",
         RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const string AdminAppEmail = "AdministrationEnquiries@Joburg.org.za";
        private const string PendingAdminLoginEmailKey = "PendingAdminLoginEmail";

        private static bool IsAdminLoginEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            var cleanEmail = email.Trim();

            return cleanEmail.Equals(
                "AdministrationEnquiries@Joburg.org.za",
                StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet]
        [Route("register")]
        [AllowAnonymous]
        public IActionResult Register(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToUserDashboard();

            ViewBag.ReturnUrl = returnUrl;
            return View(new RegisterPageViewModel { ActiveTab = "individual" });
        }

        [HttpPost]
        [Route("register/individual")]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterIndividual(RegisterPageViewModel model, string? returnUrl = null)
        {
            model.ActiveTab = "individual";
            ViewBag.ReturnUrl = returnUrl;

            
            // Validate only the Individual sub-model
            ClearCompanyErrors();

            // Terms / POPIA
            if (!model.Individual.AcceptTerms)
            {
                ModelState.AddModelError(
                    "Individual.AcceptTerms",
                    "You must accept the Terms of Use and POPIA Notice.");
            }

            // Must have either ID or Passport
            if (string.IsNullOrWhiteSpace(model.Individual.IDNumber) &&
                string.IsNullOrWhiteSpace(model.Individual.PassportNumber))
            {
                ModelState.AddModelError(
                    "Individual.IDNumber",
                    "Please provide either a South African ID number or a passport number.");
            }

            if (!ModelState.IsValid)
                return View("Register", model);

            var user = new ApplicationUser
            {
                UserName = model.Individual.Email,
                Email = model.Individual.Email,
                FirstName = model.Individual.FirstName.Trim(),
                LastName = model.Individual.LastName.Trim(),
                PhoneNumber = model.Individual.PhoneNumber?.Trim(),
                IDNumber = model.Individual.IDNumber?.Trim(),
                PassportNumber = model.Individual.PassportNumber?.Trim(),
                CreationDate = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(user, model.Individual.Password);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);

                return View("Register", model);
            }

            // Assign Client role
            await EnsureRoleAsync("Client");
            await _userManager.AddToRoleAsync(user, "Client");

            // Send email confirmation
            await SendConfirmationEmailAsync(user);

            _logger.LogInformation("Individual account created for {Email}", user.Email);
            return RedirectToAction(nameof(RegisterConfirmation));
        }

        [HttpPost]
        [Route("register/company")]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterCompany(RegisterPageViewModel model, string? returnUrl = null)
        {
            model.ActiveTab = "company";
            ViewBag.ReturnUrl = returnUrl;

            // Validate only the Company sub-model
            ClearIndividualErrors();

            if (!model.Company.AcceptTerms)
            {
                ModelState.AddModelError(
                    "Company.AcceptTerms",
                    "You must accept the Terms of Use and POPIA Notice.");
            }

            if (!ModelState.IsValid)
                return View("Register", model);

            var user = new ApplicationUser
            {
                UserName = model.Company.Email,
                Email = model.Company.Email,
                PhoneNumber = model.Company.PhoneNumber.Trim(),
                CompanyName = model.Company.CompanyName.Trim(),
                CompanyRegistration = model.Company.CompanyRegistration.Trim(),
                CreationDate = DateTime.UtcNow
            };

            var result = await _userManager.CreateAsync(user, model.Company.Password);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);

                return View("Register", model);
            }

            await EnsureRoleAsync("Client");
            await _userManager.AddToRoleAsync(user, "Client");
            await SendConfirmationEmailAsync(user);

            _logger.LogInformation("Company account created for {Email}", user.Email);
            return RedirectToAction(nameof(RegisterConfirmation));
        }

        [HttpGet]
        [Route("register-success")]
        [AllowAnonymous]
        public IActionResult RegisterConfirmation() => View();

        // ══════════════════════════════════════════════════════════════════════
        //  CONFIRM EMAIL
        // ══════════════════════════════════════════════════════════════════════

        [HttpGet]
        [Route("confirm-email")]
        [AllowAnonymous]
        public async Task<IActionResult> ConfirmEmail(string userId, string code)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
                return RedirectToAction(nameof(Login));

            var user = await _userManager.FindByIdAsync(userId);
            if (user is null)
                return RedirectToAction(nameof(Login));

            var token = HttpUtility.UrlDecode(code).Replace(" ", "+");
            var result = await _userManager.ConfirmEmailAsync(user, token);

            ViewBag.Success = result.Succeeded;
            return View();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  LOGIN
        // ══════════════════════════════════════════════════════════════════════

        [HttpGet]
        [Route("login")]
        [AllowAnonymous]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToUserDashboard();

            ViewBag.ReturnUrl = returnUrl;
            ViewBag.RecaptchaSiteKey = _captchaSiteKey;
            ViewBag.RecaptchaV3 = _captchaV3;
            ViewBag.PendingLink = PendingPropertyLink.Read(HttpContext);
            return View(new LoginViewModel());
        }

        [HttpPost]
        [Route("login")]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(
     LoginViewModel model,
     string? returnUrl = null)
        {
            ViewBag.ReturnUrl = returnUrl;
            ViewBag.RecaptchaSiteKey = _captchaSiteKey;
            ViewBag.RecaptchaV3 = _captchaV3;
            ViewBag.PendingLink = PendingPropertyLink.Read(HttpContext);

            // 1. Validate email + password fields first
            if (!ModelState.IsValid)
                return View(model);

            // 2. reCAPTCHA
            // IMPORTANT:
            // Always let ReCaptchaService decide whether verification is required.
            // This allows the UAT bypass to work.
            var captchaToken =
                Request.Form["g-recaptcha-response"].ToString();

            var captchaValid =
                await _captcha.VerifyAsync(captchaToken);

            if (!captchaValid)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Please complete the reCAPTCHA verification.");

                return View(model);
            }

            // 3. Find user
            var user =
                await _userManager.FindByEmailAsync(model.Email);

            if (user is null)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Invalid email or password.");

                return View(model);
            }

            // 4. Check lockout BEFORE checking password
            if (await _userManager.IsLockedOutAsync(user))
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Your account has been temporarily locked due to multiple failed attempts. Please try again later.");

                return View(model);
            }

            // 5. Verify password
            var passwordOk =
                await _userManager.CheckPasswordAsync(
                    user,
                    model.Password);

            if (!passwordOk)
            {
                await _userManager.AccessFailedAsync(user);

                ModelState.AddModelError(
                    string.Empty,
                    "Invalid email or password.");

                return View(model);
            }

            // 6. Admin detection
            if (IsAdminLoginEmail(model.Email))
            {
                await _userManager.ResetAccessFailedCountAsync(user);

                HttpContext.Session.SetString(
                    PendingAdminLoginEmailKey,
                    AdminAppEmail);

                await _signInManager.SignOutAsync();

                return RedirectToAction(nameof(WindowsLogin));
            }

            // 7. Client must have confirmed email
            if (!await _userManager.IsEmailConfirmedAsync(user))
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Your email address has not been confirmed. Please check your inbox for the confirmation link.");

                return View(model);
            }

            // 8. Sign client in
            await _userManager.ResetAccessFailedCountAsync(user);

            await _signInManager.SignInAsync(
                user,
                isPersistent: model.RememberMe);

            _logger.LogInformation(
                "Client {Email} signed in.",
                user.Email);

            if (!string.IsNullOrEmpty(returnUrl) &&
                Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            // A property link started before sign-in (possibly before
            // registering) — finish it now.
            var pendingLink = PendingPropertyLink.Read(HttpContext);
            if (pendingLink is not null && Url.IsLocalUrl(pendingLink.Url))
            {
                _logger.LogInformation(
                    "Client {Email} signed in with a pending property link for {Roll}.",
                    user.Email,
                    pendingLink.RollSource);

                return LocalRedirect(pendingLink.Url);
            }

            return RedirectToAction(
                "Index",
                "Dashboard");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ADMIN – SAP STEP
        // ══════════════════════════════════════════════════════════════════════

        //    [HttpPost]
        //    [Route("verify-identity")]
        //    [AllowAnonymous]
        //    [ValidateAntiForgeryToken]
        //    public async Task<IActionResult> SapStep(SapStepViewModel model)
        //    {
        //        if (HttpContext.Session.GetString(_session.AdminPendingKey) != "true")
        //            return RedirectToAction(nameof(Login));

        //        if (!ModelState.IsValid)
        //            return View(model);

        //        var umResult = await _umService.ValidateAdminAsync(model.SapNumber);

        //        if (umResult is null)
        //        {
        //            ModelState.AddModelError(string.Empty,
        //                "SAP number not recognised or you do not have access to this system.");
        //            return View(model);
        //        }

        //        var userId = HttpContext.Session.GetString(_session.AdminEmailKey)!;
        //        var user = await _userManager.FindByIdAsync(userId);

        //        if (user is null)
        //        {
        //            HttpContext.Session.Clear();
        //            return RedirectToAction(nameof(Login));
        //        }

        //        await EnsureRoleAsync("Admin");
        //        if (!await _userManager.IsInRoleAsync(user, "Admin"))
        //            await _userManager.AddToRoleAsync(user, "Admin");

        //        var sapValue = $@"{_app.SapDomain}\{model.SapNumber.Trim()}";
        //        var fullName = umResult.FullName;           // "John Smith"  — computed from FirstName+Surname
        //        var position = umResult.Position?.Trim() ?? string.Empty;


        //        HttpContext.Session.SetString("AdminSapNumber", sapValue);
        //        HttpContext.Session.SetString("AdminFullName", fullName);
        //        HttpContext.Session.SetString("AdminPosition", position);
        //        HttpContext.Session.SetString("AdminUMRole", umResult.Role ?? "Admin");

        //        var additionalClaims = new List<Claim>
        //{
        //     new Claim("SAPNumber", sapValue),
        //new Claim("UMRole", umResult.Role ?? "Admin"),
        //new Claim("FullName", fullName),
        //new Claim("Position", position),       // "Senior Valuer"
        //};

        //        HttpContext.Session.Remove(_session.AdminPendingKey);
        //        HttpContext.Session.Remove(_session.AdminEmailKey);

        //        // ── 8-hour remember cookie ────────────────────────────────
        //        if (model.RememberEightHours)
        //        {
        //            var expiresAt = DateTimeOffset.UtcNow.AddHours(SAP_HOURS);
        //            // cookie segments: email|expiry|SAPvalue|FullName|Position
        //            var payload = $"{user.Email}|{expiresAt:O}|{sapValue}|{fullName}|{position}";
        //            var cookieToken = _sapProtector.Protect(payload);

        //            Response.Cookies.Append(SAP_COOKIE, cookieToken, new CookieOptions
        //            {
        //                Expires = expiresAt,
        //                HttpOnly = true,
        //                Secure = true,
        //                SameSite = SameSiteMode.Strict,
        //                Path = "/"
        //            });
        //        }

        //        await _signInManager.SignInWithClaimsAsync(
        //            user,
        //            isPersistent: model.RememberEightHours,
        //            additionalClaims);

        //        _logger.LogInformation("Admin {SAP} signed in as '{Name}' ({Position}).",
        //            model.SapNumber, fullName, position);

        //        return RedirectToAction("Index", "Admin");
        //    }


        //    // ── ALSO REPLACE the GET SapStep action (restores Position from cookie) ──
        //    [HttpGet]
        //    [Route("verify-identity")]
        //    [AllowAnonymous]
        //    public async Task<IActionResult> SapStep()
        //    {
        //        if (HttpContext.Session.GetString(_session.AdminPendingKey) != "true")
        //            return RedirectToAction(nameof(Login));

        //        // ── Check 8-hour remember cookie ──────────────────────────
        //        if (Request.Cookies.TryGetValue(SAP_COOKIE, out var token))
        //        {
        //            try
        //            {
        //                var payload = _sapProtector.Unprotect(token);
        //                var parts = payload.Split('|');
        //                // segments: email|expiry|SAPvalue|FullName|Position
        //                var cookieEmail = parts[0];
        //                var expiresAt = DateTimeOffset.Parse(parts[1]);
        //                var pendingId = HttpContext.Session.GetString(_session.AdminEmailKey);

        //                if (expiresAt > DateTimeOffset.UtcNow && !string.IsNullOrEmpty(pendingId))
        //                {
        //                    var user = await _userManager.FindByIdAsync(pendingId);
        //                    if (user is not null
        //                        && cookieEmail.Equals(user.Email, StringComparison.OrdinalIgnoreCase))
        //                    {
        //                        await EnsureRoleAsync("Admin");
        //                        if (!await _userManager.IsInRoleAsync(user, "Admin"))
        //                            await _userManager.AddToRoleAsync(user, "Admin");

        //                        var sapClaim = parts.Length > 2 ? parts[2] : "";
        //                        var fullName = parts.Length > 3 ? parts[3] : "";
        //                        var position = parts.Length > 4 ? parts[4] : "";

        //                        HttpContext.Session.SetString("AdminSapNumber", sapClaim);
        //                        HttpContext.Session.SetString("AdminFullName", fullName);
        //                        HttpContext.Session.SetString("AdminPosition", position);
        //                        HttpContext.Session.SetString("AdminUMRole", "Admin");

        //                        var additionalClaims = new List<Claim>
        //                {
        //                      new Claim("SAPNumber", sapClaim),
        //                        new Claim("UMRole", "Admin"),
        //                        new Claim("FullName", fullName),
        //                        new Claim("Position", position),
        //                };

        //                        HttpContext.Session.Remove(_session.AdminPendingKey);
        //                        HttpContext.Session.Remove(_session.AdminEmailKey);

        //                        await _signInManager.SignInWithClaimsAsync(
        //                            user, isPersistent: true, additionalClaims);

        //                        _logger.LogInformation(
        //                            "Admin {Email} auto-signed in via 8-hour cookie as '{Name}'.",
        //                            user.Email, fullName);

        //                        return RedirectToAction("Index", "Admin");
        //                    }
        //                }
        //            }
        //            catch
        //            {
        //                Response.Cookies.Delete(SAP_COOKIE);
        //            }
        //        }

        //        return View(new SapStepViewModel());
        //    }

        [HttpGet]
        [Route("admin/windows-login")]
        [AllowAnonymous]
        public async Task<IActionResult> WindowsLogin()
        {
            const string AdminAppEmail = "AdministrationEnquiries@Joburg.org.za";

            // ── 0. Only after step 1 on /login (e-mail + password + reCAPTCHA).
            //       Opening /admin/windows-login directly skipped all three.
            if (string.IsNullOrWhiteSpace(HttpContext.Session.GetString(PendingAdminLoginEmailKey)))
            {
                _logger.LogWarning(
                    "WindowsLogin: opened without the e-mail / password / reCAPTCHA step; sent to /login.");
                return RedirectToAction(nameof(Login));
            }

            // ── 1. Authenticate via Windows / Negotiate ───────────────────
            var authResult = await HttpContext.AuthenticateAsync(
                NegotiateDefaults.AuthenticationScheme);

            if (!authResult.Succeeded)
            {
                _logger.LogInformation("WindowsLogin: issuing Negotiate challenge.");
                return Challenge(NegotiateDefaults.AuthenticationScheme);
            }

            var windowsName = authResult.Principal?.Identity?.Name;

            if (string.IsNullOrWhiteSpace(windowsName))
            {
                _logger.LogWarning("WindowsLogin: Windows identity name was empty after auth.");
                return RedirectToAction(nameof(Login));
            }

            _logger.LogInformation(
                "WindowsLogin: Windows identity = {WindowsName}",
                windowsName);

            // ── 2. Validate Windows user against UserManagement ────────────
            // Example windowsName: JOBURG\30092655
            var umResult = await _umService.ValidateByWindowsIdentityAsync(windowsName);

            if (umResult is null)
            {
                _logger.LogWarning(
                    "WindowsLogin: {WindowsName} authenticated by Windows but not found in UserManagement.",
                    windowsName);

                return View("_WindowsNoAccess", windowsName);
            }

            // ── 3. Extract Windows audit identity ──────────────────────────
            var sapNumeric = windowsName.Contains('\\')
                ? windowsName.Split('\\').Last()
                : windowsName;

            var sapFull = windowsName;

            var fullName = !string.IsNullOrWhiteSpace(umResult.FullName)
                ? umResult.FullName.Trim()
                : $"{umResult.FirstName} {umResult.Surname}".Trim();

            var position = umResult.Position?.Trim() ?? string.Empty;
            var umRole = string.IsNullOrWhiteSpace(umResult.Role)
                ? "Admin"
                : umResult.Role.Trim();

            // ── 4. IMPORTANT:
            // Always sign into the portal using the fixed admin account.
            // Do NOT use the Windows-authenticated user's email for portal login.
            var user = await _userManager.FindByEmailAsync(AdminAppEmail);

            if (user is null)
            {
                _logger.LogWarning(
                    "WindowsLogin: fixed admin account {AdminEmail} was not found. Creating it.",
                    AdminAppEmail);

                user = new ApplicationUser
                {
                    UserName = AdminAppEmail,
                    Email = AdminAppEmail,
                    EmailConfirmed = true,
                    FirstName = "Administration",
                    LastName = "Enquiries",
                    SAPNumber = sapFull
                };

                var createResult = await _userManager.CreateAsync(user);

                if (!createResult.Succeeded)
                {
                    var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));

                    _logger.LogError(
                        "WindowsLogin: failed to create fixed admin account {AdminEmail}: {Errors}",
                        AdminAppEmail,
                        errors);

                    TempData["Error"] = "Admin account could not be created. Please contact support.";
                    return RedirectToAction(nameof(Login));
                }
            }

            // ── 5. Ensure Admin role for the fixed admin account ───────────
            await EnsureRoleAsync("Admin");

            if (!await _userManager.IsInRoleAsync(user, "Admin"))
            {
                await _userManager.AddToRoleAsync(user, "Admin");
            }

            // ── 6. The person's claims go in THEIR cookie only ────────────
            // Every admin uses this one shared account, so nothing about the
            // person may be stored on it (the last admin to log in would
            // overwrite the others). Remove what older builds stored there.
            await RemoveSharedAdminClaimsAsync(user);

            // ── 7. Sign in using fixed admin account + audit claims ────────
            var additionalClaims = new List<Claim>
    {
        // App account used for pulling admin/dashboard data
        new Claim("AdminAppEmail", AdminAppEmail),
        new Claim(ClaimTypes.Email, AdminAppEmail),

        // Real Windows-authenticated user for audit trail
        new Claim("WindowsUser", windowsName),
        new Claim("SAPNumber", sapFull),
        new Claim("SAPNumeric", sapNumeric),
        new Claim("FullName", fullName),
        new Claim("Position", position),
        new Claim("UMRole", umRole),
        new Claim("LoginType", "AdminWindows")
    };

            HttpContext.Session.Remove(PendingAdminLoginEmailKey);
            HttpContext.Session.SetString("AdminAppEmail", AdminAppEmail);

            // Audit-trail identity
            HttpContext.Session.SetString("AdminWindowsUser", windowsName);
            HttpContext.Session.SetString("AdminSapNumber", sapFull);
            HttpContext.Session.SetString("AdminSapNumeric", sapNumeric);
            HttpContext.Session.SetString("AdminFullName", fullName);
            HttpContext.Session.SetString("AdminPosition", position);
            HttpContext.Session.SetString("AdminUMRole", umRole);

            await _signInManager.SignInWithClaimsAsync(
                user,
                isPersistent: true,
                additionalClaims);

            _logger.LogInformation(
                "WindowsLogin: fixed admin account {AdminEmail} signed in. Windows audit user: {FullName} ({SAP}).",
                AdminAppEmail,
                fullName,
                sapFull);

            // ── 8. Write 8-hour admin bypass cookie using fixed admin email ─
            WriteAdminCookie(
                AdminAppEmail,
                sapFull,
                fullName,
                position);

            return RedirectToAction("Index", "Admin");
        }

        private async Task RemoveSharedAdminClaimsAsync(ApplicationUser user)
        {
            var existing = await _userManager.GetClaimsAsync(user);
            var stale = existing
                .Where(c => AdminLoginClaims.SharedAccountTypes.Contains(c.Type))
                .ToList();

            if (stale.Count > 0)
                await _userManager.RemoveClaimsAsync(user, stale);
        }

        private void WriteAdminCookie(string email, string sapValue, string fullName, string position)
        {
            var expiresAt = DateTimeOffset.UtcNow.AddHours(SAP_HOURS);
            // segments: email | expiry | SAPvalue | FullName | Position
            var payload = $"{email}|{expiresAt:O}|{sapValue}|{fullName}|{position}";
            var cookieToken = _sapProtector.Protect(payload);

            Response.Cookies.Append(SAP_COOKIE, cookieToken, new CookieOptions
            {
                Expires = expiresAt,
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
        }
        // ═══════════════════════════════════════════════════════════════
        //  GET  /admin/windows-login/initiate
        //  Called by the "Login with Windows" button on the login page.
        //  Redirects to the Negotiate-protected endpoint above.
        //  This indirection keeps the main login page as anonymous.
        // ═══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("admin/windows-login/initiate")]
        [AllowAnonymous]
        public IActionResult InitiateWindowsLogin()
        {
            // Simply redirect to the Negotiate-protected action.
            // The browser will handle the Windows auth challenge automatically.
            return Redirect("/admin/windows-login");
        }


        // ══════════════════════════════════════════════════════════════════════
        //  FORGOT / RESET PASSWORD
        // ══════════════════════════════════════════════════════════════════════

        [HttpGet]
        [Route("forgot-password")]
        [AllowAnonymous]
        public IActionResult ForgotPassword() => View(new ForgotPasswordViewModel());

        [HttpPost]
        [Route("forgot-password")]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _userManager.FindByEmailAsync(model.Email);

            // Always show success to prevent email enumeration
            if (user is not null && await _userManager.IsEmailConfirmedAsync(user))
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var encodedToken = HttpUtility.UrlEncode(token);
                var resetLink = Url.Action(
                    nameof(ResetPassword), "Account",
                    new { userId = user.Id, code = encodedToken, email = user.Email }, // ✅ email added
                    Request.Scheme)!;

                await _email.SendPasswordResetEmailAsync(
                    user.Email!, user.DisplayName, resetLink);
            }

            TempData["ForgotSuccess"] = true;
            return RedirectToAction(nameof(ForgotPassword));
        }

        [HttpGet]
        [Route("reset-password")]
        [AllowAnonymous]
        public IActionResult ResetPassword(string? userId = null, string? code = null, string? email = null) // ✅ email added
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
                return RedirectToAction(nameof(Login));

            return View(new ResetPasswordViewModel
            {
                UserId = userId,
                Code = code,
                Email = email ?? string.Empty  // ✅ now populated
            });
        }

        [HttpPost]
        [Route("reset-password")]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var user = await _userManager.FindByIdAsync(model.UserId);
            if (user is null)
            {
                TempData["ResetSuccess"] = true;
                return RedirectToAction(nameof(Login));
            }

            var token = HttpUtility.UrlDecode(model.Code).Replace(" ", "+");
            var result = await _userManager.ResetPasswordAsync(user, token, model.Password);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                    ModelState.AddModelError(string.Empty, error.Description);
                return View(model);
            }

            TempData["ResetSuccess"] = true;
            return RedirectToAction(nameof(Login));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  LOGOUT
        // ══════════════════════════════════════════════════════════════════════

        [HttpPost]
        [Route("logout")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();

            HttpContext.Session.Clear();

            Response.Cookies.Delete(SAP_COOKIE);
            Response.Cookies.Delete("V2Genesis.Auth");
            Response.Cookies.Delete("V2Genesis.Session");

            return RedirectToAction(nameof(Login));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PRIVATE HELPERS
        // ══════════════════════════════════════════════════════════════════════

        private async Task EnsureRoleAsync(string roleName)
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
                await _roleManager.CreateAsync(new IdentityRole(roleName));
        }

        private async Task SendConfirmationEmailAsync(ApplicationUser user)
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = HttpUtility.UrlEncode(token);
            var confirmLink = Url.Action(
                nameof(ConfirmEmail), "Account",
                new { userId = user.Id, code = encodedToken },
                Request.Scheme)!;

            await _email.SendConfirmationEmailAsync(user.Email!, user.DisplayName, confirmLink);
        }

        private void ClearCompanyErrors()
        {
            var companyKeys = ModelState.Keys
                .Where(k => k.StartsWith("Company.", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in companyKeys) ModelState.Remove(key);
        }

        private void ClearIndividualErrors()
        {
            var individualKeys = ModelState.Keys
                .Where(k => k.StartsWith("Individual.", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in individualKeys) ModelState.Remove(key);
        }

        private IActionResult RedirectToUserDashboard() =>
            User.IsInRole("Admin")
                ? RedirectToAction("Index", "Admin")
                : RedirectToAction("Index", "Dashboard");

        private IActionResult LocalRedirectOrDashboard(string? returnUrl)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return LocalRedirect(returnUrl);

            return RedirectToUserDashboard();
        }

        [HttpGet]
        [Route("access-denied")]
        [AllowAnonymous]
        public IActionResult AccessDenied(string? ReturnUrl)
        {
            ViewBag.ReturnUrl = ReturnUrl;
            return View();
        }
    }
}
