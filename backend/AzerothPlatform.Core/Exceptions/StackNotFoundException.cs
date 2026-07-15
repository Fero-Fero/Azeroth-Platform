namespace AzerothPlatform.Core.Exceptions;

/// <summary>
/// Exception thrown when a stack cannot be found
/// </summary>
public class StackNotFoundException : Exception
{
    public StackNotFoundException(string stackId)
        : base($"Stack with ID '{stackId}' was not found or is orphaned (no containers)")
    {
    }
    
    public StackNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
