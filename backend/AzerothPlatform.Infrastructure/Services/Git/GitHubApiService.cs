using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Service for interacting with GitHub API
/// </summary>
public sealed class GitHubApiService : IGitHubApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubApiService> _logger;
    private readonly GitHubOptions _options;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GitHubApiService(
        IHttpClientFactory httpClientFactory, 
        ILogger<GitHubApiService> logger,
        IOptions<GitHubOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient("GitHubApi");
        _httpClient.BaseAddress = new Uri("https://api.github.com/");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AzerothPlatform", "1.0"));
        
        _logger = logger;
        _options = options.Value;
    }

    public async Task<CiBuildStatusDto> GetCommitBuildStatusAsync(
        string repository, 
        string commitSha, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching CI status for {Repository} @ {CommitSha}", repository, commitSha);

            var url = $"repos/{repository}/commits/{commitSha}/check-runs";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub API returned {StatusCode} for {Url}", response.StatusCode, url);
                return new CiBuildStatusDto
                {
                    Status = "unknown",
                    CheckedAt = DateTime.UtcNow
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var checkRunsResponse = JsonSerializer.Deserialize<GitHubCheckRunsResponse>(content, JsonOptions);

            if (checkRunsResponse == null || checkRunsResponse.CheckRuns.Count == 0)
            {
                _logger.LogInformation("No check runs found for {Repository} @ {CommitSha}", repository, commitSha);
                return new CiBuildStatusDto
                {
                    Status = "unknown",
                    CheckedAt = DateTime.UtcNow
                };
            }

            return AnalyzeCheckRuns(checkRunsResponse.CheckRuns);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch CI status for {Repository} @ {CommitSha}", repository, commitSha);
            return new CiBuildStatusDto
            {
                Status = "unknown",
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    public async Task<string?> GetLatestCommitShaAsync(
        string repository, 
        string branch, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching latest commit SHA for {Repository}/{Branch}", repository, branch);

            var url = $"repos/{repository}/commits/{branch}";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub API returned {StatusCode} for {Url}", response.StatusCode, url);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var commit = JsonSerializer.Deserialize<GitHubCommitResponse>(content, JsonOptions);

            return commit?.Sha;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch latest commit SHA for {Repository}/{Branch}", repository, branch);
            return null;
        }
    }

    private CiBuildStatusDto AnalyzeCheckRuns(List<GitHubCheckRun> checkRuns)
    {
        // Filter to critical workflows only
        var criticalWorkflows = checkRuns
            .Where(cr => _options.CriticalWorkflows.Any(cw => 
                cr.Name.Contains(cw, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // If no critical workflows found, consider all checks
        var checksToAnalyze = criticalWorkflows.Count > 0 ? criticalWorkflows : checkRuns;

        var criticalChecks = checksToAnalyze
            .GroupBy(cr => cr.Name)
            .Select(g => g.First()) // Take first of each unique name (deduplicate)
            .Select(cr => new CiCheckDto
            {
                Name = cr.Name,
                Status = cr.Status,
                Conclusion = cr.Conclusion,
                HtmlUrl = cr.HtmlUrl
            })
            .ToList();

        var totalChecks = criticalChecks.Count;
        var completedChecks = criticalChecks.Count(c => c.Status == "completed");
        var passedChecks = criticalChecks.Count(c => c.Status == "completed" && c.Conclusion == "success");
        var failedChecks = criticalChecks.Count(c => c.Status == "completed" && 
            (c.Conclusion == "failure" || c.Conclusion == "timed_out" || c.Conclusion == "action_required"));

        // Determine overall status
        string overallStatus;
        if (completedChecks < totalChecks)
        {
            overallStatus = "pending";
        }
        else if (failedChecks > 0)
        {
            overallStatus = "failure";
        }
        else if (passedChecks == completedChecks && completedChecks > 0)
        {
            overallStatus = "success";
        }
        else
        {
            overallStatus = "unknown";
        }

        return new CiBuildStatusDto
        {
            Status = overallStatus,
            CriticalChecks = criticalChecks,
            CheckedAt = DateTime.UtcNow,
            TotalChecks = totalChecks,
            PassedChecks = passedChecks,
            FailedChecks = failedChecks
        };
    }

    // GitHub API response models
    private sealed class GitHubCheckRunsResponse
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("check_runs")]
        public List<GitHubCheckRun> CheckRuns { get; set; } = new();
    }

    private sealed class GitHubCheckRun
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }

    private sealed class GitHubCommitResponse
    {
        [JsonPropertyName("sha")]
        public string Sha { get; set; } = string.Empty;
    }
}
