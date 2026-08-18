using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Validates stack configuration before persistence or build orchestration.
/// </summary>
public interface IStackConfigurationValidator
{
    Task<ValidationResultDto> ValidateAsync(
        StackConfigurationDto configuration,
        string? existingStackId = null,
        CancellationToken cancellationToken = default);
}
