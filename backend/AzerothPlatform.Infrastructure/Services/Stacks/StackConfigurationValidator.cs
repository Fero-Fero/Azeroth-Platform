using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Validates stack configuration against persistence and module compatibility rules.
/// </summary>
public sealed class StackConfigurationValidator : IStackConfigurationValidator
{
    private static readonly Regex StackNamePattern = new("^[a-zA-Z0-9-]{3,50}$", RegexOptions.Compiled);
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IModuleCatalogService _moduleCatalogService;
    private readonly IServerTypeCatalog _serverTypeCatalog;
    private readonly IArmoryAccountsService _armoryAccountsService;

    public StackConfigurationValidator(
        AzerothCoreDbContext dbContext,
        IModuleCatalogService moduleCatalogService,
        IServerTypeCatalog serverTypeCatalog,
        IArmoryAccountsService armoryAccountsService)
    {
        _dbContext = dbContext;
        _moduleCatalogService = moduleCatalogService;
        _serverTypeCatalog = serverTypeCatalog;
        _armoryAccountsService = armoryAccountsService;
    }

    public async Task<ValidationResultDto> ValidateAsync(
        StackConfigurationDto configuration,
        string? existingStackId = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ValidationResultDto();

        ValidateStackName(configuration, result);
        ValidateDatabase(configuration, existingStackId, result);
        ValidatePorts(configuration, result);
        ValidateAdvanced(configuration, result);
        ValidateDeployment(configuration, result);
        ValidateArmoryAccounts(configuration, result);
        ValidateCustomFork(configuration, result);
        await ValidateModulesAsync(configuration, result, cancellationToken);
        await ValidateUniquenessAsync(configuration, existingStackId, result, cancellationToken);
        await ValidateArmoryAccountsDisableAsync(configuration, existingStackId, result, cancellationToken);

        return result;
    }

    private static void ValidateStackName(StackConfigurationDto configuration, ValidationResultDto result)
    {
        if (string.IsNullOrWhiteSpace(configuration.StackName))
        {
            AddError(result, "stackName", "Stack name is required.");
            return;
        }

        if (!StackNamePattern.IsMatch(configuration.StackName))
        {
            AddError(result, "stackName", "Stack name must be 3-50 characters and use letters, numbers, or dashes only.");
        }
    }

    private static void ValidateDatabase(
        StackConfigurationDto configuration,
        string? existingStackId,
        ValidationResultDto result)
    {
        if (string.IsNullOrWhiteSpace(configuration.Database.RootPassword))
        {
            if (existingStackId is null)
            {
                AddError(result, "database.rootPassword", "Database root password must be at least 8 characters long.");
            }
        }
        else if (configuration.Database.RootPassword.Length < 8)
        {
            AddError(result, "database.rootPassword", "Database root password must be at least 8 characters long.");
        }

        ValidatePort(configuration.Database.Port, "database.port", result);
    }

    private static void ValidatePorts(StackConfigurationDto configuration, ValidationResultDto result)
    {
        var ports = new Dictionary<string, int>
        {
            ["database.port"] = configuration.Database.Port,
            ["ports.authServer"] = configuration.Ports.AuthServer,
            ["ports.worldServer"] = configuration.Ports.WorldServer,
            ["ports.soapPort"] = configuration.Ports.SoapPort
        };

        foreach (var (field, port) in ports)
        {
            ValidatePort(port, field, result);
        }

        var duplicateGroups = ports
            .GroupBy(pair => pair.Value)
            .Where(group => group.Count() > 1);

        foreach (var duplicateGroup in duplicateGroups)
        {
            foreach (var field in duplicateGroup.Select(item => item.Key))
            {
                AddError(result, field, $"Port {duplicateGroup.Key} is used more than once in this configuration.");
            }
        }
    }

    private static void ValidateAdvanced(StackConfigurationDto configuration, ValidationResultDto result)
    {
        if (string.IsNullOrWhiteSpace(configuration.Advanced.RealmName))
        {
            AddError(result, "advanced.realmName", "Realm name is required.");
        }

        if (configuration.Advanced.MaxPlayers < 1 || configuration.Advanced.MaxPlayers > 1000)
        {
            AddError(result, "advanced.maxPlayers", "Max players must be between 1 and 1000.");
        }

        if (configuration.Advanced.ServiceEnvVars.Values
            .Any(bucket => bucket.Any(entry => string.IsNullOrWhiteSpace(entry.Key))))
        {
            AddError(result, "advanced.serviceEnvVars", "Environment variable keys cannot be empty.");
        }
    }

