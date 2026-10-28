using System;

namespace AgentCat;

/// <summary>The failure of an <see cref="AgentCatFake"/> assertion.</summary>
/// <remarks>Test frameworks report it as a failed test, with <see cref="Exception.Message"/> as the reason.</remarks>
public sealed class AgentCatAssertionException : Exception
{
    /// <summary>Creates the exception with the reason for the failure.</summary>
    /// <param name="message">Why the assertion failed.</param>
    public AgentCatAssertionException(string message)
        : base(message)
    {
    }
}
