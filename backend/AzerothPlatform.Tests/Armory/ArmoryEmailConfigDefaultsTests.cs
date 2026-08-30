using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using Xunit;

namespace AzerothPlatform.Tests.Armory;

public sealed class ArmoryEmailConfigDefaultsTests
{
    [Fact]
    public void IsEmailConfigComplete_requires_host_from_address_and_template()
    {
        var accounts = new ArmoryAccountsConfigDto
        {
            UseEmailConfirmation = true,
            EmailConfigured = true,
            Email = new ArmoryEmailConfigDto
            {
                SmtpHost = "smtp.example.com",
                SmtpPort = 587,
                FromAddress = "noreply@example.com",
                VerificationSubject = "Verify",
                VerificationBodyHtml = "<p>{{verifyUrl}}</p>",
            },
        };

        Assert.True(ArmoryEmailConfigDefaults.IsEmailConfigComplete(accounts));
    }

    [Fact]
    public void NormalizeAccounts_clears_email_when_toggle_off()
    {
        var normalized = ArmoryEmailConfigDefaults.NormalizeAccounts(new ArmoryAccountsConfigDto
        {
            UseEmailConfirmation = false,
            EmailConfigured = true,
            Email = new ArmoryEmailConfigDto { SmtpHost = "smtp.example.com" },
        });

        Assert.False(normalized.UseEmailConfirmation);
        Assert.False(normalized.EmailConfigured);
        Assert.Null(normalized.Email);
    }
}
