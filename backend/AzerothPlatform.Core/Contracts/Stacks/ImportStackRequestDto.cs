using System.ComponentModel.DataAnnotations;

namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Request to import a discovered stack into the manager database
/// </summary>
public class ImportStackRequestDto
{
    /// <summary>
    /// Custom name for the imported stack
    /// </summary>
    [Required(ErrorMessage = "Stack name is required")]
    [StringLength(100, MinimumLength = 3, ErrorMessage = "Stack name must be between 3 and 100 characters")]
    [RegularExpression(@"^[a-zA-Z0-9\s\-_]+$", ErrorMessage = "Stack name can only contain letters, numbers, spaces, hyphens, and underscores")]
    public string StackName { get; set; } = string.Empty;
    
    /// <summary>
    /// Optional override for database root password (if null, will be generated)
    /// </summary>
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Database password must be at least 8 characters")]
    public string? DatabaseRootPassword { get; set; }
}
