namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Response returned after a stack is created.
/// </summary>
public class CreateStackResponse
{
    public string StackId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}
