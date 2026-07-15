namespace AzerothPlatform.Core.Exceptions;

/// <summary>
/// Exception thrown when a stack import conflicts with existing data
/// </summary>
public class StackConflictException : Exception
{
    public StackConflictException(string message)
        : base(message)
    {
    }
    
    public StackConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
