namespace GenesisV2.Services.PropertySearch;

public record RollSearchConfig(
    
      string TownshipSp,
      string SchemeSp,
    string SpTown,
    string SpStand,
    string SpStandAddress,
    string SpAddress,
    string SpScheme,
    string SpUnit,
    string SpSchemeUnit,
    string SpStandScheme,
    string SpAddressScheme,
    string DetailSp,
    string ConnectionKey,
    bool IsQuery = false,    // ← default false, only Query overrides
    // LEASE (Y/N) flag (read only): unit type FL or SL = lease.
    // Lease = the roll table, LisLease = the LIS table of the LIS search.
    // Null = no lease flag for that roll.
    LeaseLookup? Lease = null,
    LeaseLookup? LisLease = null
);

public static class RollSearchRegistry
{
    public static readonly IReadOnlyDictionary<string, RollSearchConfig> Configs =
        new Dictionary<string, RollSearchConfig>
        {
            ["Objection"] = new(
                  TownshipSp: "Objection.dbo.propertyDetailsTown",
                  SchemeSp: "Objection.dbo.propertyDetailsScheme",
                SpTown: "Objection.dbo.SearchTown",
                SpStand: "Objection.dbo.SearchTownStandNumber",
                SpStandAddress: "Objection.dbo.SearchTownStandNumberAddress",
                SpAddress: "Objection.dbo.SearchTownAddress",
                SpScheme: "Objection.dbo.SearchTownScheme",
                SpUnit: "Objection.dbo.SearchTownUnit",
                SpSchemeUnit: "Objection.dbo.SearchTownSchemeUnit",
                SpStandScheme: "Objection.dbo.SearchTownErfScheme",
                SpAddressScheme: "Objection.dbo.SearchTownAddressScheme",
                DetailSp: "IndexObjection",
                ConnectionKey: "DefaultConnection"
            ),

            ["Objection_Supp1"] = new(
                  TownshipSp: "propertyDetailsTown",
                  SchemeSp: "propertyDetailsScheme",
                SpTown: "SearchTown_Sup1",
                SpStand: "SearchTownStandNumber_Sup1",
                SpStandAddress: "StandTownStandNumberAddress_Sup1",
                SpAddress: "SearchTownAddress_Sup1",
                SpScheme: "SearchTownScheme_Sup1",
                SpUnit: "SearchTownUnit_Sup1",
                SpSchemeUnit: "SearchTownSchemeUnit_Sup1",
                SpStandScheme: "SearchTownERFScheme_Sup1",
                SpAddressScheme: "SearchTownAddressScheme_Sup1",
                DetailSp: "IndexObjection_Sup1",
                ConnectionKey: "Sup1Connection"
            ),

            ["Objection_Supp2"] = new(
                TownshipSp: "propertyDetailsTown",
                 SchemeSp: "propertyDetailsScheme",
                SpTown: "SearchTown_Sup2",
                SpStand: "SearchTownStandNumber",
                SpStandAddress: "StandTownStandNumberAddress",
                SpAddress: "SearchTownAddress_Sup2",
                SpScheme: "SearchTownScheme_Sup2",
                SpUnit: "SearchTownUnit_Sup2",
                SpSchemeUnit: "SearchTownSchemeUnit_Sup2",
                SpStandScheme: "SearchTownERFScheme_Sup2",
                SpAddressScheme: "SearchTownAddressScheme_Sup2",
                DetailSp: "IndexObjection_Sup2",
                ConnectionKey: "Sup2Connection"
            ),

            ["Objection_Supp3"] = new(
                TownshipSp: "propertyDetailsTown",
                 SchemeSp: "propertyDetailsScheme",
                SpTown: "SearchTown",
                SpStand: "SearchTownStandNumber",
                SpStandAddress: "StandTownStandNumberAddress",
                SpAddress: "SearchTownAddress",
                SpScheme: "SearchTownScheme",
                SpUnit: "SearchTownUnit",
                SpSchemeUnit: "SearchTownSchemeUnit",
                SpStandScheme: "SearchTownERFScheme",
                SpAddressScheme: "SearchTownAddressScheme",
                DetailSp: "IndexObjection_Sup3",
                ConnectionKey: "Sup3Connection"
            ),

            ["Objection_Supp4"] = new(
                TownshipSp: "propertyDetailsTown",
                 SchemeSp: "propertyDetailsScheme",
                SpTown: "SearchTown",
                SpStand: "SearchTownStandNumber",
                SpStandAddress: "StandTownStandNumberAddress",
                SpAddress: "SearchTownAddress",
                SpScheme: "SearchTownScheme",
                SpUnit: "SearchTownUnit",
                SpSchemeUnit: "SearchTownSchemeUnit",
                SpStandScheme: "SearchTownERFScheme",
                SpAddressScheme: "SearchTownAddressScheme",
                DetailSp: "IndexObjection",
                ConnectionKey: "Sup4Connection",
                Lease: LeaseLookup.Sup4,
                LisLease: LeaseLookup.Lis_2026_10_01
            ),

            ["Objection_Supp5"] = new(
    TownshipSp: "propertyDetailsTown",
     SchemeSp: "propertyDetailsScheme",
    SpTown: "SearchTown",
    SpStand: "SearchTownStandNumber",
    SpStandAddress: "StandTownStandNumberAddress",
    SpAddress: "SearchTownAddress",
    SpScheme: "SearchTownScheme",
    SpUnit: "SearchTownUnit",
    SpSchemeUnit: "SearchTownSchemeUnit",
    SpStandScheme: "SearchTownERFScheme",
    SpAddressScheme: "SearchTownAddressScheme",
    DetailSp: "IndexObjection",
    ConnectionKey: "Sup5Connection"
),
            // ── Section 78 Query roll ─────────────────────────────────
            ["Objection_Query"] = new(
                TownshipSp: "propertyDetailsTown",
                 SchemeSp: "propertyDetailsScheme",
    SpTown: "SearchTown",
    SpStand: "SearchTownStandNumber",
    SpStandAddress: "StandTownStandNumberAddress",
    SpAddress: "SearchTownAddress",
    SpScheme: "SearchTownScheme",
    SpUnit: "SearchTownUnit",
    SpSchemeUnit: "SearchTownSchemeUnit",
    SpStandScheme: "SearchTownERFScheme",
    SpAddressScheme: "SearchTownAddressScheme",
    DetailSp: "IndexSection78Property",
    ConnectionKey: "QueryConnection",
    IsQuery: true
),
        };
}