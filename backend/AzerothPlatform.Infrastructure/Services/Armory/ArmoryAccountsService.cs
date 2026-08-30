using System.Data.Common;
using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Stack-level armory account email settings: pending-registration visibility and SMTP test delivery.
/// </summary>
public sealed class ArmoryAccountsService : IArmoryAccountsService
{
    private const string PendingTable = "armory_pending_registration";
    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);

    private readonly AzerothCoreDbContext _dbContext;
    private readonly IMySqlConnectionFactory _connectionFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<ArmoryAccountsService> _logger;

    public ArmoryAccountsService(
        AzerothCoreDbContext dbContext,
        IMySqlConnectionFactory connectionFactory,
        ISecretProtector secretProtector,
        ILogger<ArmoryAccountsService> logger)
    {
        _dbContext = dbContext;
        _connectionFactory = connectionFactory;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public async Task<ArmoryAccountsStatusDto> GetStatusAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var (count, available) = await TryGetPendingCountAsync(stackId, cancellationToken);
        return new ArmoryAccountsStatusDto
        {
            PendingRegistrationCount = count,
            PendingTableAvailable = available,
        };
    }

    public async Task<int> GetPendingRegistrationCountAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var (count, _) = await TryGetPendingCountAsync(stackId, cancellationToken);
        return count;
    }

    public async Task<ArmoryTestEmailResultDto> SendTestEmailAsync(
        string stackId,
        ArmoryTestEmailRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var toAddress = request.TestEmailAddress?.Trim() ?? string.Empty;
        if (!EmailPattern.IsMatch(toAddress))
        {
            return new ArmoryTestEmailResultDto
            {
                Success = false,
                Message = "Enter a valid test email address.",
            };
        }

        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(stack => stack.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return new ArmoryTestEmailResultDto { Success = false, Message = "Stack not found." };
        }

        if (!stack.ArmoryUseEmailConfirmation)
        {
            return new ArmoryTestEmailResultDto
            {
                Success = false,
                Message = "Email confirmation is not enabled for this stack.",
            };
        }

        var email = ArmoryEmailConfigDefaults.DeserializeEmailConfig(stack.ArmoryEmailConfigJson);
        if (email is null || string.IsNullOrWhiteSpace(email.SmtpHost) || string.IsNullOrWhiteSpace(email.FromAddress))
        {
            return new ArmoryTestEmailResultDto
            {
                Success = false,
                Message = "SMTP settings are incomplete. Save host and from address first.",
            };
        }

        var smtpPassword = string.IsNullOrWhiteSpace(stack.ArmoryEmailSmtpPasswordProtected)
            ? string.Empty
            : _secretProtector.Unprotect(stack.ArmoryEmailSmtpPasswordProtected);

        var realmName = string.IsNullOrWhiteSpace(stack.RealmName) ? stack.StackName : stack.RealmName;
        var subject = RenderTemplate(
            string.IsNullOrWhiteSpace(email.VerificationSubject)
                ? ArmoryEmailConfigDefaults.DefaultVerificationSubject
                : email.VerificationSubject,
            realmName,
            "(test - no action required)");
        var body = RenderTemplate(
            string.IsNullOrWhiteSpace(email.VerificationBodyHtml)
                ? ArmoryEmailConfigDefaults.DefaultVerificationBodyHtml
                : email.VerificationBodyHtml,
            realmName,
            "(test link)");

        try
        {
            await SendSmtpAsync(email, smtpPassword, toAddress, subject, body, cancellationToken);
            _logger.LogInformation("Sent armory test email for stack {StackId} to {Address}", stackId, toAddress);
            return new ArmoryTestEmailResultDto
            {
                Success = true,
                Message = $"Test email sent to {toAddress}.",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Armory test email failed for stack {StackId}", stackId);
            return new ArmoryTestEmailResultDto
            {
                Success = false,
                Message = "Could not send the test email. Check SMTP settings and try again.",
            };
        }
    }

    private async Task<(int Count, bool Available)> TryGetPendingCountAsync(
        string stackId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "auth", cancellationToken);
            if (!await TableExistsAsync(connection, PendingTable, cancellationToken))
            {
                return (0, false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(*) FROM `{PendingTable}`
                WHERE `account_id` IS NULL
                """;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return (Convert.ToInt32(result), true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not query pending registrations for stack {StackId}", stackId);
            return (0, false);
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = @tableName
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) > 0;
    }

    private static string RenderTemplate(string template, string siteName, string verifyUrl)
        => template
            .Replace("{{verifyUrl}}", verifyUrl, StringComparison.Ordinal)
            .Replace("{{siteName}}", siteName, StringComparison.Ordinal)
            .Replace("{{expiryHours}}", "48", StringComparison.Ordinal);

    private static async Task SendSmtpAsync(
        ArmoryEmailConfigDto email,
        string smtpPassword,
        string toAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        using var message = new MailMessage
        {
            From = string.IsNullOrWhiteSpace(email.FromName)
                ? new MailAddress(email.FromAddress)
                : new MailAddress(email.FromAddress, email.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };
        message.To.Add(toAddress);

        using var client = new SmtpClient(email.SmtpHost, email.SmtpPort > 0 ? email.SmtpPort : 587);
        var security = email.SmtpSecurity?.Trim().ToLowerInvariant() ?? "starttls";
        client.EnableSsl = security is "tls" or "starttls";
        if (!string.IsNullOrWhiteSpace(email.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(email.SmtpUsername, smtpPassword);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}
