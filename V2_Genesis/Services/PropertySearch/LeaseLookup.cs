namespace GenesisV2.Services.PropertySearch;

/// <summary>
/// Where the LEASE (Y/N) flag is read from (read only): unit type FL or SL
/// = Y, anything else = N. Column names come from code, never from a request.
/// </summary>
public sealed record LeaseLookup(string Table, string UnitKey, string ValuationKey, string UnitType)
{
    /// Supplementary roll 4 table.
    public static readonly LeaseLookup Sup4 =
        new("dbo.[Sup4]", "[UNITKEY]", "[VALUATIONKEY]", "[UNITTYPE]");

    /// LIS extract used by the Supp 4 LIS search (same database).
    public static readonly LeaseLookup Lis_2026_10_01 =
        new("dbo.[LIS_2026_10_01]", "[Unit Key]", "[Valuation Key]", "[Unit Type]");

    public string BuildSql() => $"""
        SELECT TRY_CAST(TRY_CAST({UnitKey}      AS FLOAT) AS BIGINT) AS UnitKey,
               TRY_CAST(TRY_CAST({ValuationKey} AS FLOAT) AS BIGINT) AS ValuationKey,
               CAST(CASE WHEN UPPER(LTRIM(RTRIM(CAST({UnitType} AS NVARCHAR(10))))) IN (N'FL', N'SL')
                         THEN 1 ELSE 0 END AS BIT)                    AS IsLease
        FROM {Table}
        WHERE TRY_CAST(TRY_CAST({UnitKey} AS FLOAT) AS BIGINT) IN @Keys;
        """;
}
