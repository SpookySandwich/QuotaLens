using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.ViewModels;

internal static class ProviderAddFlow
{
    public static async Task<ProviderInstance?> AddAsync(
        IProviderService service,
        ProviderType providerType,
        Func<ProviderInstance, Task<bool>> configureAsync,
        Func<ProviderInstance, Task<bool>>? loginAsync = null,
        Func<ProviderInstance, bool>? needsLocalSetup = null)
    {
        // Multi-source providers (e.g. Kimi App + CLI) open the config dialog so the
        // user can pick a source and see what was detected — never silently auto-add.
        if (ProviderRegistry.HasMultipleSources(providerType.Id))
        {
            var multiInstance = service.AddInstance(providerType.Id, refreshImmediately: false);
            var keep = await configureAsync(multiInstance).ConfigureAwait(true);
            if (!keep)
            {
                service.RemoveInstance(multiInstance.Id);
                return null;
            }

            await service.RefreshAsync(multiInstance.Id).ConfigureAwait(true);
            return multiInstance;
        }

        var setupKind = Catalog.SetupKindFor(providerType.Id);
        if (setupKind is ProviderSetupKind.Ready)
            return service.AddInstance(providerType.Id, refreshImmediately: true);

        if (setupKind is ProviderSetupKind.LocalAppOrCli)
        {
            var localInstance = service.AddInstance(providerType.Id, refreshImmediately: false);
            var shouldConfigureLocal = needsLocalSetup?.Invoke(localInstance)
                ?? ProviderLocalSetup.NeedsSetup(localInstance.Id, localInstance.Type, service.Config);
            if (!shouldConfigureLocal)
            {
                await service.RefreshAsync(localInstance.Id).ConfigureAwait(true);
                return localInstance;
            }

            var keepLocalInstance = false;
            try
            {
                keepLocalInstance = await configureAsync(localInstance).ConfigureAwait(true);
            }
            catch
            {
                keepLocalInstance = false;
            }

            if (!keepLocalInstance)
            {
                service.RemoveInstance(localInstance.Id);
                return null;
            }

            await service.RefreshAsync(localInstance.Id).ConfigureAwait(true);
            return localInstance;
        }

        var instance = service.AddInstance(providerType.Id, refreshImmediately: false);
        var keepInstance = false;

        try
        {
            keepInstance = setupKind switch
            {
                ProviderSetupKind.ApiKey => await configureAsync(instance).ConfigureAwait(true),
                ProviderSetupKind.BrowserLogin => await (loginAsync?.Invoke(instance)
                    ?? service.OpenLoginAsync(instance.Id)).ConfigureAwait(true),
                _ => true,
            };
        }
        catch
        {
            keepInstance = false;
        }

        if (!keepInstance)
        {
            service.RemoveInstance(instance.Id);
            return null;
        }

        if (setupKind != ProviderSetupKind.BrowserLogin)
            await service.RefreshAsync(instance.Id).ConfigureAwait(true);
        return instance;
    }

    internal static bool RequiresUserConfiguration(string providerType) =>
        Catalog.RequiresUserConfiguration(providerType);

    internal static bool RequiresSetup(string providerType) =>
        Catalog.SetupKindFor(providerType) is ProviderSetupKind.ApiKey or ProviderSetupKind.BrowserLogin;
}
