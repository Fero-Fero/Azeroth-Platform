namespace AzerothPlatform.Core.Contracts;

/// <summary>Which checks to run when probing a remote VPC host.</summary>
public enum RemoteConnectionTestPhase
{
    /// <summary>SSH connectivity and Docker Engine / Compose availability.</summary>
    Full = 0,

    /// <summary>SSH connectivity only (step 1 of the wizard progress bar).</summary>
    SshOnly = 1,

    /// <summary>Docker Engine and Compose only — assumes SSH already succeeded.</summary>
    PrerequisitesOnly = 2,
}
