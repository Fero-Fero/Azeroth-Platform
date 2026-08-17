using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Infrastructure.Services.Cloud;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class CloudSshKeyServiceTests
{
    [Fact]
    public async Task DeleteUnusedKeysForStackAsync_removes_vault_key_matching_stack_private_key()
    {
        await using var db = CreateDbContext();
        var (service, protector) = CreateService(db);
        var pem = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem;
        var saved = await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = "Launch key test",
            PrivateKey = pem,
        });

        db.ManagedStacks.Add(CreateStack("stack-a", protector.Protect(pem)));
        await db.SaveChangesAsync();

        await service.DeleteUnusedKeysForStackAsync("stack-a");

        (await db.CloudSshKeys.CountAsync()).Should().Be(0);
        saved.Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DeleteUnusedKeysForStackAsync_keeps_key_still_used_by_another_stack()
    {
        await using var db = CreateDbContext();
        var (service, protector) = CreateService(db);
        var pem = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem;
        await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = "Shared launch key",
            PrivateKey = pem,
        });

        var protectedPem = protector.Protect(pem);
        db.ManagedStacks.Add(CreateStack("stack-a", protectedPem));
        db.ManagedStacks.Add(CreateStack("stack-b", protectedPem));
        await db.SaveChangesAsync();

        await service.DeleteUnusedKeysForStackAsync("stack-a");

        (await db.CloudSshKeys.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeleteUnusedKeysForStackAsync_removes_key_referenced_by_wizard_draft()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db);
        var saved = await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = "Launch key draft",
            PrivateKey = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem,
        });

        db.ManagedStacks.Add(CreateStack(
            "stack-draft",
            protectedPrivateKey: string.Empty,
            wizardDraftJson: "{\"deployment\":{\"savedSshKeyId\":\"" + saved.Id + "\"}}"));
        await db.SaveChangesAsync();

        await service.DeleteUnusedKeysForStackAsync("stack-draft");

        (await db.CloudSshKeys.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteUnusedKeysForStackAsync_keeps_unrelated_vault_keys()
    {
        await using var db = CreateDbContext();
        var (service, protector) = CreateService(db);
        var stackPem = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem;
        var otherPem = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem;
        await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = "Launch key stack",
            PrivateKey = stackPem,
        });
        var kept = await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = "Operator imported key",
            PrivateKey = otherPem,
        });

        db.ManagedStacks.Add(CreateStack("stack-a", protector.Protect(stackPem)));
        await db.SaveChangesAsync();

        await service.DeleteUnusedKeysForStackAsync("stack-a");

        var remaining = await db.CloudSshKeys.ToListAsync();
        remaining.Should().ContainSingle(key => key.Id == kept.Id);
    }

    [Fact]
    public async Task DeleteUnusedKeysForStackAsync_keeps_key_referenced_by_another_stack_draft()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db);
        var saved = await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = "Launch key shared draft",
            PrivateKey = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem,
        });

        var draft = "{\"deployment\":{\"savedSshKeyId\":\"" + saved.Id + "\"}}";
        db.ManagedStacks.Add(CreateStack("stack-a", string.Empty, draft));
        db.ManagedStacks.Add(CreateStack("stack-b", string.Empty, draft));
        await db.SaveChangesAsync();

        await service.DeleteUnusedKeysForStackAsync("stack-a");

        (await db.CloudSshKeys.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeleteUnusedKeysForStackAsync_removes_bootstrap_key_referenced_by_wizard_draft()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db);
        var bootstrap = await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = "Bootstrap key draft",
            PrivateKey = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem,
        });

        db.ManagedStacks.Add(CreateStack(
            "stack-draft",
            protectedPrivateKey: string.Empty,
            wizardDraftJson: "{\"deployment\":{\"bootstrapSshKeyId\":\"" + bootstrap.Id + "\"}}"));
        await db.SaveChangesAsync();

        await service.DeleteUnusedKeysForStackAsync("stack-draft");

        (await db.CloudSshKeys.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_hides_manager_bootstrap_keys()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db);
        var operatorKey = await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = "azp-admin test",
            PrivateKey = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem,
        });
        await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = CloudSshKeyService.ManagerOnlyLabelPrefix + " hidden",
            PrivateKey = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem,
        });

        var listed = await service.ListAsync();

        listed.Should().ContainSingle(key => key.Id == operatorKey.Id);
    }

    [Fact]
    public async Task ExportAsync_allows_manager_bootstrap_keys()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db);
        var pem = SshKeyMaterialHelper.GenerateKeyPair().PrivateKeyPem;
        var bootstrap = await service.CreateAsync(new CreateCloudSshKeyRequestDto
        {
            Label = CloudSshKeyService.ManagerOnlyLabelPrefix + " hidden",
            PrivateKey = pem,
            DefaultSshUser = "ubuntu",
        });

        var exported = await service.ExportAsync(bootstrap.Id);

        exported.PrivateKey.Should().Be(pem);
        exported.DefaultSshUser.Should().Be("ubuntu");
    }

    private static (CloudSshKeyService Service, ISecretProtector Protector) CreateService(AzerothCoreDbContext db)
    {
        var protector = new PassthroughSecretProtector();
        var audit = new Mock<ICloudAuditService>();
        audit
            .Setup(item => item.WriteAsync(It.IsAny<WriteCloudAuditLogRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return (new CloudSshKeyService(db, protector, audit.Object), protector);
    }

    private static AzerothCoreDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AzerothCoreDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new AzerothCoreDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static ManagedStackEntity CreateStack(
        string id,
        string protectedPrivateKey,
        string wizardDraftJson = "{}")
        => new()
        {
            Id = id,
            StackName = id,
            NormalizedStackName = id,
            RealmName = id,
            ModuleIdsJson = "[]",
            ServiceEnvVarsJson = "{}",
            AppliedPatchesJson = "[]",
            ExternalSshPrivateKey = protectedPrivateKey,
            WizardDraftJson = wizardDraftJson,
        };

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string? plaintext) => plaintext ?? string.Empty;

        public string Unprotect(string? protectedValue) => protectedValue ?? string.Empty;

        public bool IsProtected(string? value) => false;
    }
}