    private static void ValidateDeployment(StackConfigurationDto configuration, ValidationResultDto result)
    {
        if (configuration.Deployment.Target != DeploymentTarget.External)
        {
            return;
        }

        var sshUser = (configuration.Deployment.ExternalSshUser ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(sshUser))
        {
            return;
        }

        if (configuration.Deployment.RemoteOs == RemoteHostOs.Windows)
        {
            AddError(
                result,
                "deployment.remoteOs",
                "Windows Server VPC hosts are not supported. Use Ubuntu or Debian.");
        }

        if (VpcBootstrapUserData.IsForbiddenSshUser(sshUser))
        {
            AddError(
                result,
                "deployment.externalSshUser",
                $"Do not use '{sshUser}' as the stack SSH user. Create a dedicated operator user such as {VpcBootstrapUserData.DefaultOperatorUser}.");
        }
    }

    private void ValidateCustomFork(StackConfigurationDto configuration, ValidationResultDto result)
    {
        if (!_serverTypeCatalog.AllowsCustomRepository(configuration.ServerType))
        {
            return;
        }

        var repository = configuration.CustomFork?.RepositoryUrl?.Trim();
        if (string.IsNullOrWhiteSpace(repository))
        {
            AddError(result, "customFork.repositoryUrl", "A repository URL is required for a custom fork.");
            return;
        }

        try
        {
            ModuleCatalogService.ValidateGitRepository(repository);
        }
        catch (ArgumentException ex)
        {
            AddError(result, "customFork.repositoryUrl", ex.Message);
        }

        var branch = configuration.CustomFork?.Branch?.Trim();
        if (!string.IsNullOrWhiteSpace(branch))
        {
            try
            {
                ModuleCatalogService.ValidateGitRef(branch);
            }
            catch (ArgumentException ex)
            {
                AddError(result, "customFork.branch", ex.Message);
            }
        }
    }

    private async Task ValidateModulesAsync(
        StackConfigurationDto configuration,
        ValidationResultDto result,
        CancellationToken cancellationToken)
    {
        var availableModules = await _moduleCatalogService.ListAllAsync(cancellationToken);
        var modulesById = availableModules.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var moduleId in configuration.ModuleIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!modulesById.TryGetValue(moduleId, out var module))
            {
                AddError(result, "moduleIds", $"Unknown module '{moduleId}'.");
                continue;
            }

            if (!_serverTypeCatalog.IsModuleVisible(module.Id, configuration.ServerType))
            {
                AddError(result, "moduleIds",
                    $"Module '{module.Name}' is not available for the {configuration.ServerType} server type.");
            }

