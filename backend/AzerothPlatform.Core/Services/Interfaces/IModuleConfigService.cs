using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface IModuleConfigService
{
    Task<ModuleConfigSchema> GetConfigSchemaAsync(string moduleId, CancellationToken cancellationToken = default);
    Task RefreshCacheAsync(CancellationToken cancellationToken = default);
}
