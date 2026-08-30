namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Validation result for stack configuration checks.
/// </summary>
public class ValidationResultDto
{
    public bool IsValid => Errors.Count == 0;

    public List<ValidationErrorDto> Errors { get; set; } = new();

    public Dictionary<string, int> SuggestedPorts { get; set; } = new();
}
