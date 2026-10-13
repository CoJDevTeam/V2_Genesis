using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using V2_Genesis.Data;
using V2_Genesis.Models.Results.Admin;
using V2_Genesis.Services.Admin;
using V2_Genesis.Services.Interfaces;

namespace V2_Genesis.Services.Implementations;

/// <summary>
/// Builds the read-only Account Information tab. The login account, Admin
/// capturer and form parties remain separate so an Admin account is never
/// mistaken for the client represented in Obj_Section1/Attributes contacts.
/// </summary>
public sealed class AdminAccountInformationService
    : IAdminAccountInformationService
{
    private readonly ApplicationDbContext _applicationDb;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminAccountInformationService> _logger;
    private readonly int _commandTimeoutSeconds;

    public AdminAccountInformationService(
        ApplicationDbContext applicationDb,
        IConfiguration configuration,
        ILogger<AdminAccountInformationService> logger)
    {
        _applicationDb = applicationDb;
        _configuration = configuration;
        _logger = logger;
        _commandTimeoutSeconds = Math.Clamp(
            configuration.GetValue("AdminSearch:CommandTimeoutSeconds", 8),
            3,
            30);
    }

    public async Task<AdminAccountInformation> GetAsync(
        AdminEnquiryFoundation foundation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(foundation);

        var reference = foundation.Reference;
        var accountTask = LoadSubmittingAccountAsync(reference, cancellationToken);
        var partyTask = reference.ReferenceType.Equals(
                "Attributes",
                StringComparison.OrdinalIgnoreCase)
            ? LoadAttributePartiesAsync(reference.ReferenceNumber, cancellationToken)
            : LoadSectionOnePartiesAsync(reference, cancellationToken);

        await Task.WhenAll(accountTask, partyTask);

        return new AdminAccountInformation
        {
            SubmittingAccount = await accountTask,
            Parties = (await partyTask)
                .Where(HasUsefulPartyData)
                .GroupBy(
                    x => string.Join('|', x.Role, x.FullName, x.CompanyName, x.Email),
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList()
        };
    }

    private async Task<AdminSubmittingAccount> LoadSubmittingAccountAsync(
        AdminResolvedReference reference,
        CancellationToken cancellationToken)
    {
        var model = new AdminSubmittingAccount
        {
            UserId = Clean(reference.UserId),
            SubmittedByName = Clean(reference.SubmittedByName),
            SubmittedByEmail = Clean(reference.SubmittedByEmail),
            SubmittedByPhone = Clean(reference.SubmittedByPhone),
            SubmissionSource = Clean(reference.SubmissionSource),
            CapturerSapNumber = Clean(reference.CapturerSapNumber),
            SubmittedAt = reference.SubmittedAt,
            IsAdminCaptured = reference.IsAdminCaptured
        };

        if (string.IsNullOrWhiteSpace(reference.UserId))
            return model;

        var user = await _applicationDb.Users
            .AsNoTracking()
            .Where(x => x.Id == reference.UserId)
            .Select(x => new
            {
                x.Id,
                x.FirstName,
                x.LastName,
                x.CompanyName,
                x.CompanyRegistration,
                x.Email,
                x.PhoneNumber,
                x.SAPNumber,
                x.CreationDate,
                x.EmailConfirmed
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
            return model;

        var isCompany = !string.IsNullOrWhiteSpace(user.CompanyName)
            || !string.IsNullOrWhiteSpace(user.CompanyRegistration);

        model.Resolved = true;
        model.UserId = user.Id;
        model.DisplayName = isCompany
            ? Clean(user.CompanyName)
            : Join(user.FirstName, user.LastName);
        model.Email = Clean(user.Email);
        model.PhoneNumber = Clean(user.PhoneNumber);
        model.AccountType = isCompany ? "Company" : "Individual";
        model.CompanyRegistration = Clean(user.CompanyRegistration);
        model.AccountCreatedAt = user.CreationDate;
        model.EmailConfirmed = user.EmailConfirmed;
        model.IsAdministrativeAccount = IsAdministrativeAccount(model.Email);
        // The shared admin account's SAP number is just whoever created it;
        // the real capturer is CapturerSapNumber.
        model.SapNumber = model.IsAdministrativeAccount ? string.Empty : Clean(user.SAPNumber);

        if (string.IsNullOrWhiteSpace(model.SubmittedByName))
            model.SubmittedByName = model.DisplayName;
        if (string.IsNullOrWhiteSpace(model.SubmittedByEmail))
            model.SubmittedByEmail = model.Email;
        if (string.IsNullOrWhiteSpace(model.SubmittedByPhone))
            model.SubmittedByPhone = model.PhoneNumber;

        return model;
    }

    private async Task<List<AdminSubmissionParty>> LoadSectionOnePartiesAsync(
        AdminResolvedReference reference,
        CancellationToken cancellationToken)
    {
        var connectionKey = ResolveConnectionKey(reference.RollSource);
        if (string.IsNullOrWhiteSpace(connectionKey))
            return new();

        var candidates = BuildSectionOneReferences(reference);
        if (candidates.Count == 0)
            return new();

        const string sql = """
            SELECT TOP (1)
                Owner_Name AS OwnerName,
                Owner_Company AS OwnerCompany,
                Owner_Identity AS OwnerIdentity,
                Owner_Email AS OwnerEmail,
                Owner_Cell_Phone AS OwnerCell,
                Owner_Home_Phone AS OwnerHome,
                Owner_Work_Phone AS OwnerWork,
                Owner_Address_1 AS OwnerAddress1,
                Owner_Address_2 AS OwnerAddress2,
                Owner_Address_3 AS OwnerAddress3,
                Owner_Address_4 AS OwnerAddress4,
                Owner_Postal_1 AS OwnerPostal1,
                Owner_Postal_2 AS OwnerPostal2,
                Owner_Postal_3 AS OwnerPostal3,
                Owner_Postal_4 AS OwnerPostal4,

                Objector_Name AS ObjectorName,
                Objector_Company AS ObjectorCompany,
                Objector_Identity AS ObjectorIdentity,
                Objector_Email AS ObjectorEmail,
                Objector_Cell AS ObjectorCell,
                Objector_Home AS ObjectorHome,
                Objector_Work AS ObjectorWork,
                Objector_Postal_1 AS ObjectorPostal1,
                Objector_Postal_2 AS ObjectorPostal2,
                Objector_Postal_3 AS ObjectorPostal3,
                Objector_Postal_4 AS ObjectorPostal4,

                Representative_name AS RepresentativeName,
                Rep_Email AS RepresentativeEmail,
                Rep_Cell_Phone AS RepresentativeCell,
                Rep_Home_Phone AS RepresentativeHome,
                Rep_Work_Phone AS RepresentativeWork,
                Rep_Postal_1 AS RepresentativePostal1,
                Rep_Postal_2 AS RepresentativePostal2,
                Rep_Postal_3 AS RepresentativePostal3,
                Rep_Postal_4 AS RepresentativePostal4
            FROM dbo.Obj_Section1
            WHERE Objection_Ref_S1 IN @References
            ORDER BY CASE WHEN Objection_Ref_S1 = @PrimaryReference THEN 0 ELSE 1 END;
            """;

        try
        {
            await using var connection = CreateConnection(connectionKey);
            var row = await connection.QuerySingleOrDefaultAsync<SectionOneRow>(
                Command(
                    sql,
                    new
                    {
                        References = candidates,
                        PrimaryReference = candidates[0]
                    },
                    cancellationToken));

            if (row is null)
                return new();

            return BuildSectionOneParties(row);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[AdminAccountInformation] Obj_Section1 lookup failed. Ref={Reference}, Source={RollSource}",
                reference.ReferenceNumber,
                reference.RollSource);
            return new();
        }
    }

    private async Task<List<AdminSubmissionParty>> LoadAttributePartiesAsync(
        string referenceNumber,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(NULLIF(c.ContactType, ''), 'Owner') AS Role,
                c.FirstNames,
                c.LastName,
                c.CompanyName,
                c.CompanyRegistrationNumber,
                c.IDNumber AS IdentityNumber,
                c.Email,
                c.CellNo AS CellPhone,
                c.HomePhoneNo AS HomePhone,
                c.WorkPhoneNo AS WorkPhone,
                c.PhysicalAddress,
                c.PostalAddress
            FROM dbo.Attr_Property_Info p
            INNER JOIN dbo.Attr_ContactInfo c
                ON c.PropertyDetailsId = p.Attr_PropertyDetailsId
            WHERE p.Attr_No = @Reference
            ORDER BY c.Id;

            SELECT
                'Representative' AS Role,
                r.Representative_Name AS FullName,
                r.Rep_Email AS Email,
                r.Rep_Cell_Phone AS CellPhone,
                r.Rep_Home_Phone AS HomePhone,
                r.Rep_Work_Phone AS WorkPhone,
                CONCAT_WS(', ',
                    NULLIF(r.Rep_Postal_1, ''),
                    NULLIF(r.Rep_Postal_2, ''),
                    NULLIF(r.Rep_Postal_3, ''),
                    NULLIF(r.Rep_Postal_4, ''),
                    NULLIF(r.Rep_Postal_5, '')) AS PostalAddress
            FROM dbo.Attr_Representatives r
            WHERE r.Attr_No = @Reference
            ORDER BY r.Id;
            """;

        try
        {
            await using var connection = CreateConnection("AttributesConnection");
            using var results = await connection.QueryMultipleAsync(
                Command(sql, new { Reference = referenceNumber }, cancellationToken));

            var contacts = (await results.ReadAsync<AttributeContactRow>()).ToList();
            var representatives =
                (await results.ReadAsync<AdminSubmissionParty>()).ToList();

            var parties = contacts.Select(x => new AdminSubmissionParty
            {
                Role = NormaliseRole(x.Role),
                FullName = Join(x.FirstNames, x.LastName),
                CompanyName = Clean(x.CompanyName),
                CompanyRegistrationNumber = Clean(x.CompanyRegistrationNumber),
                IdentityNumber = Clean(x.IdentityNumber),
                Email = Clean(x.Email),
                CellPhone = Clean(x.CellPhone),
                HomePhone = Clean(x.HomePhone),
                WorkPhone = Clean(x.WorkPhone),
                PhysicalAddress = Clean(x.PhysicalAddress),
                PostalAddress = Clean(x.PostalAddress)
            }).ToList();

            parties.AddRange(representatives.Select(CleanParty));
            return parties;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[AdminAccountInformation] Attributes party lookup failed. Ref={Reference}",
                referenceNumber);
            return new();
        }
    }

    private static List<AdminSubmissionParty> BuildSectionOneParties(
        SectionOneRow row) =>
        new()
        {
            new AdminSubmissionParty
            {
                Role = "Owner",
                FullName = Clean(row.OwnerName),
                CompanyName = Clean(row.OwnerCompany),
                IdentityNumber = Clean(row.OwnerIdentity),
                Email = Clean(row.OwnerEmail),
                CellPhone = Clean(row.OwnerCell),
                HomePhone = Clean(row.OwnerHome),
                WorkPhone = Clean(row.OwnerWork),
                PhysicalAddress = JoinAddress(
                    row.OwnerAddress1,
                    row.OwnerAddress2,
                    row.OwnerAddress3,
                    row.OwnerAddress4),
                PostalAddress = JoinAddress(
                    row.OwnerPostal1,
                    row.OwnerPostal2,
                    row.OwnerPostal3,
                    row.OwnerPostal4)
            },
            new AdminSubmissionParty
            {
                Role = "Third Party",
                FullName = Clean(row.ObjectorName),
                CompanyName = Clean(row.ObjectorCompany),
                IdentityNumber = Clean(row.ObjectorIdentity),
                Email = Clean(row.ObjectorEmail),
                CellPhone = Clean(row.ObjectorCell),
                HomePhone = Clean(row.ObjectorHome),
                WorkPhone = Clean(row.ObjectorWork),
                PostalAddress = JoinAddress(
                    row.ObjectorPostal1,
                    row.ObjectorPostal2,
                    row.ObjectorPostal3,
                    row.ObjectorPostal4)
            },
            new AdminSubmissionParty
            {
                Role = "Representative",
                FullName = Clean(row.RepresentativeName),
                Email = Clean(row.RepresentativeEmail),
                CellPhone = Clean(row.RepresentativeCell),
                HomePhone = Clean(row.RepresentativeHome),
                WorkPhone = Clean(row.RepresentativeWork),
                PostalAddress = JoinAddress(
                    row.RepresentativePostal1,
                    row.RepresentativePostal2,
                    row.RepresentativePostal3,
                    row.RepresentativePostal4)
            }
        };

    private string? ResolveConnectionKey(string rollSource)
    {
        if (rollSource.Equals(
                "Objection_Query",
                StringComparison.OrdinalIgnoreCase))
        {
            return "QueryConnection";
        }

        return AdminRollRegistry.Configs.TryGetValue(rollSource, out var config)
            ? config.ConnectionKey
            : null;
    }

    private SqlConnection CreateConnection(string connectionKey)
    {
        var connectionString = _configuration.GetConnectionString(connectionKey)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionKey}' was not found.");
        return new SqlConnection(connectionString);
    }

    private CommandDefinition Command(
        string sql,
        object parameters,
        CancellationToken cancellationToken) =>
        new(
            sql,
            parameters,
            commandTimeout: _commandTimeoutSeconds,
            cancellationToken: cancellationToken);

    private static List<string> BuildSectionOneReferences(
        AdminResolvedReference reference)
    {
        var values = new List<string>();
        Add(reference.ReferenceNumber);
        Add(reference.RelatedReferenceNumber);

        if (reference.ReferenceNumber.EndsWith(
                "-R",
                StringComparison.OrdinalIgnoreCase))
        {
            Add(reference.ReferenceNumber[..^2]);
        }

        return values;

        void Add(string? value)
        {
            var clean = Clean(value);
            if (!string.IsNullOrWhiteSpace(clean)
                && !values.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(clean);
            }
        }
    }

    private static bool HasUsefulPartyData(AdminSubmissionParty party) =>
        !string.IsNullOrWhiteSpace(party.FullName)
        || !string.IsNullOrWhiteSpace(party.CompanyName)
        || !string.IsNullOrWhiteSpace(party.IdentityNumber)
        || party.HasContactDetails
        || !string.IsNullOrWhiteSpace(party.PhysicalAddress)
        || !string.IsNullOrWhiteSpace(party.PostalAddress);

    private static AdminSubmissionParty CleanParty(AdminSubmissionParty party)
    {
        party.Role = NormaliseRole(party.Role);
        party.FullName = Clean(party.FullName);
        party.CompanyName = Clean(party.CompanyName);
        party.IdentityNumber = Clean(party.IdentityNumber);
        party.CompanyRegistrationNumber = Clean(party.CompanyRegistrationNumber);
        party.Email = Clean(party.Email);
        party.CellPhone = Clean(party.CellPhone);
        party.HomePhone = Clean(party.HomePhone);
        party.WorkPhone = Clean(party.WorkPhone);
        party.PhysicalAddress = Clean(party.PhysicalAddress);
        party.PostalAddress = Clean(party.PostalAddress);
        return party;
    }

    private static string NormaliseRole(string? value)
    {
        var role = Clean(value);
        if (role.Contains("representative", StringComparison.OrdinalIgnoreCase))
            return "Representative";
        if (role.Contains("third", StringComparison.OrdinalIgnoreCase))
            return "Third Party";
        if (role.Contains("company", StringComparison.OrdinalIgnoreCase))
            return "Owner / Company";
        return string.IsNullOrWhiteSpace(role) ? "Owner" : role;
    }

    private static string Join(params string?[] values) =>
        string.Join(
            " ",
            values.Select(Clean).Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string JoinAddress(params string?[] values) =>
        string.Join(
            ", ",
            values.Select(Clean).Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsAdministrativeAccount(string? email)
    {
        var value = Clean(email);

        return value.Equals(
                   "AdministrationEnquiries@Joburg.org.za",
                   StringComparison.OrdinalIgnoreCase)
               || (value.StartsWith(
                       "val.admin",
                       StringComparison.OrdinalIgnoreCase)
                   && value.EndsWith(
                       "@joburg.org.za",
                       StringComparison.OrdinalIgnoreCase));
    }

    private sealed class AttributeContactRow
    {
        public string? Role { get; set; }
        public string? FirstNames { get; set; }
        public string? LastName { get; set; }
        public string? CompanyName { get; set; }
        public string? CompanyRegistrationNumber { get; set; }
        public string? IdentityNumber { get; set; }
        public string? Email { get; set; }
        public string? CellPhone { get; set; }
        public string? HomePhone { get; set; }
        public string? WorkPhone { get; set; }
        public string? PhysicalAddress { get; set; }
        public string? PostalAddress { get; set; }
    }

    private sealed class SectionOneRow
    {
        public string? OwnerName { get; set; }
        public string? OwnerCompany { get; set; }
        public string? OwnerIdentity { get; set; }
        public string? OwnerEmail { get; set; }
        public string? OwnerCell { get; set; }
        public string? OwnerHome { get; set; }
        public string? OwnerWork { get; set; }
        public string? OwnerAddress1 { get; set; }
        public string? OwnerAddress2 { get; set; }
        public string? OwnerAddress3 { get; set; }
        public string? OwnerAddress4 { get; set; }
        public string? OwnerPostal1 { get; set; }
        public string? OwnerPostal2 { get; set; }
        public string? OwnerPostal3 { get; set; }
        public string? OwnerPostal4 { get; set; }
        public string? ObjectorName { get; set; }
        public string? ObjectorCompany { get; set; }
        public string? ObjectorIdentity { get; set; }
        public string? ObjectorEmail { get; set; }
        public string? ObjectorCell { get; set; }
        public string? ObjectorHome { get; set; }
        public string? ObjectorWork { get; set; }
        public string? ObjectorPostal1 { get; set; }
        public string? ObjectorPostal2 { get; set; }
        public string? ObjectorPostal3 { get; set; }
        public string? ObjectorPostal4 { get; set; }
        public string? RepresentativeName { get; set; }
        public string? RepresentativeEmail { get; set; }
        public string? RepresentativeCell { get; set; }
        public string? RepresentativeHome { get; set; }
        public string? RepresentativeWork { get; set; }
        public string? RepresentativePostal1 { get; set; }
        public string? RepresentativePostal2 { get; set; }
        public string? RepresentativePostal3 { get; set; }
        public string? RepresentativePostal4 { get; set; }
    }
}
