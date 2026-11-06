using DingTalkInsights.Core;

namespace DingTalkInsights.Runtime;

/// <summary>Lazy standard-location verification. Resolve is only called after explicit review.
/// Injected providers are for guarded isolated roots; production always uses the standalone known folder.</summary>
public sealed class CanonicalStoreLocation(Func<string> canonicalRoot)
{
    public static CanonicalStoreLocation Production()=>new(()=>Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DingTalkInsights"));
    public string? Resolve(bool reviewed)
    {
        if(!reviewed)return null;
        var root=canonicalRoot();
        if(!Path.IsPathFullyQualified(root))throw new RuntimeFault(RuntimeFailure.Connection);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    public Task<bool> ConnectAsync(InsightController controller,string reviewedRoot,bool confirmed,CancellationToken cancellation=default)
    {
        if(!confirmed)return Task.FromResult(false);
        // An arbitrary copied ledger cannot exclude the standard old standalone app.
        if(!string.Equals(Resolve(true),Path.TrimEndingDirectorySeparator(Path.GetFullPath(reviewedRoot)),
            OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new RuntimeFault(RuntimeFailure.Connection);
        return controller.ConnectAsync(reviewedRoot,true,cancellation);
    }
}
