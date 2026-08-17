using System.Text.Json;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Defaults and persistence helpers for per-stack armory email confirmation settings.
/// </summary>
internal static class ArmoryEmailConfigDefaults
{
    public const string DefaultVerificationSubject = "Verify your account";

    public const string DefaultVerificationBodyHtml = """
        <p>Thanks for signing up on {{siteName}}.</p>
        <p><a href="{{verifyUrl}}">Verify your email address</a> to finish creating your account.</p>
        <p>This link expires in {{expiryHours}} hours.</p>
        """;

    public static ArmoryEmailConfigDto CreateDefaultEmailTemplate(string fromName = "")
        => new()
        {
            SmtpPort = 587,
            SmtpSecurity = "starttls",
            FromName = fromName,
            VerificationSubject = DefaultVerificationSubject,
            VerificationBodyHtml = DefaultVerificationBodyHtml,
        };

    public static ArmoryAccountsConfigDto NormalizeAccounts(ArmoryAccountsConfigDto? accounts)
    {
        accounts ??= new ArmoryAccountsConfigDto();
        if (!accounts.UseEmailConfirmation)
        {
            return new ArmoryAccountsConfigDto
            {
                UseEmailConfirmation = false,
                EmailConfigured = false,
                Email = null,
            };
        }

        accounts.Email ??= CreateDefaultEmailTemplate();
        if (string.IsNullOrWhiteSpace(accounts.Email.VerificationSubject))
        {
            accounts.Email.VerificationSubject = DefaultVerificationSubject;
        }

        if (string.IsNullOrWhiteSpace(accounts.Email.VerificationBodyHtml))
        {
            accounts.Email.VerificationBodyHtml = DefaultVerificationBodyHtml;
        }

        if (accounts.Email.SmtpPort <= 0)
        {
            accounts.Email.SmtpPort = 587;
        }

        if (string.IsNullOrWhiteSpace(accounts.Email.SmtpSecurity))
        {
            accounts.Email.SmtpSecurity = "starttls";
        }

        return accounts;
    }

    public static bool IsEmailConfigComplete(ArmoryAccountsConfigDto accounts)
    {
        if (!accounts.UseEmailConfirmation || !accounts.EmailConfigured || accounts.Email is null)
        {
            return false;
        }

        var email = accounts.Email;
        return !string.IsNullOrWhiteSpace(email.SmtpHost)
               && email.SmtpPort > 0
               && !string.IsNullOrWhiteSpace(email.FromAddress)
               && !string.IsNullOrWhiteSpace(email.VerificationSubject)
               && !string.IsNullOrWhiteSpace(email.VerificationBodyHtml);
    }

    public static string SerializeEmailConfig(ArmoryEmailConfigDto email)
        => JsonSerializer.Serialize(email);

    public static ArmoryEmailConfigDto? DeserializeEmailConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ArmoryEmailConfigDto>(json);
        }
        catch
        {
            return null;
        }
    }

    public static ArmoryEmailConfigDto ToPublicDto(ArmoryEmailConfigDto source)
        => new()
        {
            SmtpHost = source.SmtpHost,
            SmtpPort = source.SmtpPort,
            SmtpSecurity = source.SmtpSecurity,
            SmtpUsername = source.SmtpUsername,
            SmtpPassword = string.Empty,
            FromAddress = source.FromAddress,
            FromName = source.FromName,
            VerificationSubject = source.VerificationSubject,
            VerificationBodyHtml = source.VerificationBodyHtml,
        };
}
