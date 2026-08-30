namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// SMTP + template settings for armory email-verified registration on a stack.
/// </summary>
public class ArmoryEmailConfigDto
{
    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    /// <summary>none | starttls | tls</summary>
    public string SmtpSecurity { get; set; } = "starttls";

    public string SmtpUsername { get; set; } = string.Empty;

    /// <summary>Blank on read means unchanged on update (same pattern as SSH private key).</summary>
    public string SmtpPassword { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;

    public string VerificationSubject { get; set; } = string.Empty;

    public string VerificationBodyHtml { get; set; } = string.Empty;
}

/// <summary>
/// Armory player-account options configured at stack setup / in stack settings.
/// </summary>
public class ArmoryAccountsConfigDto
{
    public bool UseEmailConfirmation { get; set; }

    /// <summary>False when the operator skipped the email step or has not finished SMTP setup.</summary>
    public bool EmailConfigured { get; set; }

    public ArmoryEmailConfigDto? Email { get; set; }
}
