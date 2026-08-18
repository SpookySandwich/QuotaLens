using QuotaLens.Core;

namespace QuotaLens.Services;

public sealed record ProviderConnectionResult(bool Started, ProviderSnapshot? Snapshot)
{
    public bool Verified => Snapshot is not null;
}

/// <summary>
/// Runs every source setup flow the same way: start its source-owned action, then poll
/// that exact source until it returns real data. No provider identity checks live here.
/// </summary>
public static class ProviderConnectionCoordinator
{
    public static async Task<ProviderConnectionResult> ConnectAndVerifyAsync(
        IProviderSource source,
        string instanceId,
        IConfig config,
        CancellationToken ct)
    {
        var action = source.ConnectionAction;
        if (action is null)
            return new ProviderConnectionResult(false, null);

        if (!await action.StartAsync(instanceId, config, ct).ConfigureAwait(false))
            return new ProviderConnectionResult(false, null);

        var deadline = DateTimeOffset.UtcNow + action.VerificationTimeout;
        ProviderException? lastError = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await source.FetchAsync(instanceId, config, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(snapshot.Error))
                    throw new ProviderException(snapshot.Error, snapshot.ErrorKind);
                return new ProviderConnectionResult(true, snapshot);
            }
            catch (ProviderException error) when (CanRetry(error))
            {
                lastError = error;
            }

            if (DateTimeOffset.UtcNow >= deadline)
                break;

            await Task.Delay(action.VerificationRetryDelay, ct).ConfigureAwait(false);
        }
        while (true);

        throw lastError ?? new ProviderException("Not available: The data source did not become ready.");
    }

    internal static bool CanRetry(ProviderException error)
    {
        if (error.Kind == ProviderErrorKind.AuthenticationRequired)
            return true;
        if (error.Kind != ProviderErrorKind.Unknown)
            return false;

        return error.Message.StartsWith("Not available", StringComparison.OrdinalIgnoreCase)
            || error.Message.StartsWith("Network error", StringComparison.OrdinalIgnoreCase)
            || error.Message.StartsWith("Timeout", StringComparison.OrdinalIgnoreCase);
    }
}