            foreach (var requiredId in module.RequiredModuleIds)
            {
                if (configuration.ModuleIds.Contains(requiredId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var requiredName = modulesById.TryGetValue(requiredId, out var requiredModule)
                    ? requiredModule.Name
                    : requiredId;
                AddError(result, "moduleIds", $"{module.Name} requires the {requiredName} module.");
            }
        }

        foreach (var requiredId in _serverTypeCatalog.GetRequiredModuleIds(configuration.ServerType))
        {
            if (configuration.ModuleIds.Contains(requiredId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var requiredName = modulesById.TryGetValue(requiredId, out var requiredModule)
                ? requiredModule.Name
                : requiredId;
            AddError(result, "moduleIds",
                $"{requiredName} is required for the {configuration.ServerType} server type.");
        }
    }

    private async Task ValidateUniquenessAsync(
        StackConfigurationDto configuration,
        string? existingStackId,
        ValidationResultDto result,
        CancellationToken cancellationToken)
    {
        var normalizedStackName = NormalizeStackName(configuration.StackName);

        var existingStacks = await _dbContext.ManagedStacks
            .Where(stack => existingStackId == null || stack.Id != existingStackId)
            .ToListAsync(cancellationToken);

        if (existingStacks.Any(stack => stack.NormalizedStackName == normalizedStackName))
        {
            AddError(result, "stackName", "A stack with this name already exists.");
        }

        ValidatePortConflict(
            existingStacks.Where(stack => stack.Status != StackStatus.SetupIncomplete),
            configuration.Database.Port, "database.port", result);
        ValidatePortConflict(
            existingStacks.Where(stack => stack.Status != StackStatus.SetupIncomplete),
            configuration.Ports.AuthServer, "ports.authServer", result);
        ValidatePortConflict(
            existingStacks.Where(stack => stack.Status != StackStatus.SetupIncomplete),
            configuration.Ports.WorldServer, "ports.worldServer", result);
        ValidatePortConflict(
            existingStacks.Where(stack => stack.Status != StackStatus.SetupIncomplete),
            configuration.Ports.SoapPort, "ports.soapPort", result);
        SuggestAvailablePorts(existingStacks, configuration, result);
    }

    private static void ValidatePortConflict(
        IEnumerable<Data.Entities.ManagedStackEntity> existingStacks,
        int port,
        string field,
        ValidationResultDto result)
    {
        var conflictingStack = existingStacks.FirstOrDefault(stack =>
            stack.DatabasePort == port ||
            stack.AuthServerPort == port ||
            stack.WorldServerPort == port ||
            stack.SoapPort == port);

        if (conflictingStack is not null)
        {
            AddError(result, field, $"Port {port} is already used by stack '{conflictingStack.StackName}'.");
        }
    }

    private static void ValidatePort(int port, string field, ValidationResultDto result)
    {
        if (port is < 1024 or > 65535)
        {
            AddError(result, field, "Port must be between 1024 and 65535.");
        }
    }

    private static void SuggestAvailablePorts(
        IReadOnlyCollection<Data.Entities.ManagedStackEntity> existingStacks,
        StackConfigurationDto configuration,
        ValidationResultDto result)
    {
        var requestedPorts = new Dictionary<string, int>
        {
            ["database.port"] = configuration.Database.Port,
            ["ports.authServer"] = configuration.Ports.AuthServer,
            ["ports.worldServer"] = configuration.Ports.WorldServer,
            ["ports.soapPort"] = configuration.Ports.SoapPort
        };

        var fieldsNeedingSuggestions = result.Errors
            .Select(error => error.Field)
            .Where(requestedPorts.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (fieldsNeedingSuggestions.Count == 0)
        {
            return;
        }

        var reservedPorts = existingStacks
            .SelectMany(GetPorts)
            .Concat(requestedPorts.Values.Where(port => port is >= 1024 and <= 65535))
            .ToHashSet();

        foreach (var field in fieldsNeedingSuggestions)
        {
            var preferredPort = Math.Clamp(requestedPorts[field] + 1, 1024, 65535);
            var availablePort = FindAvailablePort(reservedPorts, preferredPort);
            if (availablePort is null)
            {
                continue;
            }

            result.SuggestedPorts[field] = availablePort.Value;
            reservedPorts.Add(availablePort.Value);
        }
    }

    private static int? FindAvailablePort(HashSet<int> reservedPorts, int startPort)
    {
        for (var port = startPort; port <= 65535; port++)
        {
            if (!reservedPorts.Contains(port))
            {
                return port;
            }
        }

        for (var port = 1024; port < startPort; port++)
        {
            if (!reservedPorts.Contains(port))
            {
                return port;
            }
        }

        return null;
    }

    private static IEnumerable<int> GetPorts(Data.Entities.ManagedStackEntity stack)
    {
        yield return stack.DatabasePort;
        yield return stack.AuthServerPort;
        yield return stack.WorldServerPort;
        yield return stack.SoapPort;
    }

    private static string NormalizeStackName(string stackName)
    {
        return stackName.Trim().ToUpperInvariant();
    }

    private static void ValidateArmoryAccounts(StackConfigurationDto configuration, ValidationResultDto result)
    {
        var accounts = configuration.ArmoryAccounts ?? new ArmoryAccountsConfigDto();
        if (!accounts.UseEmailConfirmation)
        {
            return;
        }

        var email = accounts.Email;
        if (email is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(email.SmtpHost))
        {
            AddError(result, "armoryAccounts.email.smtpHost", "SMTP host is required when email confirmation is enabled.");
        }

        if (email.SmtpPort is < 1 or > 65535)
        {
            AddError(result, "armoryAccounts.email.smtpPort", "SMTP port must be between 1 and 65535.");
        }

        var security = email.SmtpSecurity?.Trim().ToLowerInvariant();
        if (security is not ("starttls" or "tls" or "none"))
        {
            AddError(result, "armoryAccounts.email.smtpSecurity", "SMTP security must be starttls, tls, or none.");
        }

        if (string.IsNullOrWhiteSpace(email.FromAddress))
        {
            AddError(result, "armoryAccounts.email.fromAddress", "From address is required when email confirmation is enabled.");
        }
        else if (!email.FromAddress.Contains('@', StringComparison.Ordinal))
        {
            AddError(result, "armoryAccounts.email.fromAddress", "From address must be a valid email address.");
        }
    }

    private async Task ValidateArmoryAccountsDisableAsync(
        StackConfigurationDto configuration,
        string? existingStackId,
        ValidationResultDto result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(existingStackId))
        {
            return;
        }

        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == existingStackId, cancellationToken);
        if (stack is null || !stack.ArmoryUseEmailConfirmation)
        {
            return;
        }

        var accounts = configuration.ArmoryAccounts ?? new ArmoryAccountsConfigDto();
        if (accounts.UseEmailConfirmation)
        {
            return;
        }

        var pendingCount = await _armoryAccountsService.GetPendingRegistrationCountAsync(existingStackId, cancellationToken);
        if (pendingCount > 0)
        {
            AddError(
                result,
                "armoryAccounts.useEmailConfirmation",
                $"Cannot disable email confirmation while {pendingCount} pending registration(s) exist.");
        }
    }

    private static void AddError(ValidationResultDto result, string field, string message)
    {
        result.Errors.Add(new ValidationErrorDto
        {
            Field = field,
            Message = message
        });
    }
}
