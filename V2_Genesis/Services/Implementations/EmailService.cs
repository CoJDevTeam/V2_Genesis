using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using V2_Genesis.Data;
using V2_Genesis.Models.Emails;
using V2_Genesis.Services.Interfaces;

namespace V2_Genesis.Services.Implementations
{
    public class EmailService : IEmailService
    {
        private readonly EmailSettings _cfg;
        private readonly AppSettings _app;
        private readonly ILogger<EmailService> _logger;
        private readonly IConfiguration _config;
        private readonly AttributesDbContext _attributesDb;

        public EmailService(
            IOptions<EmailSettings> emailOpts,
            IOptions<AppSettings> appOpts,
            ILogger<EmailService> logger,
            IConfiguration config,
            AttributesDbContext attributesDb,
            V2_Genesis.Services.IPortalLinks links)
        {
            _links = links;
            _cfg = emailOpts.Value;
            _app = appOpts.Value;
            _logger = logger;
            _config = config;
            _attributesDb = attributesDb;
        }

        private readonly V2_Genesis.Services.IPortalLinks _links;

        private static readonly Dictionary<string, string> RollConnections = new()
        {
            ["Objection"] = "DefaultConnection",
            ["Objection_Supp1"] = "Sup1Connection",
            ["Objection_Supp2"] = "Sup2Connection",
            ["Objection_Supp3"] = "Sup3Connection",
            ["Objection_Supp4"] = "Sup4Connection",
            ["Objection_Supp5"] = "Sup5Connection",
        };

        private static readonly Dictionary<string, string> RollTitles = new()
        {
            ["Objection"] = "General Valuation Roll (GV23)",
            ["Objection_Supp1"] = "Supplementary Roll 1",
            ["Objection_Supp2"] = "Supplementary Roll 2",
            ["Objection_Supp3"] = "Supplementary Roll 3",
            ["Objection_Supp4"] = "Supplementary Roll 4",
            ["Objection_Supp5"] = "Supplementary Roll 5",
        };

        // Contact details shown in the submission emails.
        private const string ValuationEnquiriesEmail = "valuationenquiries@joburg.org.za";
        private const string ObjectionEnquiriesPhone = "011 407-6622 / 011 407-6597";
        private const string Section78EnquiriesPhone = "011 084 9823";

        // ════════════════════════════════════════════════════════════
        //  ONE EMAIL LAYOUT FOR EVERY EMAIL
        //
        //  All styles are inline and the layout is table based, so it
        //  looks the same in Outlook, Gmail and on a phone. Every email
        //  body is built from the small helpers below; nothing else in
        //  this class writes its own CSS.
        // ════════════════════════════════════════════════════════════
        private static class EmailStyle
        {
            public const string Font = "font-family:Arial,Helvetica,sans-serif;";
            public const string Gold = "#e6b000";
            public const string Dark = "#1a1a1a";
            public const string Text = "#222222";
            public const string Muted = "#555555";
            public const string Line = "#e5e5e5";
            public const string Soft = "#f7f7f7";
            public const string Page = "#f4f4f4";
            public const string NoticeBg = "#fff8e1";
            public const string NoticeText = "#6b4e00";
        }

        private static string H(string? value) =>
            WebUtility.HtmlEncode(value ?? string.Empty);

        /// <summary>The page around every email: gold header, white body, dark footer.</summary>
        private static string EmailShell(string subtitle, string contentHtml)
        {
            return $@"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8' />
<meta name='viewport' content='width=device-width, initial-scale=1.0' />
<title>{H(subtitle)}</title>
</head>
<body style='margin:0;padding:0;background:{EmailStyle.Page};{EmailStyle.Font}color:{EmailStyle.Text};'>
<table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='background:{EmailStyle.Page};padding:24px 0;'>
<tr><td align='center' style='padding:0 12px;'>
<table role='presentation' width='640' cellpadding='0' cellspacing='0' style='width:100%;max-width:640px;background:#ffffff;border-radius:8px;overflow:hidden;border:1px solid {EmailStyle.Line};'>
<tr>
<td style='background:{EmailStyle.Gold};padding:24px 32px;text-align:center;{EmailStyle.Font}'>
<div style='font-size:20px;font-weight:700;color:{EmailStyle.Dark};letter-spacing:.5px;'>City of Johannesburg</div>
<div style='margin-top:6px;font-size:13px;color:{EmailStyle.Dark};'>Valuation Services Department &mdash; {H(subtitle)}</div>
</td>
</tr>
<tr>
<td style='padding:28px 32px;font-size:14px;line-height:1.65;color:{EmailStyle.Text};{EmailStyle.Font}'>
{contentHtml}
</td>
</tr>
<tr>
<td style='background:{EmailStyle.Dark};padding:18px 32px;text-align:center;font-size:12px;line-height:1.6;color:#cccccc;{EmailStyle.Font}'>
City of Johannesburg &mdash; Valuation Services Department<br />
This is an automated email. Please do not reply directly.<br />
&copy; {DateTime.Now.Year} City of Johannesburg. All rights reserved.
</td>
</tr>
</table>
</td></tr>
</table>
</body>
</html>";
        }

        private static string Greeting(string? name) =>
            $"<p style='margin:0 0 14px;font-size:15px;'>Dear <strong>{H(string.IsNullOrWhiteSpace(name) ? "Valued Client" : name.Trim())}</strong>,</p>";

        /// <summary>A paragraph. The text must already be HTML-safe.</summary>
        private static string Para(string html) =>
            $"<p style='margin:0 0 14px;'>{html}</p>";

