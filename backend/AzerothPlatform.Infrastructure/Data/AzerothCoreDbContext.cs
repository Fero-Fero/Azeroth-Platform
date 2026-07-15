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
            entity.Property(stack => stack.CustomEnvVarsJson).IsRequired();
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
