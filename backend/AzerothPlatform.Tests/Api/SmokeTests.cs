using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzerothPlatform.Tests.Api;

/// <summary>
/// Smoke tests that verify the core API surface is functional end-to-end using an
/// in-process ASP.NET Core test host backed by an in-memory SQLite database and
/// mocked external dependencies (Docker, Git).
///
/// The test class implements <see cref="IAsyncLifetime"/> so the database is wiped
/// before every test method, guaranteeing full isolation even though all tests
/// share a single <see cref="AzerothPlatformWebApplicationFactory"/> via
/// <see cref="IClassFixture{TFixture}"/>.
/// </summary>
public sealed class SmokeTests : IClassFixture<AzerothPlatformWebApplicationFactory>, IAsyncLifetime
{
    // The server serialises enums as strings (JsonStringEnumConverter is registered
    // in Program.cs).  The test client must use the same convention when
    // deserialising responses.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AzerothPlatformWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SmokeTests(AzerothPlatformWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ------------------------------------------------------------------
    // IAsyncLifetime - wipe DB rows before every test for full isolation
    // ------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        db.ManagedStacks.RemoveRange(db.ManagedStacks);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------
    // 1. Health endpoint
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetHealth_Returns200WithStatusField()
    {
        // When
        var response = await _client.GetAsync("/api/health");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(
            doc.RootElement.TryGetProperty("status", out _),
            $"Expected response body to contain a 'status' field, but got: {body}");
    }

    // ------------------------------------------------------------------
    // 2. Stacks list - empty on a fresh database
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStacks_Returns200WithEmptyArrayInitially()
    {
        // When
        var response = await _client.GetAsync("/api/stacks");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(
            doc.RootElement.GetArrayLength() == 0,
            $"Expected an empty array from GET /api/stacks on a fresh database, but got: {body}");
    }

    // ------------------------------------------------------------------
    // 3. Modules catalogue
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetModules_Returns200WithJsonArray()
    {
        // When
        var response = await _client.GetAsync("/api/modules");

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(
            doc.RootElement.ValueKind == JsonValueKind.Array,
            $"Expected GET /api/modules to return a JSON array, but got: {body}");
    }

    // ------------------------------------------------------------------
    // 4. Validate - valid minimal configuration → isValid: true
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostValidate_WithValidMinimalConfig_ReturnsIsValidTrue()
    {
        // Given - a complete, valid minimal configuration
        var config = BuildValidConfig("smoke-valid");

        // When
        var response = await _client.PostAsJsonAsync("/api/stacks/validate", config, JsonOptions);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ValidationResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.True(
            result.IsValid,
            $"Expected validation to succeed for a valid minimal config, but got errors: " +
            string.Join("; ", result.Errors.Select(e => $"[{e.Field}] {e.Message}")));
    }

    // ------------------------------------------------------------------
    // 5. Validate - empty name → isValid: false
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostValidate_WithEmptyStackName_ReturnsIsValidFalse()
    {
        // Given - configuration with an empty stack name
        var config = BuildValidConfig("");

        // When
        var response = await _client.PostAsJsonAsync("/api/stacks/validate", config, JsonOptions);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ValidationResultDto>(JsonOptions);
        Assert.NotNull(result);
        Assert.False(
            result.IsValid,
            "Expected validation to fail when stack name is empty, but IsValid was true.");

        Assert.True(
            result.Errors.Any(e => e.Field == "stackName"),
            $"Expected a validation error on 'stackName', but got: " +
            string.Join("; ", result.Errors.Select(e => $"[{e.Field}] {e.Message}")));
    }

    // ------------------------------------------------------------------
    // 6. Full CRUD flow: POST → GET by id → DELETE → GET returns 404
    // ------------------------------------------------------------------

    [Fact]
    public async Task FullCrudFlow_CreateGetDeleteNotFound()
    {
        // ---- Create ----
        var config = BuildValidConfig("smoke-crud");
        var createResponse = await _client.PostAsJsonAsync("/api/stacks", config, JsonOptions);

        Assert.True(
            createResponse.StatusCode == HttpStatusCode.Created,
            $"POST /api/stacks should return 201 Created. Body: {await createResponse.Content.ReadAsStringAsync()}");

        var created = await createResponse.Content.ReadFromJsonAsync<CreateStackResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.False(
            string.IsNullOrEmpty(created.StackId),
            "Expected a non-empty StackId in the create response.");

        var stackId = created.StackId;

        // ---- GET by id ----
        var getResponse = await _client.GetAsync($"/api/stacks/{stackId}");
        Assert.True(
            getResponse.StatusCode == HttpStatusCode.OK,
            $"GET /api/stacks/{{stackId}} should return 200 after creation. Body: {await getResponse.Content.ReadAsStringAsync()}");

        var stackDetails = await getResponse.Content.ReadFromJsonAsync<StackDetailsDto>(JsonOptions);
        Assert.NotNull(stackDetails);
        Assert.Equal(stackId, stackDetails.StackId);

        // ---- DELETE ----
        var deleteResponse = await _client.DeleteAsync($"/api/stacks/{stackId}");
        Assert.True(
            deleteResponse.StatusCode == HttpStatusCode.NoContent,
            $"DELETE /api/stacks/{{stackId}} should return 204 No Content. Body: {await deleteResponse.Content.ReadAsStringAsync()}");

        // ---- GET after delete → 404 ----
        var getAfterDeleteResponse = await _client.GetAsync($"/api/stacks/{stackId}");
        Assert.True(
            getAfterDeleteResponse.StatusCode == HttpStatusCode.NotFound,
            "GET /api/stacks/{stackId} should return 404 Not Found after deletion.");
    }

    // ------------------------------------------------------------------
    // 7. GET non-existent stack → 404
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetStack_WithNonExistentId_Returns404()
    {
        // Given
        var nonExistentId = "00000000000000000000000000000000";

        // When
        var response = await _client.GetAsync($"/api/stacks/{nonExistentId}");

        // Then
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"GET /api/stacks/{{nonExistentId}} should return 404 for an ID that does not exist.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a fully populated, valid StackConfigurationDto for smoke tests.
    /// </summary>
    private static StackConfigurationDto BuildValidConfig(string stackName) => new()
    {
        StackName = stackName,
        ServerType = ServerType.Standard,
        ModuleIds = new List<string>(),
        Database = new DatabaseConfigDto
        {
            RootPassword = "SuperSecure123",
            Port = 13306
        },
        Ports = new PortConfigDto
        {
            AuthServer = 13724,
            WorldServer = 18085,
            SoapPort = 17878
        },
        Advanced = new AdvancedConfigDto
        {
            RealmName = "SmokeTestRealm",
            MaxPlayers = 100
        }
    };
}