        /// <summary>Label / value box with the gold left border. Values must be HTML-safe.</summary>
        private static string Details(params (string Label, string ValueHtml)[] rows)
        {
            var sb = new StringBuilder();
            sb.Append($"<table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='margin:18px 0;background:{EmailStyle.Soft};border-left:4px solid {EmailStyle.Gold};border-collapse:collapse;'>");

            foreach (var (label, value) in rows)
            {
                sb.Append("<tr>");
                sb.Append($"<td style='padding:8px 14px;width:190px;font-weight:700;color:{EmailStyle.Muted};vertical-align:top;'>{H(label)}</td>");
                sb.Append($"<td style='padding:8px 14px;vertical-align:top;'>{value}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</table>");
            return sb.ToString();
        }

        /// <summary>Highlighted notice box. The content must be HTML-safe.</summary>
        private static string Notice(string html) =>
            $"<div style='margin:18px 0;padding:14px 16px;background:{EmailStyle.NoticeBg};border:1px solid {EmailStyle.Gold};border-radius:6px;color:{EmailStyle.NoticeText};'>{html}</div>";

        private static string Button(string label, string link) =>
            $"<div style='text-align:center;margin:26px 0;'><a href='{H(link)}' style='display:inline-block;background:{EmailStyle.Gold};color:{EmailStyle.Dark};text-decoration:none;padding:13px 30px;border-radius:6px;font-weight:700;font-size:15px;'>{H(label)}</a></div>";

        // ── Upload Evidence PIN + link (Objection, Appeal, Query, Review) ──
        // Clients use the EXTERNAL address (objections.joburg.org.za);
        // City staff on the City network can use the INTERNAL address.
        private string EvidenceUploadLink(string referenceNo) =>
            _links.Client(EvidencePath(referenceNo));

        private string EvidenceUploadStaffLink(string referenceNo) =>
            _links.Staff(EvidencePath(referenceNo));

        private static string EvidencePath(string referenceNo) =>
            "/evidence/verify?objectionNo=" + Uri.EscapeDataString(referenceNo?.Trim() ?? string.Empty);

        /// Small line under a button: the same page on the City network.
        private static string StaffLinkLine(string staffLink) =>
            SmallPrint($"City staff on the City network: {H(staffLink)}");

        private static string PinCell(string? pin) =>
            string.IsNullOrWhiteSpace(pin)
                ? "See the attached acknowledgement"
                : $"<strong style='font-size:16px;letter-spacing:1px;'>{H(pin.Trim())}</strong>";

        private static string EvidenceUploadSection(string referenceNo, string? pin, string link, string? staffLink = null)
        {
            var deadline = DateTime.Now.AddHours(48).ToString("dd MMMM yyyy 'at' HH:mm");

            return Notice(
                       "<strong>Upload supporting evidence within 48 hours</strong> " +
                       $"(by <strong>{H(deadline)}</strong>).<br/>" +
                       "Click the button below, then enter your reference number " +
                       $"<strong>{H(referenceNo)}</strong> and your <strong>Upload Evidence PIN</strong>" +
                       (string.IsNullOrWhiteSpace(pin) ? "" : $" <strong>{H(pin.Trim())}</strong>") +
                       ". You do not need to sign in. The PIN is only for uploading evidence for this submission &mdash; keep it safe and do not share it.")
                   + Button("Upload Evidence", link)
                   + SmallPrint($"If the button does not work, copy this link into your browser: {H(link)}")
                   + (string.IsNullOrWhiteSpace(staffLink) ? string.Empty : StaffLinkLine(staffLink));
        }

        private static string BulletList(IEnumerable<string> itemsHtml, bool numbered = false)
        {
            var tag = numbered ? "ol" : "ul";
            var items = string.Join(string.Empty, itemsHtml.Select(x => $"<li style='margin:0 0 6px;'>{x}</li>"));
            return $"<{tag} style='margin:0 0 14px;padding-left:22px;'>{items}</{tag}>";
        }

        private static string SmallPrint(string html) =>
            $"<p style='margin:18px 0 0;font-size:12.5px;color:#777777;'>{html}</p>";

        private static string Contact(string email, string phone) =>
            Para($"For enquiries please contact us:<br /><strong>Tel:</strong> {H(phone)}<br /><strong>Email:</strong> <a href='mailto:{H(email)}' style='color:#9a7400;'>{H(email)}</a>");

        private static string SignOff() =>
            "<p style='margin:22px 0 0;'>Regards,<br /><strong>City of Johannesburg</strong><br />Valuation Services Department</p>";

        private string FromAddress =>
            string.IsNullOrWhiteSpace(_cfg.FromAddress) ? _cfg.Username : _cfg.FromAddress;

        // ════════════════════════════════════════════════════════════
        //  GENERAL EMAILS
        // ════════════════════════════════════════════════════════════
        public async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
        {
            try
            {
                using var smtp = BuildClient();
                using var msg = new MailMessage
                {
                    From = new MailAddress(FromAddress, _cfg.FromName),
                    Subject = subject,
                    Body = htmlBody,
                    IsBodyHtml = true
                };
                msg.To.Add(toEmail);
                await smtp.SendMailAsync(msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Email}", toEmail);
                throw;
            }
        }

        public Task SendConfirmationEmailAsync(string toEmail, string displayName, string confirmationLink)
        {
            var subject = $"{_app.PortalSubtitle} – Please confirm your email address";
            var body = EmailTemplate(
                heading: "Confirm Your Email",
                body: Greeting(displayName)
                    + Para($"Thank you for registering on the <strong>{H(_app.PortalSubtitle)}</strong>.")
                    + Para("Please click the button below to confirm your email address and activate your account."),
                btnLabel: "Confirm Email Address",
                btnLink: confirmationLink,
                footer: "If you did not create an account, please ignore this email.");

            return SendEmailAsync(toEmail, subject, body);
        }

        public Task SendPasswordResetEmailAsync(string toEmail, string displayName, string resetLink)
        {
            var subject = $"{_app.PortalSubtitle} – Password Reset Request";
            var body = EmailTemplate(
                heading: "Reset Your Password",
                body: Greeting(displayName)
                    + Para($"We received a request to reset the password for your <strong>{H(_app.PortalSubtitle)}</strong> account.")
                    + Para("Click the button below to set a new password. This link expires in 24 hours."),
                btnLabel: "Reset My Password",
                btnLink: resetLink,
                footer: "If you did not request a password reset, please ignore this email and your password will remain unchanged.");

            return SendEmailAsync(toEmail, subject, body);
        }

        public Task SendAccountDetailsChangedAsync(
            string toEmail,
            string displayName,
            IReadOnlyCollection<string> changedFields,
            DateTime changedAt,
            string profileUrl)
        {
            var changedItems = changedFields.Count == 0
                ? new[] { "Account details" }
                : changedFields.Select(H).ToArray();

            var subject = "City of Johannesburg — Account Details Changed";
            var body = EmailTemplate(
                heading: "Account Details Changed",
                body: Greeting(displayName)
                    + Para("This email confirms that the following details on your Valuation Portal account were changed successfully:")
                    + BulletList(changedItems)
                    + Details(("Date and time", H(changedAt.ToString("dd MMMM yyyy HH:mm"))))
                    + Para("If you made this change, no further action is required.")
                    + Para("If you did not make this change, reset your password immediately and contact Valuation Services."),
                btnLabel: "Open Valuation Portal",
                btnLink: profileUrl,
                footer: "For your security, passwords are never included in account emails.");

            return SendEmailAsync(toEmail, subject, body);
        }

        // ── Private helpers ────────────────────────────────────────────────────
        private SmtpClient BuildClient()
        {
            var client = new SmtpClient
            {
                Host = _cfg.Host,
                Port = _cfg.Port,
                EnableSsl = _cfg.EnableSsl,
                UseDefaultCredentials = _cfg.UseDefaultCredentials
            };

            if (!_cfg.UseDefaultCredentials)
                client.Credentials = new NetworkCredential(_cfg.Username, _cfg.Password);

            return client;
        }

        /// <summary>Account emails (confirm, reset, details changed).</summary>
        private string EmailTemplate(string heading, string body, string btnLabel, string btnLink, string footer)
        {
            var support = string.IsNullOrWhiteSpace(_app.SupportEmail) && string.IsNullOrWhiteSpace(_app.SupportPhone)
                ? string.Empty
                : SmallPrint($"Support: {H(_app.SupportPhone)} &bull; {H(_app.SupportEmail)}");

            var content =
                $"<h2 style='margin:0 0 18px;font-size:20px;color:{EmailStyle.Dark};'>{H(heading)}</h2>"
                + body
                + Button(btnLabel, btnLink)
                + SmallPrint(H(footer))
                + support;

            return EmailShell(heading, content);
        }

        // ════════════════════════════════════════════════════════════
        //  OBJECTION / APPEAL ACKNOWLEDGEMENT
        //  Owner, Representative (or Third party) each get their own
        //  email, and a copy of each email is saved in the folder.
        // ════════════════════════════════════════════════════════════
        public async Task SendObjectionAcknowledgementAsync(
            string objectionRef,
            string rollSource,
            bool isAppeal,
            byte[] acknowledgementPdf,
            string folderPath,
            List<EmailAttachment>? extraAttachments = null,
            string? evidencePin = null)
        {
            try
            {
                if (acknowledgementPdf is null || acknowledgementPdf.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"The acknowledgement PDF is empty for {objectionRef}.");
                }

                if (isAppeal
                    && (extraAttachments is null
                        || !extraAttachments.Any(x =>
                            x.FileBytes is { Length: > 0 }
                            && x.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))))
                {
                    throw new InvalidOperationException(
                        $"The populated Appeal form PDF is missing for {objectionRef}.");
                }

                var recipients = await ResolveRecipientsAsync(objectionRef, rollSource, isAppeal);

                if (!recipients.Any())
                {
                    throw new InvalidOperationException(
                        $"No valid client email address was found for {objectionRef}.");
                }

                var rollTitle = RollTitles.GetValueOrDefault(rollSource, rollSource);
                var submissionType = isAppeal ? "Appeal" : "Objection";

                var propertyDescription = await ResolvePropertyDescriptionAsync(
                    objectionRef,
                    rollSource,
                    isAppeal);

                var cleanPropertyDescription = string.IsNullOrWhiteSpace(propertyDescription)
                    ? "Property"
                    : propertyDescription.Trim();

                // The subject is the same for every party.
                var subject =
                    $"City of Johannesburg — {submissionType} Acknowledgement: {objectionRef} — {cleanPropertyDescription}";

                var ackFileName = $"{submissionType}_Acknowledgement_{objectionRef}.pdf";
                var sentRecipients = new List<EmailRecipient>();

                foreach (var recipient in recipients)
                {
                    var htmlBody = BuildHtmlBody(
                        objectionRef,
                        rollTitle,
                        isAppeal,
                        recipient,
                        recipients,
                        evidencePin,
                        EvidenceUploadLink(objectionRef),
                        EvidenceUploadStaffLink(objectionRef));

                    // One failed address must not stop the other party.
                    try
                    {
                        await SendMailAsync(
                            recipient,
                            subject,
                            htmlBody,
                            acknowledgementPdf,
                            objectionRef,
                            isAppeal,
                            extraAttachments);

                        sentRecipients.Add(recipient);
                    }
                    catch (Exception sendEx)
                    {
                        _logger.LogError(
                            sendEx,
                            "[Email] Failed sending {SubmissionType} acknowledgement to {Type} {Addr} for {ObjRef}",
                            submissionType,
                            recipient.RecipientType,
                            recipient.Address,
                            objectionRef);
                        continue;
                    }

                    // Save this party's copy of the email in the folder.
                    try
                    {
                        await SaveEmailCopyAsync(
                            folderPath,
                            objectionRef,
                            cleanPropertyDescription,
                            subject,
                            htmlBody,
                            acknowledgementPdf,
                            ackFileName,
                            new[] { recipient },
                            extraAttachments,
                            copySuffix: CopySuffixFor(recipient));
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogError(
                            saveEx,
                            "[Email] Sent but could not save the {Type} email copy for {ObjRef}",
                            recipient.RecipientType,
                            objectionRef);
                    }
                }

                if (sentRecipients.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"The {submissionType} acknowledgement could not be sent to any recipient for {objectionRef}.");
                }

                _logger.LogInformation(
                    "[Email] Sent {Count} {SubmissionType} acknowledgement email(s) for {ObjRef}. AcknowledgementBytes={AcknowledgementBytes}, ExtraAttachments={ExtraAttachmentCount}",
                    sentRecipients.Count,
                    submissionType,
                    objectionRef,
                    acknowledgementPdf.Length,
                    extraAttachments?.Count ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[Email] Failed sending acknowledgement for {ObjRef}",
                    objectionRef);

