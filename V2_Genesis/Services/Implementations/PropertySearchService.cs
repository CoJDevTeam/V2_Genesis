using Dapper;
using GenesisV2.Services.PropertySearch;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Data;
using V2_Genesis.Helpers;
using V2_Genesis.Models;
using V2_Genesis.Models.Results;
using V2_Genesis.Models.Results.Section78;
using V2_Genesis.Services;
using V2_Genesis.Services.Interfaces;
using V2_Genesis.Services.Omission;
using V2_Genesis.Services.PropertySearch;

namespace V2_Genesis.Services.Implementations;

public class PropertySearchService : IPropertySearchService
{
    private readonly IConfiguration _config;
    private readonly ILogger<PropertySearchService> _logger;
    private readonly IMemoryCache _cache;

    // Used only for shared township and scheme procedures.
    private readonly string _defaultConn;

    private const string SP_TOWNSHIPS =
        "Objection.dbo.propertyDetailsTown";

    private const string SP_SCHEMES =
        "Objection.dbo.propertyDetailsScheme";

    private const string SP_LINK_PROPERTY =
        "InsertLinkedProperty";

    public PropertySearchService(
        IConfiguration config,
        ILogger<PropertySearchService> logger,
        IMemoryCache cache)
    {
        _config = config;
        _logger = logger;
        _cache = cache;

        _defaultConn =
            config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "DefaultConnection is missing from configuration.");
    }

    // ─────────────────────────────────────────────────────────────
    // Connection handling
    // ─────────────────────────────────────────────────────────────

    private string GetRollConnection(
        RollSearchConfig config)
    {
        return _config.GetConnectionString(
                   config.ConnectionKey)
               ?? _defaultConn;
    }

    private static bool IsQueryRoll(
        string rollSource,
        RollSearchConfig config)
    {
        return config.IsQuery ||
               rollSource.Equals(
                   "Query",
                   StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────
    // Shared search lists
    // ─────────────────────────────────────────────────────────────

    public async Task<List<string>> GetTownshipsAsync(
     string? rollSource = null)
    {
        var source = string.IsNullOrWhiteSpace(rollSource)
            ? "Objection"
            : rollSource.Trim();

        var cacheKey =
            $"PropertySearch:Townships:Roll:{source}";

        if (_cache.TryGetValue(
                cacheKey,
                out List<string>? cachedTownships) &&
            cachedTownships is not null)
        {
            return cachedTownships;
        }

        if (!RollSearchRegistry.Configs.TryGetValue(
                source,
                out var rollConfig))
        {
            _logger.LogWarning(
                "No township configuration found for Roll={RollSource}",
                source);

            return new List<string>();
        }

        var connectionString =
            GetRollConnection(rollConfig);

        try
        {
            await using var conn =
                new SqlConnection(connectionString);

            var rows = await conn.QueryAsync<string>(
                rollConfig.TownshipSp,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 60);

            var result = rows
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

            _cache.Set(
                cacheKey,
                result,
                TimeSpan.FromMinutes(30));

            _logger.LogInformation(
                "Loaded {Count} townships. Roll={RollSource}, SP={TownshipSp}, Connection={ConnectionKey}",
                result.Count,
                source,
                rollConfig.TownshipSp,
                rollConfig.ConnectionKey);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not load townships. Roll={RollSource}, SP={TownshipSp}, Connection={ConnectionKey}",
                source,
                rollConfig.TownshipSp,
                rollConfig.ConnectionKey);

            // Never fall back to another roll.
            return new List<string>();
        }
    }
    public async Task<List<string>> GetSchemesAsync(
     string? rollSource = null,
     string? township = null)
    {
        var source = string.IsNullOrWhiteSpace(rollSource)
            ? "Objection"
            : rollSource.Trim();

        if (!RollSearchRegistry.Configs.TryGetValue(
                source,
                out var rollConfig))
        {
            return new List<string>();
        }

        var town = township?.Trim() ?? string.Empty;

        var cacheKey =
            $"PropertySearch:Schemes:{source}:{town}";

        if (_cache.TryGetValue(
                cacheKey,
                out List<string>? cached) &&
            cached is not null)
        {
            return cached;
        }

        try
        {
            var connectionString =
                GetRollConnection(rollConfig);

            await using var conn =
                new SqlConnection(connectionString);

            var parameters =
                new DynamicParameters();

            parameters.Add(
                "@TownName",
                string.IsNullOrWhiteSpace(town)
                    ? null
                    : town);

            var rows = await conn.QueryAsync<string>(
                rollConfig.SchemeSp,
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 60);

            var result = rows
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

            _cache.Set(
                cacheKey,
                result,
                TimeSpan.FromMinutes(30));

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed loading schemes. Roll={Roll}, Town={Town}",
                source,
                town);

            return new List<string>();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Property search
    // ─────────────────────────────────────────────────────────────

    public async Task<List<PropertySearchResult>> SearchAsync(
        string rollSource,
        PropertySearchParams searchParams,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rollSource))
            return new List<PropertySearchResult>();

        if (!RollSearchRegistry.Configs.TryGetValue(
                rollSource,
                out var config))
        {
            _logger.LogWarning(
                "Unknown property-search roll source: {RollSource}",
                rollSource);

            return new List<PropertySearchResult>();
        }

        ArgumentNullException.ThrowIfNull(searchParams);

        var storedProcedure =
            ResolveSp(config, searchParams);

        var parameters =
            BuildParams(searchParams, IsQueryRoll(rollSource, config));

        var connectionString =
            GetRollConnection(config);

        try
        {
            await using var conn =
                new SqlConnection(connectionString);

            // Township-only searching is a supported business flow because it
            // allows a client to continue to LIS/Omission when the property is
            // not on the selected valuation roll. Give that broader search a
            // little more SQL time while keeping narrowed searches fast.
            var isTownshipOnly =
                !searchParams.HasStand &&
                !searchParams.HasAddress &&
                !searchParams.HasScheme &&
                !searchParams.HasUnit;

            var results = await QueryAllowingOptionalFiltersAsync(
                conn,
                storedProcedure,
                parameters,
                isTownshipOnly ? 45 : 15,
                rollSource,
                cancellationToken);

            /*
             * For the Query roll, the search stored procedure must
             * return Review_Close_Date.
             *
             * Dapper maps it directly to:
             * PropertySearchResult.Review_Close_Date
             */
            var list = results.ToList();
            await ApplyLeaseFlagsAsync(conn, config.Lease, rollSource, list, cancellationToken);
            return list;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Property search failed. Roll: {RollSource}, SP: {StoredProcedure}",
                rollSource,
                storedProcedure);

            throw new ApplicationException(
                $"Property search failed for roll '{rollSource}'.",
                ex);
        }
    }

    // Scheme (and Stand / Address / Unit) are OPTIONAL filters.
    // Some legacy roll databases (e.g. the Section 78 Query DB) declare a
    // search procedure parameter without a default, so SQL rejects a
    // township-only search with error 201 "expects parameter '@X'".
    // Treat any such filter as "match everything" and retry, instead of
    // forcing the client to pick a scheme.
    private static readonly System.Text.RegularExpressions.Regex MissingParam =
        new(@"expects parameter '(@\w+)'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task<IEnumerable<PropertySearchResult>> QueryAllowingOptionalFiltersAsync(
        SqlConnection conn,
        string storedProcedure,
        DynamicParameters parameters,
        int timeoutSeconds,
        string rollSource,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await conn.QueryAsync<PropertySearchResult>(
                    new CommandDefinition(
                        storedProcedure,
                        parameters,
                        commandType: CommandType.StoredProcedure,
                        commandTimeout: timeoutSeconds,
                        cancellationToken: cancellationToken));
            }
            catch (SqlException ex) when (ex.Number == 201 && attempt < 4)
            {
                var match = MissingParam.Match(ex.Message);
                if (!match.Success)
                    throw;

                var name = match.Groups[1].Value;

                _logger.LogWarning(
                    "Search SP {Sp} on roll {Roll} requires {Param}; retrying with a match-all value. " +
                    "Give this parameter a default of NULL/'%' in SQL to remove this retry.",
                    storedProcedure,
                    rollSource,
                    name);

                parameters.Add(name, "%");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Property details
    // ─────────────────────────────────────────────────────────────

    public async Task<List<PropertyDetailResult>>
        GetPropertyDetailsAsync(
            string rollSource,
            string unitKey,
            string valuationKey,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rollSource))
            return new List<PropertyDetailResult>();

        if (!RollSearchRegistry.Configs.TryGetValue(
                rollSource,
                out var config))
        {
            _logger.LogWarning(
                "Unknown property-detail roll source: {RollSource}",
                rollSource);

            return new List<PropertyDetailResult>();
        }

        if (string.IsNullOrWhiteSpace(unitKey) &&
            string.IsNullOrWhiteSpace(valuationKey))
        {
            return new List<PropertyDetailResult>();
        }

        var connectionString =
            GetRollConnection(config);

        try
        {
            await using var conn =
                new SqlConnection(connectionString);

            var results = (await conn.QueryAsync<PropertyDetailResult>(
                new CommandDefinition(
                    config.DetailSp,
                    new
                    {
                        UnitKey = unitKey,
                        ValuationKey = valuationKey
                    },
                    commandType: CommandType.StoredProcedure,
                    commandTimeout: 15,
                    cancellationToken: cancellationToken)))
                .ToList();

            /*
             * SECTION 78 / QUERY SPLITS
             * -------------------------
             * A multiple-purpose property can have several valuation split
             * rows for the same UnitKey.  The Query version of IndexObjection
             * can return only the selected valuation row when ValuationKey is
             * supplied, while the objection roll detail procedures return the
             * complete split set.
             *
             * For the Query roll, retry the same detail SP with the UnitKey
             * only.  If that returns more rows, use the broader result so the
             * pre-link Property View shows every split and the Query form can
             * receive the same values.
             */
            if (config.IsQuery &&
                !string.IsNullOrWhiteSpace(unitKey) &&
                results.Count <= 1)
            {
                try
                {
                    var splitRows = (await conn.QueryAsync<PropertyDetailResult>(
                        new CommandDefinition(
                            config.DetailSp,
                            new
                            {
                                UnitKey = unitKey,
                                ValuationKey = string.Empty
                            },
                            commandType: CommandType.StoredProcedure,
                            commandTimeout: 30,
                            cancellationToken: cancellationToken)))
                        .ToList();

                    if (splitRows.Count > results.Count)
                    {
                        _logger.LogInformation(
                            "Query property detail expanded from {OriginalCount} to {SplitCount} valuation rows. UnitKey={UnitKey}",
                            results.Count,
                            splitRows.Count,
                            unitKey);

                        results = splitRows;
                    }
                }
                catch (Exception ex)
                {
                    // The broader split lookup is a fallback only.  Keep the
                    // original exact detail row if the legacy SP does not
                    // accept an empty ValuationKey.
                    _logger.LogWarning(
                        ex,
                        "Query split expansion failed. Keeping exact property detail. UnitKey={UnitKey}, ValuationKey={ValuationKey}",
                        unitKey,
                        valuationKey);
                }
            }

            /*
             * For the Query roll, the detail stored procedure must
             * return Review_Close_Date.
             *
             * Dapper maps it directly to:
             * PropertyDetailResult.Review_Close_Date
             */
            var details = DeduplicatePropertyDetails(results);
            await ApplyLeaseFlagsAsync(conn, config.Lease, rollSource, details, cancellationToken);
            return details;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Property-detail lookup failed. Roll: {RollSource}, UnitKey: {UnitKey}, ValuationKey: {ValuationKey}",
                rollSource,
                unitKey,
                valuationKey);

            throw new ApplicationException(
                $"Property details could not be loaded for roll '{rollSource}'.",
                ex);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // LEASE (Y/N) flag (Supp 4)
    // ─────────────────────────────────────────────────────────────
    // Y when the unit type is FL or SL, otherwise N. Read only; the search
    // procedures do not need to change.

    private const int LeaseLookupChunk = 1000;

    private async Task ApplyLeaseFlagsAsync(
        SqlConnection conn,
        LeaseLookup? lookup,
        string rollSource,
        List<PropertySearchResult> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var flags = await LoadLeaseFlagsAsync(conn, lookup, rollSource,
            rows.Select(r => r.UnitKey), cancellationToken);
        if (flags is null) return;

        foreach (var row in rows)
        {
            var flag = LeaseFlags.Find(flags, row.UnitKey, row.ValuationKey);
            if (flag is not null)
                row.Lease = LeaseFlags.SearchText(flag);
        }
    }

    private async Task ApplyLeaseFlagsAsync(
        SqlConnection conn,
        LeaseLookup? lookup,
        string rollSource,
        List<PropertyDetailResult> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var flags = await LoadLeaseFlagsAsync(conn, lookup, rollSource,
            rows.Select(r => r.UnitKey), cancellationToken);
        if (flags is null) return;

        foreach (var row in rows)
        {
            var flag = LeaseFlags.Find(flags, row.UnitKey, row.ValuationKey);
            if (flag is not null)
                row.LeaseDesc = LeaseFlags.DetailText(flag);
        }
    }

    private async Task<List<LeaseFlag>?> LoadLeaseFlagsAsync(
        SqlConnection conn,
        LeaseLookup? lookup,
        string rollSource,
        IEnumerable<string?> unitKeys,
        CancellationToken cancellationToken)
    {
        if (lookup is null) return null;

        var keys = unitKeys
            .Select(LeaseFlags.Key)
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .Distinct()
            .ToList();
        if (keys.Count == 0) return null;

        var table = lookup.Table;
        var sql = lookup.BuildSql();

        try
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(cancellationToken);

            var all = new List<LeaseFlag>();
            foreach (var chunk in keys.Chunk(LeaseLookupChunk))
            {
                all.AddRange(await conn.QueryAsync<LeaseFlag>(
                    new CommandDefinition(sql, new { Keys = chunk },
                        commandTimeout: 15, cancellationToken: cancellationToken)));
            }
            return all;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The flag is extra information – never fail the search over it
            _logger.LogWarning(ex,
                "Lease flags not loaded for roll {RollSource} ({Table}).",
                rollSource, table);
            return null;
        }
    }

    /// LEASE (Y/N) for LIS search results (Supp 4 LIS table).
    public async Task ApplyLisLeaseFlagsAsync(
        string rollSource,
        List<PropertySearchResult> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0 ||
            !RollSearchRegistry.Configs.TryGetValue(rollSource, out var config) ||
            config.LisLease is null)
            return;

        await using var conn = new SqlConnection(GetRollConnection(config));
        await ApplyLeaseFlagsAsync(conn, config.LisLease, rollSource, rows, cancellationToken);
    }

    /// Lease field on Property Details for a LIS property.
    public async Task ApplyLisLeaseFlagsAsync(
        string rollSource,
        List<PropertyDetailResult> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0 ||
            !RollSearchRegistry.Configs.TryGetValue(rollSource, out var config) ||
            config.LisLease is null)
            return;

        await using var conn = new SqlConnection(GetRollConnection(config));
        await ApplyLeaseFlagsAsync(conn, config.LisLease, rollSource, rows, cancellationToken);
    }

    private static List<PropertyDetailResult>
        DeduplicatePropertyDetails(
            List<PropertyDetailResult> rows)
    {
        return rows
            .GroupBy(x => new
            {
                UnitKey =
                    Clean(x.UnitKey),

                ValuationKey =
                    Clean(x.ValuationKey),

                PropertyId =
                    Clean(x.PropertyId),

                Category =
                    Clean(x.CatDesc),

                Extent =
                    Clean(x.RateableArea),

                MarketValue =
                    CleanMoney(x.MarketValue),

                WefDate =
                    Clean(x.WefDate),

                /*
                 * Preserve separate records when the review
                 * closing date differs.
                 */
                ReviewCloseDate =
                    x.Review_Close_Date
            })
            .Select(group => group.First())
            .ToList();
    }

    private static string Clean(
        string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private static string CleanMoney(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(
            value.Where(char.IsDigit).ToArray());
    }

    // ─────────────────────────────────────────────────────────────
    // Stored procedure selection
    // ─────────────────────────────────────────────────────────────

    private static string ResolveSp(
        RollSearchConfig config,
        PropertySearchParams searchParams)
    {
        if (searchParams.HasStand &&
            !searchParams.HasAddress &&
            !searchParams.HasScheme)
        {
            return config.SpStand;
        }

        if (searchParams.HasStand &&
            searchParams.HasAddress &&
            !searchParams.HasScheme)
        {
            return config.SpStandAddress;
        }

        if (!searchParams.HasStand &&
            !searchParams.HasAddress &&
            searchParams.HasScheme &&
            !searchParams.HasUnit)
        {
            return config.SpScheme;
        }

        if (!searchParams.HasStand &&
            searchParams.HasAddress &&
            !searchParams.HasScheme)
        {
            return config.SpAddress;
        }

        if (!searchParams.HasStand &&
            !searchParams.HasAddress &&
            !searchParams.HasScheme &&
            searchParams.HasUnit)
        {
            return config.SpUnit;
        }

        if (searchParams.HasScheme &&
            searchParams.HasUnit)
        {
            return config.SpSchemeUnit;
        }

        if (searchParams.HasStand &&
            !searchParams.HasAddress &&
            searchParams.HasScheme)
        {
            return config.SpStandScheme;
        }

        if (!searchParams.HasStand &&
            searchParams.HasAddress &&
            searchParams.HasScheme)
        {
            return config.SpAddressScheme;
        }

        return config.SpTown;
    }

    private static DynamicParameters BuildParams(
        PropertySearchParams searchParams,
        bool optimiseForQueryRoll)
    {
        var parameters = new DynamicParameters();

        // Township and Scheme are selected from dropdown lists. For the Query
        // roll use exact matching so SQL can use an index seek instead of a
        // leading-wildcard scan. Stand and Unit are also identifiers and are
        // treated as exact values. Address remains a contains search because
        // users commonly enter only part of the street address.
        var town = searchParams.TownName.Trim();

        // IMPORTANT BUSINESS RULE:
        // Township is the only required search field.
        //
        // The existing SearchTown stored procedures across GV, supplementary
        // rolls and Query were built around LIKE-style matching, so preserve
        // the original wildcard parameter contract. This also tolerates legacy
        // data with spacing / description differences between roll databases.
        parameters.Add(
            "@SearchTownName",
            $"%{town}%");

        if (searchParams.HasStand)
        {
            var stand = searchParams.Stand!.Trim();
            parameters.Add(
                "@SearchStand",
                optimiseForQueryRoll ? stand : $"%{stand}%");
        }

        if (searchParams.HasAddress)
        {
            var address = searchParams.Address!.Trim();
            parameters.Add(
                "@SearchAddress",
                $"%{address}%");
        }

        if (searchParams.HasScheme)
        {
            var scheme = searchParams.Scheme!.Trim();
            parameters.Add(
                "@SearchScheme",
                optimiseForQueryRoll ? scheme : $"%{scheme}%");
        }

        if (searchParams.HasUnit)
        {
            var unit = searchParams.Unit!.Trim();
            parameters.Add(
                "@SearchUnit",
                optimiseForQueryRoll ? unit : $"%{unit}%");
        }

        return parameters;
    }

    // ─────────────────────────────────────────────────────────────
    // Link property
    // ─────────────────────────────────────────────────────────────

    public async Task<LinkResult> LinkPropertyAsync(
        string rollSource,
        string idProperty,
        string userId,
        string propertyFrom,
        string? unitKey = null,
        string? valuationKey = null)
    {
        if (string.IsNullOrWhiteSpace(rollSource))
        {
            return LinkResult.Fail(
                "A valuation roll must be supplied.");
        }

        if (string.IsNullOrWhiteSpace(idProperty))
        {
            return LinkResult.Fail(
                "A property must be selected.");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return LinkResult.Fail(
                "The current user could not be identified.");
        }

        if (!RollSearchRegistry.Configs.TryGetValue(
                rollSource,
                out var config))
        {
            return LinkResult.Fail(
                $"Unknown roll source '{rollSource}'.");
        }

        var connectionString =
            GetRollConnection(config);

        var isQueryRoll =
            IsQueryRoll(rollSource, config);

        /*
         * Query linked properties historically do not use
         * PropertyFrom. Normal objection/appeal rolls retain it.
         */
        string? resolvedPropertyFrom =
            isQueryRoll
                ? null
                : propertyFrom;

        try
        {
            await using var conn =
                new SqlConnection(connectionString);

            await conn.OpenAsync();

            if (isQueryRoll)
            {
                /*
                 * InsertLinkedProperty for Objection_Query now returns:
                 *
                 * NewID
                 * Review_Status
                 * Review_Close_Date
                 */
                // QueryFirstOrDefault (not Single): only the first row of
                // the first result set is needed, even if the procedure
                // ever returns extra rows or result sets.
                // Section 78: the keys (UnitKey + ValuationKey) decide Query
                // vs Review. If the database still has the OLD version of
                // InsertLinkedProperty (no @UnitKey / @ValuationKey), SQL
                // raises 8144 "too many arguments" - link without the keys
                // instead of failing, and log it so the SP gets updated.
                LinkSection78PropertyResult? linkedResult;

                try
                {
                    linkedResult =
                        await conn.QueryFirstOrDefaultAsync<
                            LinkSection78PropertyResult>(
                            SP_LINK_PROPERTY,
                            new
                            {
                                IDProperty = idProperty.Trim(),
                                UserID = userId,
                                PropertyFrom = resolvedPropertyFrom,
                                UnitKey =
                                    string.IsNullOrWhiteSpace(unitKey) ? null : unitKey.Trim(),
                                ValuationKey =
                                    string.IsNullOrWhiteSpace(valuationKey) ? null : valuationKey.Trim()
                            },
                            commandType: CommandType.StoredProcedure,
                            commandTimeout: 90);
                }
                catch (SqlException ex) when (ex.Number == 8144)
                {
                    _logger.LogWarning(
                        "InsertLinkedProperty on {RollSource} does not accept @UnitKey/@ValuationKey yet. " +
                        "Linking with IDProperty only. Run the Section 78 SQL script to update the procedure.",
                        rollSource);

                    linkedResult =
                        await conn.QueryFirstOrDefaultAsync<
                            LinkSection78PropertyResult>(
                            SP_LINK_PROPERTY,
                            new
                            {
                                IDProperty = idProperty.Trim(),
                                UserID = userId,
                                PropertyFrom = resolvedPropertyFrom
                            },
                            commandType: CommandType.StoredProcedure,
                            commandTimeout: 90);
                }

                if (linkedResult is null)
                {
                    return LinkResult.Fail(
                        "The property could not be linked because the database returned no result.");
                }

                return LinkResult.Ok(
                    linkedPropertyId:
                        linkedResult.NewID,

                    reviewStatus:
                        linkedResult.Review_Status,

                    reviewCloseDate:
                        linkedResult.Review_Close_Date);
            }

            /*
             * Existing non-Query rolls may still return only an
             * identity value or may not return a result set.
             */
            await conn.ExecuteAsync(
                SP_LINK_PROPERTY,
                new
                {
                    IDProperty = idProperty,
                    UserID = userId,
                    PropertyFrom =
                        resolvedPropertyFrom
                },
                commandType:
                    CommandType.StoredProcedure,
                commandTimeout: 60);

            return LinkResult.Ok();
        }
        catch (SqlException ex)
            when (ex.Number == 2627 ||
                  ex.Number == 2601)
        {
            return LinkResult.Duplicate();
        }
        catch (SqlException ex)
            when (ex.Number == 50001)
        {
            _logger.LogWarning(
                ex,
                "The selected Query LIS property was not found. Property ID: {PropertyId}",
                idProperty);

            return LinkResult.Fail(
                ex.Message);
        }
        catch (SqlException ex)
        {
            _logger.LogError(
                ex,
                "SQL error {Number} linking property {PropertyId} for user {UserId} on roll {RollSource}: {Message}",
                ex.Number,
                idProperty,
                userId,
                rollSource,
                ex.Message);

            return LinkResult.Fail(
                $"The property could not be linked because of a database error (SQL {ex.Number}). Please contact support.");
        }
        catch (Exception ex) when (ex is DataException or InvalidCastException or InvalidOperationException)
        {
            // The procedure ran but its result could not be read (e.g. a
            // column type the model does not expect). Don't crash the page.
            _logger.LogError(
                ex,
                "Link result could not be read. Property {PropertyId}, user {UserId}, roll {RollSource}. {Message}",
                idProperty,
                userId,
                rollSource,
                ex.Message);

            return LinkResult.Fail(
                "The property was sent for linking but the result could not be read. Please check your dashboard or try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error linking property {PropertyId} for user {UserId} on roll {RollSource}. {Message}",
                idProperty,
                userId,
                rollSource,
                ex.Message);

            return LinkResult.Fail(
                "The property could not be linked. Please try again.");
        }
    }
}