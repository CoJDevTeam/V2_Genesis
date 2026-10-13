using V2_Genesis.Models;
using V2_Genesis.Models.Results;

namespace V2_Genesis.Services.Interfaces
{
    public interface IPropertySearchService
    {

        Task<List<string>> GetTownshipsAsync(string? rollSource = null);


        Task<List<string>> GetSchemesAsync(
    string? rollSource = null,
    string? township = null);


        Task<List<PropertySearchResult>> SearchAsync(
            string rollSource,
            PropertySearchParams searchParams,
            CancellationToken cancellationToken = default);


        /// LEASE (Y/N) for LIS results: unit type FL / SL = lease.
        Task ApplyLisLeaseFlagsAsync(
            string rollSource,
            List<PropertySearchResult> rows,
            CancellationToken cancellationToken = default);

        Task ApplyLisLeaseFlagsAsync(
            string rollSource,
            List<PropertyDetailResult> rows,
            CancellationToken cancellationToken = default);

        Task<List<PropertyDetailResult>> GetPropertyDetailsAsync(
            string rollSource,
            string unitKey,
            string valuationKey,
            CancellationToken cancellationToken = default);


        Task<LinkResult> LinkPropertyAsync(
            string rollSource,
            string idProperty,
            string userId,
            string propertyFrom,
            string? unitKey = null,
            string? valuationKey = null);
    }
}