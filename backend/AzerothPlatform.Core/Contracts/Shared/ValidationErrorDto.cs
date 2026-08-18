namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Validation error details for a configuration field.
/// </summary>
public class ValidationErrorDto
{
    public string Field { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
