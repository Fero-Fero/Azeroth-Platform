using Microsoft.EntityFrameworkCore;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Data.Entities;

namespace AzerothPlatform.Infrastructure.Data;

/// <summary>
/// Database context for Azeroth Platform
/// </summary>
public class AzerothCoreDbContext : DbContext
{
    public AzerothCoreDbContext(DbContextOptions<AzerothCoreDbContext> options)
        : base(options)
    {
    }

    public DbSet<ManagedStackEntity> ManagedStacks => Set<ManagedStackEntity>();

    public DbSet<CloudSshKeyEntity> CloudSshKeys => Set<CloudSshKeyEntity>();

    public DbSet<CloudProviderConnectionEntity> CloudProviderConnections => Set<CloudProviderConnectionEntity>();

    public DbSet<CloudAuditLogEntity> CloudAuditLogs => Set<CloudAuditLogEntity>();

    public DbSet<CatalogModuleEntity> CatalogModules => Set<CatalogModuleEntity>();

    public DbSet<StackRevisionEntity> StackRevisions => Set<StackRevisionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ManagedStackEntity>(entity =>
        {
            entity.ToTable("ManagedStacks");
            entity.HasKey(stack => stack.Id);
            entity.HasIndex(stack => stack.NormalizedStackName).IsUnique();

            entity.Property(stack => stack.Id).HasMaxLength(64);
            entity.Property(stack => stack.StackName).HasMaxLength(50).IsRequired();
            entity.Property(stack => stack.NormalizedStackName).HasMaxLength(50).IsRequired();
            entity.Property(stack => stack.ServerType).HasConversion<string>().IsRequired();
            entity.Property(stack => stack.Status).HasConversion<string>().IsRequired();
            entity.Property(stack => stack.ModuleIdsJson).IsRequired();
            entity.Property(stack => stack.DatabaseRootPassword).HasMaxLength(256).IsRequired();
            entity.Property(stack => stack.RealmName).HasMaxLength(50).IsRequired();
            entity.Property(stack => stack.ServiceEnvVarsJson).IsRequired();
            entity.Property(stack => stack.AppliedPatchesJson).IsRequired();
            entity.Property(stack => stack.ApplyingPatchKey).HasMaxLength(128);
            entity.Property(stack => stack.ApplyRunId).HasMaxLength(64);
            entity.Property(stack => stack.PostBuildAction).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(stack => stack.LauncherDisplayName).HasMaxLength(100).IsRequired();
            entity.Property(stack => stack.LauncherDescription).HasMaxLength(2000).IsRequired();
            entity.Property(stack => stack.RealmlistHostOverride).HasMaxLength(255).IsRequired();
            entity.Property(stack => stack.LauncherTemplate).HasMaxLength(32).IsRequired();
            entity.Property(stack => stack.DeploymentTarget).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(stack => stack.ExternalHost).HasMaxLength(255).IsRequired();
            entity.Property(stack => stack.ExternalSshUser).HasMaxLength(64).IsRequired();
            entity.Property(stack => stack.ExternalSshPrivateKey).IsRequired();
            entity.Property(stack => stack.CloudConnectionId).HasMaxLength(64).IsRequired();
            entity.Property(stack => stack.CloudInstanceId).HasMaxLength(128).IsRequired();
            entity.Property(stack => stack.CloudRegion).HasMaxLength(64).IsRequired();
            entity.Property(stack => stack.CloudProvider).HasMaxLength(32).IsRequired();
            entity.Property(stack => stack.CloudInstanceType).HasMaxLength(64).IsRequired();
            entity.Property(stack => stack.WizardDraftJson).IsRequired();
            entity.Property(stack => stack.WizardStepId).HasMaxLength(32).IsRequired();
        });

        modelBuilder.Entity<CloudSshKeyEntity>(entity =>
        {
            entity.ToTable("CloudSshKeys");
            entity.HasKey(key => key.Id);
            entity.Property(key => key.Id).HasMaxLength(64);
            entity.Property(key => key.Label).HasMaxLength(100).IsRequired();
            entity.Property(key => key.ProtectedPrivateKey).IsRequired();
            entity.Property(key => key.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(key => key.DefaultSshUser).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<CloudProviderConnectionEntity>(entity =>
        {
            entity.ToTable("CloudProviderConnections");
            entity.HasKey(connection => connection.Id);
            entity.Property(connection => connection.Id).HasMaxLength(64);
            entity.Property(connection => connection.Provider).HasMaxLength(32).IsRequired();
            entity.Property(connection => connection.Label).HasMaxLength(100).IsRequired();
            entity.Property(connection => connection.ProtectedCredentials).IsRequired();
            entity.Property(connection => connection.DefaultRegion).HasMaxLength(64).IsRequired();
            entity.Property(connection => connection.DefaultProjectId).HasMaxLength(64).IsRequired();
            entity.Property(connection => connection.AuthMethod).HasMaxLength(32).IsRequired();
            entity.Property(connection => connection.AccountHint).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<CloudAuditLogEntity>(entity =>
        {
            entity.ToTable("CloudAuditLogs");
            entity.HasKey(entry => entry.Id);
            entity.HasIndex(entry => entry.OccurredAtUtc);
            entity.Property(entry => entry.Id).HasMaxLength(64);
            entity.Property(entry => entry.Actor).HasMaxLength(128).IsRequired();
            entity.Property(entry => entry.EventType).HasMaxLength(64).IsRequired();
            entity.Property(entry => entry.ResourceType).HasMaxLength(32).IsRequired();
            entity.Property(entry => entry.ResourceId).HasMaxLength(64);
            entity.Property(entry => entry.Summary).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<CatalogModuleEntity>(entity =>
        {
            entity.ToTable("CatalogModules");
            entity.HasKey(module => module.Id);
            entity.Property(module => module.Id).HasMaxLength(64);
            entity.Property(module => module.SourceType).HasMaxLength(16).IsRequired();
            entity.Property(module => module.Name).HasMaxLength(100).IsRequired();
            entity.Property(module => module.Description).HasMaxLength(1000);
            entity.Property(module => module.Repository).HasMaxLength(500).IsRequired();
            entity.Property(module => module.Branch).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<StackRevisionEntity>(entity =>
        {
            entity.ToTable("StackRevisions");
            entity.HasKey(revision => revision.Id);
            entity.HasIndex(revision => revision.StackId);

            entity.Property(revision => revision.Id).HasMaxLength(64);
            entity.Property(revision => revision.StackId).HasMaxLength(64).IsRequired();
            entity.Property(revision => revision.Reason).HasMaxLength(32).IsRequired();
            entity.Property(revision => revision.Status).HasMaxLength(16).IsRequired();
            entity.Property(revision => revision.CoreCommitSha).HasMaxLength(64);
            entity.Property(revision => revision.ModuleVersionsJson).IsRequired();
            entity.Property(revision => revision.AppliedPatchesJson).IsRequired();
        });
    }
}
