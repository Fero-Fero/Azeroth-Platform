namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Runs the one-time Express auto-provisioner after the first successful local build.
/// </summary>
public interface IExpressProvisionService
{
    void Enqueue(string stackId);
}