                throw;
            }
        }

        private async Task<string> ResolvePropertyDescriptionAsync(
            string referenceNo,
            string rollSource,
            bool isAppeal)
        {
            try
            {
                var connKey = RollConnections.GetValueOrDefault(rollSource, "DefaultConnection");
                var connStr = _config.GetConnectionString(connKey);

                if (string.IsNullOrWhiteSpace(connStr))
                    return "";

                await using var conn = new SqlConnection(connStr);

                if (isAppeal)
                {
                    var sql = @"
                SELECT TOP 1
                    COALESCE(
                        NULLIF(LTRIM(RTRIM(a.A_Property_Desc)), ''),
                        NULLIF(LTRIM(RTRIM(o.Property_Desc)), '')
                    )
                FROM dbo.Obj_Property_Info_Appeal a
                LEFT JOIN dbo.Obj_Property_Info o
                       ON LTRIM(RTRIM(o.Objection_No)) = LTRIM(RTRIM(a.Obj_Ref))
                WHERE LTRIM(RTRIM(a.Appeal_No)) = LTRIM(RTRIM(@Ref));";

                    return await conn.QueryFirstOrDefaultAsync<string>(
                        sql,
                        new { Ref = referenceNo.Trim() }) ?? "";
                }
                else
                {
                    var sql = @"
                SELECT TOP 1 Property_Desc
                FROM dbo.Obj_Property_Info
                WHERE LTRIM(RTRIM(Objection_No)) = LTRIM(RTRIM(@Ref));";

                    return await conn.QueryFirstOrDefaultAsync<string>(
                        sql,
                        new { Ref = referenceNo.Trim() }) ?? "";
                }
            }
            catch
            {
                return "";
            }
        }

        // ════════════════════════════════════════════════════════════
        //  SECTION 78 QUERY / REVIEW ACKNOWLEDGEMENT
        // ════════════════════════════════════════════════════════════
        public async Task SendSection78AcknowledgementAsync(
            string queryRef,
            bool isReview,
            string propertyDescription,
            byte[] acknowledgementPdf,
            string folderPath,
            List<EmailAttachment>? extraAttachments = null,
            string? evidencePin = null)
        {
            try
            {
                var recipients = await ResolveSection78RecipientsAsync(queryRef);
                if (!recipients.Any())
                {
                    _logger.LogWarning(
                        "[S78 Email] No valid addresses found for {Ref} — skipping.",
                        queryRef);
                    return;
                }

                var actionWord = isReview ? "Review" : "Query";

                // The subject is the same for every party.
                var subject = $"City of Johannesburg — Section 78 {actionWord} Acknowledgement: {queryRef}";
                var ackFileName = $"S78_{actionWord}_Acknowledgement_{queryRef}.pdf";

                foreach (var recipient in recipients)
                {
                    var htmlBody = BuildSection78HtmlBody(
                        queryRef,
                        isReview,
                        recipient,
                        evidencePin,
                        EvidenceUploadLink(queryRef),
                        EvidenceUploadStaffLink(queryRef));

                    try
                    {
                        using var msg = new MailMessage();

                        msg.From = new MailAddress(FromAddress, _cfg.FromName);
                        msg.To.Add(new MailAddress(recipient.Address, recipient.Name));
                        msg.CC.Add(new MailAddress(FromAddress, "Valuation Services (Copy)"));

                        msg.Subject = subject;
                        msg.IsBodyHtml = true;
                        msg.Body = htmlBody;

                        msg.Attachments.Add(new Attachment(
                            new MemoryStream(acknowledgementPdf),
                            ackFileName,
                            MediaTypeNames.Application.Pdf));

                        AddExtraAttachments(msg, extraAttachments);

                        using var smtp = BuildClient();
                        await smtp.SendMailAsync(msg);

                        _logger.LogInformation(
                            "[S78 Email] Sent {Action} acknowledgement to {Type} {Addr} for {Ref}",
                            actionWord,
                            recipient.RecipientType,
                            recipient.Address,
                            queryRef);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "[S78 Email] Failed sending to {Type} {Addr} for {Ref}",
                            recipient.RecipientType,
                            recipient.Address,
                            queryRef);
                        continue;
                    }

                    try
                    {
                        await SaveEmailCopyAsync(
                            folderPath,
                            queryRef,
                            propertyDescription,
                            subject,
                            htmlBody,
                            acknowledgementPdf,
                            ackFileName,
                            new[] { recipient },
                            extraAttachments,
                            copySuffix: CopySuffixFor(recipient));
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogError(saveEx,
                            "[S78 Email] Sent but could not save the {Type} email copy for {Ref}",
                            recipient.RecipientType,
                            queryRef);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[S78 Email] Failed acknowledgement for {Ref}", queryRef);
            }
        }

        // ── Recipients from Obj_Section1 in the Objection_Query DB ─────────
        private async Task<List<EmailRecipient>> ResolveSection78RecipientsAsync(string queryRef)
        {
            var connStr = _config.GetConnectionString("QueryConnection")!;
            try
            {
                await using var conn = new SqlConnection(connStr);
                var section1 = await conn.QueryFirstOrDefaultAsync(
                    @"SELECT TOP 1
                        Owner_Name,          Owner_Email,
                        Objector_Name,       Objector_Email,
                        Representative_name, Rep_Email
                      FROM dbo.Obj_Section1
                      WHERE Objection_Ref_S1 = @Ref",
                    new { Ref = queryRef.Trim() });

                if (section1 is null) return new();

                // Objector_Status is the third party's capacity ("Tenant",
                // ...), not Owner/Representative, so the captured details
                // decide: a Rep email means the Rep lodged it and both the
                // Owner and the Representative get the acknowledgement.
                return BuildSubmissionRecipients(
                    submitterType: null,
                    ownerName: (string?)section1.Owner_Name?.ToString(),
                    ownerEmail: (string?)section1.Owner_Email?.ToString(),
                    objectorName: (string?)section1.Objector_Name?.ToString(),
                    objectorEmail: (string?)section1.Objector_Email?.ToString(),
                    repName: (string?)section1.Representative_name?.ToString(),
                    repEmail: (string?)section1.Rep_Email?.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[S78 Email] Error reading Obj_Section1 for {Ref}", queryRef);
                return new();
            }
        }

        // ── Section 78 email body ──────────────────────────────────────────
        private static string BuildSection78HtmlBody(
            string queryRef,
            bool isReview,
            EmailRecipient recipient,
            string? evidencePin,
            string evidenceLink,
            string? staffLink = null)
        {
            var actionWord = isReview ? "Review" : "Query";
            var lower = actionWord.ToLowerInvariant();
            var date = DateTime.Now.ToString("dd MMMM yyyy HH:mm");

            // Representative wording, otherwise the normal wording.
            var intro = IsRepresentative(recipient)
                ? Para($"Thank you for submitting this Section 78 {lower} on behalf of the owner.")
                  + Para($"You are receiving this acknowledgement as the authorised representative for the owner in respect of this {lower}.")
                : Para($"Your Section 78 <strong>{lower}</strong> has been successfully received by the City of Johannesburg Valuation Services Department.");

            var content =
                Greeting(recipient.Name)
                + intro
                + Details(
                    ("Reference Number", $"<strong style='font-size:16px;'>{H(queryRef)}</strong>"),
                    ("Submission Type", $"Section 78 {actionWord}"),
                    ("Date Submitted", H(date)),
                    ("Recipient", H(RecipientLabel(recipient))),
                    ("Status", $"{actionWord}-Lodging"),
                    ("Upload Evidence PIN", PinCell(evidencePin)))
                + EvidenceUploadSection(queryRef, evidencePin, evidenceLink, staffLink)
                + Notice($"<strong>Please keep your reference number</strong> ({H(queryRef)}) for all future correspondence regarding this {lower}. Your official acknowledgement document is attached to this email.")
                + Contact(ValuationEnquiriesEmail, Section78EnquiriesPhone);

            return EmailShell($"Section 78 {actionWord} Acknowledgement", content);
        }

        // ════════════════════════════════════════════════════════════
        //  EVIDENCE UPLOAD CONFIRMATION
        // ════════════════════════════════════════════════════════════
        public async Task SendEvidenceUploadConfirmationAsync(
            string referenceNo,
            string rollSource,
            bool isAppeal,
            IReadOnlyCollection<string> uploadedFileNames,
            DateTime uploadedAt,
            int remainingSlots)
        {
            var recipients = await ResolveRecipientsAsync(referenceNo, rollSource, isAppeal);
            if (!recipients.Any())
            {
                _logger.LogWarning(
                    "[Evidence Email] No recipient found for {ReferenceNo} on {RollSource}",
                    referenceNo, rollSource);
                throw new InvalidOperationException(
                    $"No client email address was found in the submission for '{referenceNo}'.");
            }

            var submissionType = isAppeal ? "Appeal" : "Objection";
            var subject = $"City of Johannesburg — Evidence Upload Confirmation: {referenceNo}";

            foreach (var recipient in recipients)
            {
                var body = BuildEvidenceUploadBody(
                    recipient.Name,
                    referenceNo,
                    submissionType,
                    uploadedFileNames,
                    uploadedAt,
                    remainingSlots);

                await SendEmailAsync(recipient.Address, subject, body);
            }
        }

        public async Task SendAttributeEvidenceUploadConfirmationAsync(
            string attributeNo,
            IReadOnlyCollection<string> uploadedFileNames,
            DateTime uploadedAt,
            int remainingSlots)
        {
            var delivery = await ResolveAttributeDeliveryAsync(attributeNo);
            if (delivery is null)
            {
                _logger.LogWarning(
                    "[Attribute Evidence Email] No recipient found for {AttributeNo}",
                    attributeNo);
                throw new InvalidOperationException(
                    $"No client email address was found in the attribute submission for '{attributeNo}'.");
            }

            var subject = $"City of Johannesburg — Evidence Upload Confirmation: {attributeNo}";
            var body = BuildEvidenceUploadBody(
                delivery.To.Name,
                attributeNo,
                "Attribute",
                uploadedFileNames,
                uploadedAt,
                remainingSlots);

            body = ApplyAttributeTestModeBanner(body, delivery);
            await SendAttributeMessageAsync(delivery, subject, body, attachments: null);
        }

        // ════════════════════════════════════════════════════════════
        //  ATTRIBUTES — delivery rules (unchanged)
        // ════════════════════════════════════════════════════════════
        private sealed record AttributeDelivery(
            EmailRecipient To,
            IReadOnlyList<EmailRecipient> Cc);

        private async Task<AttributeDelivery?> ResolveAttributeDeliveryAsync(
            string attributeNo,
            string? fallbackEmail = null,
            string? fallbackName = null)
        {
            attributeNo = attributeNo?.Trim() ?? string.Empty;

            var info = await _attributesDb.AttrPropertyInfo
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Attr_No == attributeNo);

            if (info is null)
            {
                if (!string.IsNullOrWhiteSpace(fallbackEmail))
                {
                    return new AttributeDelivery(
                        new EmailRecipient(
                            string.IsNullOrWhiteSpace(fallbackName) ? "Valued Client" : fallbackName.Trim(),
                            fallbackEmail.Trim(),
                            "Client"),
                        Array.Empty<EmailRecipient>());
                }

                return null;
            }

            var ownerRecipients = new List<EmailRecipient>();

            if (info.Attr_PropertyDetailsId.HasValue)
            {
                var contacts = await _attributesDb.AttrContactInfo
                    .AsNoTracking()
                    .Where(x => x.PropertyDetailsId == info.Attr_PropertyDetailsId.Value)
                    .OrderBy(x => x.Id)
                    .ToListAsync();

                foreach (var contact in contacts)
                {
                    var name = string.Join(" ", new[] { contact.FirstNames, contact.LastName }
                        .Where(x => !string.IsNullOrWhiteSpace(x)));

                    if (string.IsNullOrWhiteSpace(name))
                        name = contact.CompanyName;

                    if (string.IsNullOrWhiteSpace(name))
                        name = "Owner";

                    TryAdd(
                        ownerRecipients,
                        name,
                        contact.Email,
                        contact.IsCompany ? "Company / Owner" : "Owner");
                }
            }

            var representativeSubmission =
                info.Objector_Type?.Equals(
                    "Representative",
                    StringComparison.OrdinalIgnoreCase) == true;

            if (representativeSubmission)
            {
                var representative = await _attributesDb.AttrRepresentatives
                    .AsNoTracking()
                    .Where(x => x.Attr_No == attributeNo)
                    .OrderByDescending(x => x.Id)
                    .FirstOrDefaultAsync();

                EmailRecipient? repRecipient = null;

                if (representative is not null &&
                    !string.IsNullOrWhiteSpace(representative.Rep_Email))
                {
                    repRecipient = new EmailRecipient(
                        string.IsNullOrWhiteSpace(representative.Representative_Name)
                            ? "Representative"
                            : representative.Representative_Name.Trim(),
                        representative.Rep_Email.Trim(),
                        "Representative");
                }
                else if (!string.IsNullOrWhiteSpace(fallbackEmail))
                {
                    repRecipient = new EmailRecipient(
                        string.IsNullOrWhiteSpace(fallbackName)
                            ? "Representative"
                            : fallbackName.Trim(),
                        fallbackEmail.Trim(),
                        "Representative");
                }
                else if (!string.IsNullOrWhiteSpace(info.SubmittedByEmail))
                {
                    repRecipient = new EmailRecipient(
                        string.IsNullOrWhiteSpace(info.SubmittedByName)
                            ? "Representative"
                            : info.SubmittedByName.Trim(),
                        info.SubmittedByEmail.Trim(),
                        "Representative");
                }

                if (repRecipient is null)
                    return null;

                var cc = ownerRecipients
                    .Where(x => !x.Address.Equals(
                        repRecipient.Address,
                        StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First())
                    .ToList();

                return new AttributeDelivery(repRecipient, cc);
            }

            var owner = ownerRecipients.FirstOrDefault();

            if (owner is null && !string.IsNullOrWhiteSpace(fallbackEmail))
            {
                owner = new EmailRecipient(
                    string.IsNullOrWhiteSpace(fallbackName) ? "Valued Client" : fallbackName.Trim(),
                    fallbackEmail.Trim(),
                    "Owner");
            }
            else if (owner is null && !string.IsNullOrWhiteSpace(info.SubmittedByEmail))
            {
                owner = new EmailRecipient(
                    string.IsNullOrWhiteSpace(info.SubmittedByName) ? "Valued Client" : info.SubmittedByName.Trim(),
                    info.SubmittedByEmail.Trim(),
                    "Owner");
            }

            return owner is null
                ? null
                : new AttributeDelivery(owner, Array.Empty<EmailRecipient>());
        }

        private string ApplyAttributeTestModeBanner(
            string htmlBody,
            AttributeDelivery delivery)
        {
            if (!_cfg.TestMode)
                return htmlBody;

            var intendedCc = delivery.Cc.Count == 0
                ? "None"
                : string.Join(", ", delivery.Cc.Select(x => x.Address));

            var banner =
                $"<div style='margin:0 0 18px;padding:12px 14px;border:2px solid #b45309;background:#fff7ed;color:#7c2d12;{EmailStyle.Font}font-size:13px;line-height:1.5;'>"
                + "<strong>TEST MODE — no client email was sent.</strong><br/>"
                + $"Intended To: {H(delivery.To.Address)}<br/>"
                + $"Intended CC: {H(intendedCc)}<br/>"
                + $"Actual UAT recipient: {H(_cfg.TestRecipient)}"
                + "</div>";

            // Put the banner at the top of the white content area so the
            // email layout stays intact.
            const string marker = "<td style='padding:28px 32px;";
            var index = htmlBody.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                return banner + htmlBody;

            var close = htmlBody.IndexOf('>', index);
            return close < 0
                ? banner + htmlBody
                : htmlBody.Insert(close + 1, banner);
        }

        private async Task SendAttributeMessageAsync(
            AttributeDelivery delivery,
            string subject,
            string htmlBody,
            List<EmailAttachment>? attachments)
        {
            using var smtp = BuildClient();
            using var msg = new MailMessage
            {
                From = new MailAddress(FromAddress, _cfg.FromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };

            if (_cfg.TestMode)
            {
                if (string.IsNullOrWhiteSpace(_cfg.TestRecipient))
                    throw new InvalidOperationException(
                        "Email:TestRecipient must be configured while Email:TestMode is enabled.");

                msg.To.Add(_cfg.TestRecipient.Trim());
            }
            else
            {
                msg.To.Add(new MailAddress(delivery.To.Address, delivery.To.Name));

                foreach (var recipient in delivery.Cc
                    .Where(x => !string.IsNullOrWhiteSpace(x.Address))
                    .Where(x => !x.Address.Equals(
                        delivery.To.Address,
                        StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => x.Address.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First()))
                {
                    msg.CC.Add(new MailAddress(recipient.Address, recipient.Name));
                }
            }

            if (attachments is not null)
            {
                foreach (var item in attachments)
                {
                    if (item?.FileBytes == null || item.FileBytes.Length == 0 ||
                        string.IsNullOrWhiteSpace(item.FileName))
                        continue;

                    msg.Attachments.Add(new Attachment(
                        new MemoryStream(item.FileBytes),
                        item.FileName,
                        string.IsNullOrWhiteSpace(item.ContentType)
                            ? "application/octet-stream"
                            : item.ContentType));
                }
            }

            await smtp.SendMailAsync(msg);

            _logger.LogInformation(
                "[Attributes Email] {Mode} message sent for intended To={To}; CC={Cc}",
                _cfg.TestMode ? "TEST MODE" : "LIVE",
                delivery.To.Address,
                delivery.Cc.Count == 0
                    ? "None"
                    : string.Join(",", delivery.Cc.Select(x => x.Address)));
        }

        private static string BuildEvidenceUploadBody(
            string? recipientName,
            string referenceNo,
            string submissionType,
            IReadOnlyCollection<string> uploadedFileNames,
            DateTime uploadedAt,
            int remainingSlots)
        {
            var files = uploadedFileNames.Any()
                ? uploadedFileNames.Select(H).ToArray()
                : new[] { "No filename was returned." };

            var safeRemaining = Math.Max(0, remainingSlots);
            var slotMessage = safeRemaining == 1
                ? "1 evidence file slot remains"
                : $"{safeRemaining} evidence file slots remain";

            var content =
                Greeting(recipientName)
                + Para($"Your additional evidence for the {H(submissionType)} submission was uploaded successfully.")
                + Details(
                    ("Reference number", $"<strong>{H(referenceNo)}</strong>"),
                    ("Date uploaded", H(uploadedAt.ToString("dd MMMM yyyy HH:mm"))),
                    ("Files uploaded", uploadedFileNames.Count.ToString()),
                    ("Available file slots", $"{safeRemaining} of 10"))
                + Para("<strong>Uploaded filenames</strong>")
                + BulletList(files)
                + Notice($"<strong>{H(slotMessage)}.</strong> These remaining slots may only be used while the 48-hour evidence-upload window for this submission is still open.")
                + Para("Please keep this email for your records.");

            return EmailShell("Evidence Upload Confirmation", content);
        }

        // ════════════════════════════════════════════════════════════
        //  RESOLVE RECIPIENTS from Obj_Section1 + Obj_Property_Info
        //
        //    Owner          → Owner
        //    Representative → Owner AND Representative
        //    Third_Party    → Third party (Objector)
        //
        //  Objector_Type comes from the browser (sessionStorage) and is
        //  sometimes empty, so a captured Rep email also means the Rep
        //  lodged it. For appeals the Appeal_Type is used first.
        // ════════════════════════════════════════════════════════════
        private async Task<List<EmailRecipient>> ResolveRecipientsAsync(
            string objectionRef,
            string rollSource,
            bool isAppeal = false)
        {
            var connKey = RollConnections.GetValueOrDefault(rollSource, "DefaultConnection");
            var connStr = _config.GetConnectionString(connKey)!;

            try
            {
                await using var conn = new SqlConnection(connStr);

                var sql = isAppeal
                    ? @"
                SELECT TOP 1
                    s1.Owner_Name,
                    s1.Owner_Email,
                    s1.Objector_Name,
                    s1.Objector_Email,
                    s1.Representative_name,
                    s1.Rep_Email,
                    COALESCE(
                        NULLIF(LTRIM(RTRIM(opia.Appeal_Type)), ''),
                        NULLIF(LTRIM(RTRIM(opi.Objector_Type)), '')
                    ) AS Objector_Type
                FROM dbo.Obj_Section1 s1
                LEFT JOIN dbo.Obj_Property_Info_Appeal opia
                       ON opia.Appeal_No = @Ref
                LEFT JOIN dbo.Obj_Property_Info opi
                       ON opi.Objection_No = opia.Obj_Ref
                WHERE s1.Objection_Ref_S1 = @Ref"
                    : @"
                SELECT TOP 1
                    s1.Owner_Name,
                    s1.Owner_Email,
                    s1.Objector_Name,
                    s1.Objector_Email,
                    s1.Representative_name,
                    s1.Rep_Email,
                    opi.Objector_Type
                FROM dbo.Obj_Section1 s1
                LEFT JOIN dbo.Obj_Property_Info opi
                       ON opi.Objection_No = @Ref
                WHERE s1.Objection_Ref_S1 = @Ref";

                var row = await conn.QueryFirstOrDefaultAsync(
                    sql,
                    new { Ref = objectionRef.Trim() });

                if (row is null)
                {
                    _logger.LogWarning(
                        "[Email] Obj_Section1 not found for {ObjRef}",
                        objectionRef);

                    return new List<EmailRecipient>();
                }

                string objectorType = ((string?)row.Objector_Type?.ToString())?.Trim() ?? string.Empty;

                _logger.LogInformation(
                    "[Email] {ObjRef} Objector_Type = '{Type}'",
                    objectionRef,
                    objectorType);

                var list = BuildSubmissionRecipients(
                    submitterType: objectorType,
                    ownerName: (string?)row.Owner_Name?.ToString(),
                    ownerEmail: (string?)row.Owner_Email?.ToString(),
                    objectorName: (string?)row.Objector_Name?.ToString(),
                    objectorEmail: (string?)row.Objector_Email?.ToString(),
                    repName: (string?)row.Representative_name?.ToString(),
                    repEmail: (string?)row.Rep_Email?.ToString());

                if (!list.Any())
                {
                    _logger.LogWarning(
                        "[Email] No usable email address found for {ObjRef}. Objector_Type was '{Type}'.",
                        objectionRef,
                        objectorType);
                }

                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[Email] Error resolving recipients for {ObjRef}",
                    objectionRef);

                return new List<EmailRecipient>();
            }
        }

        /// <summary>Who receives a submission acknowledgement.</summary>
        private static List<EmailRecipient> BuildSubmissionRecipients(
            string? submitterType,
            string? ownerName,
            string? ownerEmail,
            string? objectorName,
            string? objectorEmail,
            string? repName,
            string? repEmail)
        {
            var type = (submitterType ?? string.Empty).Trim().Replace(' ', '_');

            var isThirdParty = type.Equals("Third_Party", StringComparison.OrdinalIgnoreCase);
            var isRepresentative =
                type.Equals("Representative", StringComparison.OrdinalIgnoreCase) ||
                (!isThirdParty && IsEmailAddress(repEmail));

            var list = new List<EmailRecipient>();

            if (isThirdParty)
            {
                TryAdd(list, objectorName, objectorEmail, "Third Party");
            }
            else if (isRepresentative)
            {
                // Representative first: if the Rep typed the same address in
                // the Owner field, that person gets the Representative email.
                TryAdd(list, repName, repEmail, "Representative");
                TryAdd(list, ownerName, ownerEmail, "Owner");
            }
            else
            {
                TryAdd(list, ownerName, ownerEmail, "Owner");
            }

            // Nothing usable yet (e.g. a third party whose type was not saved).
            if (list.Count == 0)
                TryAdd(list, objectorName, objectorEmail, "Third Party");

            return list;
        }

        private static bool IsEmailAddress(string? email) =>
            !string.IsNullOrWhiteSpace(email) && email.Contains('@');

        private static bool IsRepresentative(EmailRecipient recipient) =>
            string.Equals(recipient.RecipientType, "Representative", StringComparison.OrdinalIgnoreCase);

        private static string RecipientLabel(EmailRecipient recipient) =>
            string.IsNullOrWhiteSpace(recipient.RecipientType) ? "Client" : recipient.RecipientType;

        // Saved .eml name. The Owner / Third party copy keeps the original
        // name; the Representative copy gets its own file.
        private static string CopySuffixFor(EmailRecipient recipient) =>
            IsRepresentative(recipient)
                ? "Acknowledgement_Representative"
                : "Acknowledgement";

        private static void TryAdd(
            List<EmailRecipient> list,
            string? name,
            string? email,
            string recipientType = "Client")
        {
            if (!string.IsNullOrWhiteSpace(email) && email.Contains('@'))
            {
                var cleanEmail = email.Trim();

                // Prevent duplicate sends if two fields hold the same address.
                if (list.Any(x => x.Address.Equals(cleanEmail, StringComparison.OrdinalIgnoreCase)))
                    return;

                list.Add(new EmailRecipient(
                    string.IsNullOrWhiteSpace(name) ? cleanEmail : name.Trim(),
                    cleanEmail,
                    recipientType));
            }
        }

        // ════════════════════════════════════════════════════════════
        //  OBJECTION / APPEAL EMAIL BODY
        // ════════════════════════════════════════════════════════════
        private static string BuildHtmlBody(
            string objectionRef,
            string rollTitle,
            bool isAppeal,
            EmailRecipient recipient,
            List<EmailRecipient> recipients,
            string? evidencePin,
            string evidenceLink,
            string? staffLink = null)
        {
            var actionWord = isAppeal ? "appeal" : "objection";
            var ActionWord = isAppeal ? "Appeal" : "Objection";
            var ActorWord = isAppeal ? "Appellant" : "Objector";
            var now = DateTime.Now.ToString("dd MMMM yyyy HH:mm");

            var recipientType = string.IsNullOrWhiteSpace(recipient.RecipientType)
                ? (recipients.Count > 1 ? "Representative" : "Client")
                : recipient.RecipientType;

            // Representative wording, otherwise the normal wording.
            var intro = IsRepresentative(recipient)
                ? Para($"Thank you for submitting this property {actionWord} on behalf of the owner.")
                  + Para($"You are receiving this acknowledgement as the authorised representative for the owner in respect of this {actionWord}.")
                : Para($"Thank you for submitting your property {actionWord} through the City of Johannesburg Valuation Portal. This email confirms that your {actionWord} has been successfully received and recorded.");

            var content =
                Greeting(recipient.Name)
                + intro
                + Details(
                    ($"{ActionWord} Reference", $"<strong style='font-size:16px;'>{H(objectionRef)}</strong>"),
                    ("Valuation Roll", H(rollTitle)),
                    ("Submission Date", H(now)),
                    ($"{ActorWord} Type", H(recipientType)),
                    ("Upload Evidence PIN", PinCell(evidencePin)))
                + EvidenceUploadSection(objectionRef, evidencePin, evidenceLink, staffLink)
                + Para($"Please find the official {ActionWord} acknowledgement and the populated {ActionWord} form attached to this email. Keep the attached documents for your records as proof of submission.")
                + Contact(ValuationEnquiriesEmail, ObjectionEnquiriesPhone);

            return EmailShell($"{ActionWord} Acknowledgement", content);
        }

        // ════════════════════════════════════════════════════════════
        //  SEND ONE OBJECTION / APPEAL EMAIL
        // ════════════════════════════════════════════════════════════
        private async Task SendMailAsync(
            EmailRecipient recipient,
            string subject,
            string htmlBody,
            byte[] pdfAttachment,
            string objectionRef,
            bool isAppeal,
            List<EmailAttachment>? extraAttachments = null)
        {
            using var msg = new MailMessage();

            msg.From = new MailAddress(FromAddress, _cfg.FromName);
            msg.To.Add(new MailAddress(recipient.Address, recipient.Name));
            msg.Subject = subject;
            msg.IsBodyHtml = true;
            msg.Body = htmlBody;

            var pdfName = $"{(isAppeal ? "Appeal" : "Objection")}_Acknowledgement_{objectionRef}.pdf";

            msg.Attachments.Add(new Attachment(
                new MemoryStream(pdfAttachment),
                pdfName,
                MediaTypeNames.Application.Pdf));

            AddExtraAttachments(msg, extraAttachments);

            using var client = BuildClient();
            await client.SendMailAsync(msg);

            _logger.LogInformation(
                "[Email] Sent {Action} acknowledgement to {Type} {Addr} for {Ref}",
                isAppeal ? "Appeal" : "Objection",
                recipient.RecipientType,
                recipient.Address,
                objectionRef);
        }

        private static void AddExtraAttachments(
            MailMessage msg,
            List<EmailAttachment>? extraAttachments)
        {
            if (extraAttachments == null || !extraAttachments.Any())
                return;

            foreach (var item in extraAttachments)
            {
                if (item == null)
                    continue;

                if (item.FileBytes == null || item.FileBytes.Length == 0)
                    continue;

                if (string.IsNullOrWhiteSpace(item.FileName))
                    continue;

                msg.Attachments.Add(new Attachment(
                    new MemoryStream(item.FileBytes),
                    item.FileName,
                    string.IsNullOrWhiteSpace(item.ContentType)
                        ? MediaTypeNames.Application.Pdf
                        : item.ContentType));
            }
        }

        // ════════════════════════════════════════════════════════════
        //  BUILD EMAIL RECORD PDF (QuestPDF)
        // ════════════════════════════════════════════════════════════
        private byte[] BuildEmailRecordPdf(
            string objectionRef,
            string rollTitle,
            bool isAppeal,
            List<EmailRecipient> recipients)
        {
            var actionWord = isAppeal ? "Appeal" : "Objection";

            return Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.5f, Unit.Centimetre);
                    page.DefaultTextStyle(t => t.FontFamily("Arial").FontSize(9));

                    page.Content().Column(col =>
                    {
                        col.Item().Background("#e6b000").Padding(16).Row(row =>
                        {
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text("CITY OF JOHANNESBURG")
                                    .FontSize(14).Bold().FontColor("#1a1a1a");
                                c.Item().Text("Valuation Services — Email Notification Record")
                                    .FontSize(9).FontColor(Colors.Black);
                            });
                        });

                        col.Item().Height(12);

                        col.Item().AlignCenter()
                            .Text($"{actionWord.ToUpper()} ACKNOWLEDGEMENT EMAIL RECORD")
                            .FontSize(11).Bold();

                        col.Item().Height(6);
                        col.Item().BorderBottom(1).BorderColor("#cccccc");
                        col.Item().Height(10);

                        col.Item().Background("#f7f7f7").BorderLeft(4).BorderColor("#e6b000")
                            .Padding(10).Column(meta =>
                            {
                                void Row(string label, string value)
                                {
                                    meta.Item().Row(r =>
                                    {
                                        r.ConstantItem(150).Text(label).Bold().FontSize(9);
                                        r.RelativeItem().Text(value).FontSize(9);
                                    });
                                    meta.Item().Height(3);
                                }

                                Row($"{actionWord} Reference:", objectionRef);
                                Row("Valuation Roll:", rollTitle);
                                Row("Sent Date/Time:", DateTime.Now.ToString("dd MMMM yyyy HH:mm"));
                                Row("Sent From:", FromAddress);
                            });

                        col.Item().Height(12);

                        col.Item().Text("EMAIL RECIPIENTS").Bold().FontSize(9);
                        col.Item().Height(6);

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(30);
                                c.RelativeColumn(2);
                                c.RelativeColumn(3);
                            });

                            static IContainer TH(IContainer c) =>
                                c.Background("#1a1a1a").Padding(6);

                            table.Cell().Element(TH).Text("#").FontColor(Colors.White).Bold().FontSize(8);
                            table.Cell().Element(TH).Text("Name").FontColor(Colors.White).Bold().FontSize(8);
                            table.Cell().Element(TH).Text("Email Address").FontColor(Colors.White).Bold().FontSize(8);

                            bool alt = false;
                            for (int i = 0; i < recipients.Count; i++)
                            {
                                var bg = alt ? Colors.Grey.Lighten5 : Colors.White;
                                alt = !alt;
                                IContainer TD(IContainer c) =>
                                    c.Background(bg).BorderBottom(0.5f)
                                     .BorderColor("#eeeeee").Padding(5);

                                table.Cell().Element(TD).Text((i + 1).ToString()).FontSize(8);
                                table.Cell().Element(TD).Text(recipients[i].Name).FontSize(8);
                                table.Cell().Element(TD).Text(recipients[i].Address).FontSize(8);
                            }
                        });

                        col.Item().Height(12);

                        col.Item().Background("#fffbeb").Border(1)
                            .BorderColor("#f59e0b").Padding(8)
                            .DefaultTextStyle(x => x.FontSize(8))
                            .Text(t =>
                            {
                                t.Span("Note: ").Bold();
                                t.Span("The official acknowledgement PDF was attached to each " +
                                       "email listed above. This document serves as a record " +
                                       "that the notification was dispatched.");
                            });

                        col.Item().Height(14);

                        col.Item().BorderTop(1).BorderColor("#cccccc").PaddingTop(6)
                            .AlignCenter()
                            .Text($"Generated by Genesis V2 — City of Johannesburg " +
                                  $"Valuation Services — {DateTime.Now:dd MMMM yyyy HH:mm}")
                            .FontSize(7).FontColor("#888888");
                    });
                });
            }).GeneratePdf();
        }

        // ════════════════════════════════════════════════════════════
        //  SAVE .EML COPY TO FOLDER
        // ════════════════════════════════════════════════════════════
        private async Task SaveEmailCopyAsync(
            string folderPath,
            string reference,
            string propertyDescription,
            string subject,
            string htmlBody,
            byte[] acknowledgementPdf,
            string acknowledgementFileName,
            IReadOnlyCollection<EmailRecipient> recipients,
            List<EmailAttachment>? extraAttachments = null,
            string copySuffix = "Acknowledgement",
            IReadOnlyCollection<EmailRecipient>? ccRecipients = null)
        {
            try
            {
                Directory.CreateDirectory(folderPath);

                var safeReference = SanitizeFilePart(reference);
                var safePropertyDescription = SanitizeFilePart(propertyDescription);
                var safeCopySuffix = SanitizeFilePart(copySuffix);
                var fileName =
                    $"Email_{safeReference}_{safePropertyDescription}_{safeCopySuffix}.eml";
                var fullPath = Path.Combine(folderPath, fileName);

                using var msg = new MailMessage
                {
                    From = new MailAddress(FromAddress, _cfg.FromName),
                    Subject = subject,
                    IsBodyHtml = true,
                    Body = htmlBody
                };

                foreach (var recipient in recipients
                    .Where(x => !string.IsNullOrWhiteSpace(x.Address))
                    .GroupBy(x => x.Address.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First()))
                {
                    msg.To.Add(new MailAddress(recipient.Address, recipient.Name));
                }

                if (ccRecipients is not null)
                {
                    foreach (var recipient in ccRecipients
                        .Where(x => !string.IsNullOrWhiteSpace(x.Address))
                        .Where(x => !msg.To.Cast<MailAddress>().Any(to =>
                            to.Address.Equals(x.Address.Trim(), StringComparison.OrdinalIgnoreCase)))
                        .GroupBy(x => x.Address.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First()))
                    {
                        msg.CC.Add(new MailAddress(recipient.Address, recipient.Name));
                    }
                }

                if (msg.To.Count == 0)
                    throw new InvalidOperationException(
                        $"No recipient is available for the email copy of {reference}.");

                msg.Attachments.Add(new Attachment(
                    new MemoryStream(acknowledgementPdf),
                    BuildPdfFileName(
                        acknowledgementFileName,
                        $"{reference}_Acknowledgement.pdf"),
                    MediaTypeNames.Application.Pdf));

                AddExtraAttachments(msg, extraAttachments);

                var tmpDir = Path.Combine(
                    Path.GetTempPath(),
                    "eml_" + Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(tmpDir);

                try
                {
                    using (var pickup = new SmtpClient
                    {
                        DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                        PickupDirectoryLocation = tmpDir
                    })
                    {
                        pickup.Send(msg);
                    }

                    var generated = Directory.GetFiles(tmpDir).SingleOrDefault();
                    if (generated is null)
                        throw new InvalidOperationException(
                            $"The .eml copy was not generated for {reference}.");

                    File.Move(generated, fullPath, overwrite: true);

                    _logger.LogInformation(
                        "[Email] EML copy saved as {FileName} for {Reference}. Attachments={AttachmentCount}",
                        fileName,
                        reference,
                        msg.Attachments.Count);
                }
                finally
                {
                    if (Directory.Exists(tmpDir))
                        Directory.Delete(tmpDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[Email] Failed saving EML copy for {Ref}",
                    reference);

                throw;
            }
        }

        private static string SanitizeFilePart(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "NA";

            var invalid = Path.GetInvalidFileNameChars();

            var cleaned = new string(value.Trim()
                .Select(c => invalid.Contains(c) ? '_' : c)
                .ToArray());

            cleaned = cleaned
                .Replace("@", "_at_")
                .Replace(".", "_")
                .Replace(" ", "_")
                .Replace("/", "_")
                .Replace("\\", "_")
                .Replace(":", "_");

            while (cleaned.Contains("__"))
                cleaned = cleaned.Replace("__", "_");

            if (string.IsNullOrWhiteSpace(cleaned))
                return "NA";

            cleaned = cleaned.Trim('_');
            return cleaned.Length > 90 ? cleaned[..90] : cleaned;
        }

        public async Task SendEmailWithAttachmentsAsync(
            string toEmail,
            string subject,
            string body,
            List<EmailAttachment> attachments,
            bool isHtml = true)
        {
            try
            {
                using var smtp = BuildClient();

                using var msg = new MailMessage
                {
                    From = new MailAddress(FromAddress, _cfg.FromName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = isHtml
                };

                msg.To.Add(toEmail);

                if (attachments != null && attachments.Any())
                {
                    foreach (var item in attachments)
                    {
                        if (item == null)
                            continue;

                        if (item.FileBytes == null || item.FileBytes.Length == 0)
                            continue;

                        if (string.IsNullOrWhiteSpace(item.FileName))
                            continue;

                        msg.Attachments.Add(new Attachment(
                            new MemoryStream(item.FileBytes),
                            item.FileName,
                            string.IsNullOrWhiteSpace(item.ContentType)
                                ? MediaTypeNames.Application.Pdf
                                : item.ContentType));
                    }
                }

                await smtp.SendMailAsync(msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to send email with attachments to {Email}", toEmail);

                throw;
            }
        }

        public async Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string htmlBody,
            byte[] attachmentBytes,
            string attachmentFileName)
        {
            var attachments = new List<EmailAttachment>
            {
                new EmailAttachment
                {
                    FileName = attachmentFileName,
                    FileBytes = attachmentBytes,
                    ContentType = MediaTypeNames.Application.Pdf
                }
            };

            await SendEmailWithAttachmentsAsync(
                toEmail,
                subject,
                htmlBody,
                attachments,
                true);
        }

        // ════════════════════════════════════════════════════════════
        //  ATTRIBUTE ACKNOWLEDGEMENTS
        // ════════════════════════════════════════════════════════════
        public async Task SendAttributeAcknowledgementAsync(
            string recipientEmail,
            string clientName,
            string attributeNumber,
            string propertyDescription,
            string evidencePin,
            DateTime evidenceDeadline,
            byte[] acknowledgementPdf,
            byte[] submittedFormPdf,
            string acknowledgementFileName,
            string submittedFormFileName,
            string folderPath)
        {
            if (acknowledgementPdf == null || acknowledgementPdf.Length == 0)
            {
                throw new ArgumentException(
                    "Acknowledgement PDF is required.",
                    nameof(acknowledgementPdf));
            }

            if (submittedFormPdf == null || submittedFormPdf.Length == 0)
            {
                throw new ArgumentException(
                    "Submitted attribute form PDF is required.",
                    nameof(submittedFormPdf));
            }

            clientName = string.IsNullOrWhiteSpace(clientName)
                ? "Valued Client"
                : clientName.Trim();

            attributeNumber = string.IsNullOrWhiteSpace(attributeNumber)
                ? "Attribute Submission"
                : attributeNumber.Trim();

            propertyDescription = string.IsNullOrWhiteSpace(propertyDescription)
                ? "Property"
                : propertyDescription.Trim();

            acknowledgementFileName = BuildPdfFileName(
                acknowledgementFileName,
                $"{attributeNumber}_Acknowledgement.pdf");

            submittedFormFileName = BuildPdfFileName(
                submittedFormFileName,
                $"{attributeNumber}_Attribute_Form.pdf");

            var subject =
                $"City of Johannesburg — Attribute Submission Acknowledgement: {attributeNumber}";

            var delivery = await ResolveAttributeDeliveryAsync(
                attributeNumber,
                recipientEmail,
                clientName)
                ?? throw new InvalidOperationException(
                    $"No Attribute email recipient could be resolved for '{attributeNumber}'.");

            var body = BuildAttributeAcknowledgementBody(
                delivery.To.Name,
                attributeNumber,
                propertyDescription,
                evidencePin,
                evidenceDeadline);

            body = ApplyAttributeTestModeBanner(body, delivery);

            var submittedFormAttachment = new EmailAttachment
            {
                FileName = submittedFormFileName,
                FileBytes = submittedFormPdf,
                ContentType = MediaTypeNames.Application.Pdf
            };

            var attachments = new List<EmailAttachment>
            {
                new()
                {
                    FileName = acknowledgementFileName,
                    FileBytes = acknowledgementPdf,
                    ContentType = MediaTypeNames.Application.Pdf
                },
                submittedFormAttachment
            };

            await SendAttributeMessageAsync(delivery, subject, body, attachments);

            await SaveEmailCopyAsync(
                folderPath,
                attributeNumber,
                propertyDescription,
                subject,
                body,
                acknowledgementPdf,
                acknowledgementFileName,
                new[] { delivery.To },
                new List<EmailAttachment> { submittedFormAttachment },
                ccRecipients: delivery.Cc);

            _logger.LogInformation(
                "[Attributes Email] Acknowledgement sent to {Email} for {AttributeNumber}",
                delivery.To.Address,
                attributeNumber);
        }

        public async Task SendAttributeCorrectionAcknowledgementAsync(
            string recipientEmail,
            string clientName,
            string attributeNumber,
            string propertyDescription,
            string correctionComment,
            IReadOnlyCollection<string> correctedSections,
            byte[] acknowledgementPdf,
            string acknowledgementFileName,
            string folderPath)
        {
            if (acknowledgementPdf == null || acknowledgementPdf.Length == 0)
                throw new ArgumentException("Correction acknowledgement PDF is required.", nameof(acknowledgementPdf));

            clientName = string.IsNullOrWhiteSpace(clientName) ? "Valued Client" : clientName.Trim();
            attributeNumber = string.IsNullOrWhiteSpace(attributeNumber) ? "Attribute Submission" : attributeNumber.Trim();
            propertyDescription = string.IsNullOrWhiteSpace(propertyDescription) ? "Property" : propertyDescription.Trim();
            correctionComment = string.IsNullOrWhiteSpace(correctionComment) ? "Corrections submitted." : correctionComment.Trim();

            acknowledgementFileName = BuildPdfFileName(
                acknowledgementFileName,
                $"{attributeNumber}_Correction_Acknowledgement.pdf");

            var subject =
                $"City of Johannesburg — Attribute Correction Acknowledgement: {attributeNumber}";

            var delivery = await ResolveAttributeDeliveryAsync(
                attributeNumber,
                recipientEmail,
                clientName)
                ?? throw new InvalidOperationException(
                    $"No Attribute email recipient could be resolved for '{attributeNumber}'.");

            var body = BuildAttributeCorrectionAcknowledgementBody(
                delivery.To.Name,
                attributeNumber,
                propertyDescription,
                correctionComment,
                correctedSections);

            body = ApplyAttributeTestModeBanner(body, delivery);

            var attachments = new List<EmailAttachment>
            {
                new()
                {
                    FileName = acknowledgementFileName,
                    FileBytes = acknowledgementPdf,
                    ContentType = MediaTypeNames.Application.Pdf
                }
            };

            await SendAttributeMessageAsync(delivery, subject, body, attachments);

            await SaveEmailCopyAsync(
                folderPath,
                attributeNumber,
                propertyDescription,
                subject,
                body,
                acknowledgementPdf,
                acknowledgementFileName,
                new[] { delivery.To },
                extraAttachments: null,
                copySuffix: "Correction_Acknowledgement",
                ccRecipients: delivery.Cc);

            _logger.LogInformation(
                "[Attributes Email] Correction acknowledgement sent to {Email} for {AttributeNumber}",
                delivery.To.Address,
                attributeNumber);
        }

        private static string BuildPdfFileName(
            string? fileName,
            string fallbackFileName)
        {
            var value = string.IsNullOrWhiteSpace(fileName)
                ? fallbackFileName
                : Path.GetFileName(fileName.Trim());

            if (string.IsNullOrWhiteSpace(value))
                value = fallbackFileName;

            var invalidCharacters = Path.GetInvalidFileNameChars();

            value = new string(
                value.Select(character =>
                        invalidCharacters.Contains(character)
                            ? '_'
                            : character)
                    .ToArray());

            if (!value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                value += ".pdf";

            return value;
        }

        private static string BuildAttributeCorrectionAcknowledgementBody(
            string clientName,
            string attributeNumber,
            string propertyDescription,
            string correctionComment,
            IReadOnlyCollection<string> correctedSections)
        {
            var sections = (correctedSections ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(H)
                .ToList();

            if (sections.Count == 0)
                sections.Add("Corrections submitted as requested by the Valuer.");

            var content =
                Greeting(clientName)
                + Para("Your corrected property attribute information has been received successfully.")
                + Details(
                    ("Attribute Reference", $"<strong>{H(attributeNumber)}</strong>"),
                    ("Property", H(propertyDescription)))
                + Para("<strong>Corrections confirmed</strong>")
                + BulletList(sections)
                + Para($"<strong>Your correction note:</strong><br />{H(correctionComment)}")
                + Notice("This correction acknowledgement confirms receipt of the revised information requested by the Valuer. There is no new 48-hour evidence period and no evidence PIN for this correction submission.")
                + Para("The corrected information will continue through the valuation review process.")
                + SignOff();

            return EmailShell("Attribute Corrections", content);
        }

        private static string BuildAttributeAcknowledgementBody(
            string clientName,
            string attributeNumber,
            string propertyDescription,
            string evidencePin,
            DateTime evidenceDeadline)
        {
            var content =
                Greeting(clientName)
                + Para("Your property attribute submission has been received successfully by the City of Johannesburg Valuation Services Department.")
                + Details(
                    ("Attribute Number", $"<strong style='font-size:16px;'>{H(attributeNumber)}</strong>"),
                    ("Property Description", H(propertyDescription)),
                    ("Upload Evidence PIN", $"<strong>{H(evidencePin)}</strong>"),
                    ("Evidence Deadline", H(evidenceDeadline.ToString("dd MMMM yyyy HH:mm"))))
                + Notice("<strong>Important:</strong> You may upload additional supporting evidence within 48 hours of the original submission, subject to the remaining evidence-file limit.")
                + Para("The following documents are attached:")
                + BulletList(new[] { "Attribute submission acknowledgement", "Submitted attribute form" }, numbered: true)
                + Para("Please keep your attribute reference number and Upload Evidence PIN safe for future use.")
                + SignOff();

            return EmailShell("Attribute Submission", content);
        }

        // ════════════════════════════════════════════════════════════
        //  SECTION 51 NOTICE (Third-Party objection → property owner)
        // ════════════════════════════════════════════════════════════
        public async Task SendSection51NoticeAsync(Section51NoticeEmail notice)
        {
            if (notice is null)
                throw new ArgumentNullException(nameof(notice));

            if (string.IsNullOrWhiteSpace(notice.ToAddress))
                throw new InvalidOperationException(
                    $"No recipient for the Section 51 notice of {notice.ObjectionNo}.");

            if (notice.PdfBytes is null || notice.PdfBytes.Length == 0)
                throw new InvalidOperationException(
                    $"The Section 51 notice PDF is empty for {notice.ObjectionNo}.");

            var propertyDescription = string.IsNullOrWhiteSpace(notice.PropertyDescription)
                ? "Property"
                : notice.PropertyDescription.Trim();

            var subject =
                $"City of Johannesburg — Section 51 Notice: {notice.ObjectionNo} — {propertyDescription}";

            var htmlBody = BuildSection51NoticeBody(notice, propertyDescription);

            MailMessage BuildMessage()
            {
                var msg = new MailMessage
                {
                    From = new MailAddress(FromAddress, _cfg.FromName),
                    Subject = subject,
                    Body = htmlBody,
                    IsBodyHtml = true
                };

                msg.To.Add(new MailAddress(notice.ToAddress.Trim()));

                foreach (var cc in notice.CcAddresses
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Select(x => x.Trim())
                             .Where(x => !x.Equals(notice.ToAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    msg.CC.Add(new MailAddress(cc));
                }

                foreach (var bcc in notice.BccAddresses
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Select(x => x.Trim())
                             .Where(x => !x.Equals(notice.ToAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    msg.Bcc.Add(new MailAddress(bcc));
                }

                msg.Attachments.Add(new Attachment(
                    new MemoryStream(notice.PdfBytes),
                    notice.PdfFileName,
                    MediaTypeNames.Application.Pdf));

                return msg;
            }

            // 1. Send
            using (var msg = BuildMessage())
            using (var client = BuildClient())
            {
                await client.SendMailAsync(msg);
            }

            _logger.LogInformation(
                "[S51 Email] Section 51 notice sent for {ObjectionNo} to {To}{Test}. Cc={Cc}, Bcc={Bcc}",
                notice.ObjectionNo,
                notice.ToAddress,
                notice.IsTest
                    ? $" (TEST — owner email {(notice.OwnerHasEmail ? notice.OwnerEmail : "none")})"
                    : notice.OwnerHasEmail ? string.Empty : " (no owner email — tracking mailbox only)",
                notice.CcAddresses.Count == 0 ? "(none)" : string.Join(", ", notice.CcAddresses),
                notice.BccAddresses.Count == 0 ? "(none)" : string.Join(", ", notice.BccAddresses));

            // 2. Save the .eml copy (a failure here must not undo the send)
            try
            {
                Directory.CreateDirectory(notice.EmlFolderPath);

                var tmpDir = Path.Combine(Path.GetTempPath(), "eml_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmpDir);

                try
                {
                    using (var msg = BuildMessage())
                    using (var pickup = new SmtpClient
                    {
                        DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                        PickupDirectoryLocation = tmpDir
                    })
                    {
                        pickup.Send(msg);
                    }

                    var generated = Directory.GetFiles(tmpDir).SingleOrDefault()
                        ?? throw new InvalidOperationException("The .eml copy was not generated.");

                    var emlPath = Path.Combine(notice.EmlFolderPath, notice.EmlFileName);
                    File.Move(generated, emlPath, overwrite: true);

                    // Same copy in the Objection Pack (…\{ObjectionNo}\Section51).
                    foreach (var folder in notice.EmlExtraFolders
                                 .Where(f => !string.IsNullOrWhiteSpace(f))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Directory.CreateDirectory(folder);
                            File.Copy(emlPath, Path.Combine(folder, notice.EmlFileName), overwrite: true);
                        }
                        catch (Exception copyEx)
                        {
                            _logger.LogError(copyEx,
                                "[S51 Email] .eml copy to {Folder} failed for {ObjectionNo}",
                                folder, notice.ObjectionNo);
                        }
                    }

                    _logger.LogInformation(
                        "[S51 Email] EML copy saved as {FileName} for {ObjectionNo}",
                        notice.EmlFileName,
                        notice.ObjectionNo);
                }
                finally
                {
                    if (Directory.Exists(tmpDir))
                        Directory.Delete(tmpDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[S51 Email] Notice sent but the .eml copy could not be saved for {ObjectionNo}",
                    notice.ObjectionNo);
            }
        }

        private static string BuildSection51NoticeBody(
            Section51NoticeEmail notice,
            string propertyDescription)
        {
            var content = new StringBuilder();

            if (notice.IsTest)
            {
                content.Append(Notice(
                    $"<strong>TEST MODE</strong> — this Section 51 notice is intended for the property owner at " +
                    $"<strong>{H(!notice.OwnerHasEmail || string.IsNullOrWhiteSpace(notice.OwnerEmail) ? "(no email)" : notice.OwnerEmail)}</strong>."));
            }

            if (!notice.OwnerHasEmail)
            {
                // Internal copy for the tracking mailbox only.
                content.Append(Notice(
                    "<strong>NO OWNER E-MAIL ON THE ROLL</strong> — this copy was sent to the Section 51 tracking " +
                    "mailbox only. Please print the attached notice and post it to the owner's postal address."));
            }

            content.Append(Greeting("Property Owner"));

            content.Append(Para(
                $"<strong>NOTICE OF {H(notice.RollName.ToUpperInvariant())}</strong><br /><strong>Section 51 Notice</strong>"));

            content.Append(Details(
                ("Property Description", H(propertyDescription)),
                ("Objection Number", H(notice.ObjectionNo)),
                ("Valuation Key", H(string.IsNullOrWhiteSpace(notice.ValuationKey) ? "—" : notice.ValuationKey))));

            content.Append(Para(
                "You are hereby notified that the Municipal Valuer has received an objection from an individual " +
                "to your property as reflected in the valuation roll."));

            // Direct link: opens the Section 51 upload page with the objection
            // number filled in. No portal account or sign-in is needed.
            var portal = string.IsNullOrWhiteSpace(notice.PortalUrl)
                ? V2_Genesis.Services.PortalLinks.DefaultExternal
                : notice.PortalUrl.Trim().TrimEnd('/');
            var uploadLink = portal + "/section51/verify?objectionNo=" +
                Uri.EscapeDataString(notice.ObjectionNo?.Trim() ?? string.Empty);

            content.Append(Para(
                "Submissions by the owner in response to the objection must be submitted online to the Municipal Valuer " +
                $"no later than <strong>{H(notice.SubmissionsCloseDate.ToString("dd MMMM yyyy"))}</strong>."));

            content.Append(Details(
                ("Objection Number", $"<strong>{H(notice.ObjectionNo)}</strong>"),
                ("Section 51 PIN", string.IsNullOrWhiteSpace(notice.Section51Pin)
                    ? "See the attached notice"
                    : $"<strong style='font-size:16px;letter-spacing:1px;'>{H(notice.Section51Pin.Trim())}</strong>"),
                ("Submissions close", H(notice.SubmissionsCloseDate.ToString("dd MMMM yyyy")))));

            content.Append(Para(
                "<strong>The quickest way:</strong> click the button below. The upload page opens with the objection " +
                "number already filled in — enter your Section 51 PIN and upload your documents. " +
                "You do <strong>not</strong> need a portal account or to sign in."));

            content.Append(Button("Upload Section 51 Submission", uploadLink));

            content.Append(Para("<strong>Or go to the portal yourself:</strong>"));
            content.Append(BulletList(new[]
            {
                $"<strong>No portal account:</strong> open <a href='{H(portal)}' style='color:#9a7400;'>{H(portal)}</a>, " +
                "click <strong>Add Evidence</strong> in the top menu and choose <strong>Section 51 Owner Evidence</strong>.",
                "<strong>You have a portal account:</strong> sign in, click <strong>Add Evidence</strong> in the top menu " +
                "and choose <strong>Section 51 Evidence</strong>.",
                $"Enter the objection number <strong>{H(notice.ObjectionNo)}</strong> and your Section 51 PIN, " +
                "then upload your documents: up to 10 files (PDF, JPG or PNG, 3 MB each). Section 51 documents can be submitted once."
            }, numbered: false));

            content.Append(SmallPrint($"If the button does not work, copy this link into your browser: {H(uploadLink)}"));
            if (!string.IsNullOrWhiteSpace(notice.StaffPortalUrl))
                content.Append(StaffLinkLine(notice.StaffPortalUrl.Trim().TrimEnd('/') + "/section51/verify?objectionNo=" +
                    Uri.EscapeDataString(notice.ObjectionNo?.Trim() ?? string.Empty)));

            content.Append(Para(
                "You will be notified of the Municipal Valuer’s decision in terms of Section 53 of the " +
                "Municipal Property Rates Act 6 of 2004. If you are dissatisfied with the decision, " +
                "you will have the right to lodge an appeal."));

            content.Append(Para("The Section 51 notice is attached to this email as a PDF."));

            content.Append(Contact(ValuationEnquiriesEmail, ObjectionEnquiriesPhone));
            content.Append(SignOff());

            return EmailShell("Section 51 Notice", content.ToString());
        }
    }
}