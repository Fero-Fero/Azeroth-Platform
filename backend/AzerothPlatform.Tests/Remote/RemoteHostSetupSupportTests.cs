using AzerothPlatform.Infrastructure.Services.RemoteHost;
using Xunit;

namespace AzerothPlatform.Tests.Remote;

public sealed class RemoteHostSetupSupportTests
{
    [Fact]
    public void FormatError_IgnoresPowerShellProgressCliXml()
    {
        const string cliXml = """
            #< CLIXML
            <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><Obj S="progress" RefId="0"><TN RefId="0"><T>System.Management.Automation.PSCustomObject</T><T>System.Object</T></TN><MS><I64 N="SourceId">1</I64><PR N="Record"><AV>Preparing modules for first use.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC><T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj></Objs>
            """;

        var message = RemoteHostSetupSupport.FormatError(cliXml, "AZP_DOCKER_FAILED\nWSL MSI download failed");

        Assert.DoesNotContain("CLIXML", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Preparing modules", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WSL MSI download failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatError_ExtractsCliXmlErrorRecords()
    {
        const string cliXml = """
            #< CLIXML
            <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><S N="Error">Access is denied._x000A_</S></Objs>
            """;

        var message = RemoteHostSetupSupport.FormatError(cliXml, string.Empty);

        Assert.Contains("Access is denied.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIXML", message, StringComparison.OrdinalIgnoreCase);
    }
}
