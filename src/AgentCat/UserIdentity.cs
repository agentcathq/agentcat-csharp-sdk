using System;
using System.Text.Json.Nodes;

namespace AgentCat;

/// <summary>Identifies the person or agent behind a session, as returned by the <c>Identify</c> hook.</summary>
public sealed class UserIdentity
{
    /// <summary>Creates an identity for the given user identifier.</summary>
    /// <param name="userId">Stable identifier of the user in the customer's own system.</param>
    /// <exception cref="ArgumentNullException"><paramref name="userId"/> is <see langword="null"/>.</exception>
    public UserIdentity(string userId)
    {
        UserId = userId ?? throw new ArgumentNullException(nameof(userId));
    }

    /// <summary>Stable identifier of the user in the customer's own system.</summary>
    public string UserId { get; }

    /// <summary>Display name of the user, if known.</summary>
    public string? UserName { get; init; }

    /// <summary>Free-form data about the user. It is sent as part of the event and passes through redaction.</summary>
    public JsonObject? UserData { get; init; }
}
