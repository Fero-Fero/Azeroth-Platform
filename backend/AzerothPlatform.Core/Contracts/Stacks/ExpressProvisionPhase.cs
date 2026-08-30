namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Checkpoint inside Express Setup. On failure the provisioner retries from this step.
/// </summary>
public enum ExpressProvisionPhase
{
    None,
    SaveChoices,
    DisableBots,
    StartStack,
    SoapDbc,
    AhBot,
    GameAccount,
    StopStack,
    WaitClient,
    SwpSync,
    EnableBots,
    Launcher,
    Addons,
    Done
}
