using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Provides the per-service environment-variable templates the stack wizard renders. Each stack
/// service (worldserver, authserver, armory, client) declares the variables it accepts so admins can
/// configure them per container instead of dumping everything into one global list.
/// </summary>
public interface IServiceEnvTemplateService
{
    /// <summary>Returns the env-var templates for every configurable stack service, in display order.</summary>
    IReadOnlyList<ServiceEnvTemplate> GetTemplates();
}
