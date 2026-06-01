using System.Threading;
using System.Threading.Tasks;

namespace TaskbarQuota.Usage
{
    /// <summary>Whether a provider is a subscription plan (percent-based) or API-billed (cost-based).</summary>
    public enum BillingKind
    {
        Subscription,
        Api,
    }

    public interface IUsageProvider
    {
        ProviderId Id { get; }
        string DisplayName { get; }
        string SessionLabel { get; }
        string WeeklyLabel { get; }
        BillingKind Billing { get; }

        Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default);
    }
}
