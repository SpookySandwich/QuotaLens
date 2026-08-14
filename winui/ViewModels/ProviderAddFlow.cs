using QuotaLens.Core;

namespace QuotaLens.ViewModels;

internal static class ProviderAddFlow
{
    public static async Task<ProviderInstance?> AddAsync(
        IProviderService service,
        ProviderType providerType,
        Func<ProviderInstance, Task<bool>> configureAsync)
    {
        var instance = service.AddInstance(providerType.Id, refreshImmediately: false);
        var keep = false;
        try
        {
            keep = await configureAsync(instance).ConfigureAwait(true);
        }
        catch
        {
            keep = false;
        }

        if (!keep)
        {
            service.RemoveInstance(instance.Id);
            return null;
        }

        await service.RefreshAsync(instance.Id).ConfigureAwait(true);
        return instance;
    }

    internal static bool RequiresUserConfiguration(string providerType) =>
        Catalog.RequiresUserConfiguration(providerType);

    internal static bool RequiresSetup(string providerType) =>
        Catalog.SetupKindFor(providerType) is ProviderSetupKind.ApiKey or ProviderSetupKind.BrowserLogin;
}
