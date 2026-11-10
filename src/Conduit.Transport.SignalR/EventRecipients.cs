namespace Conduit.Transport.SignalR;

/// <summary>
/// The SignalR connections an event is pushed to, as an <see cref="IEventRecipientResolver"/> answers:
/// <see cref="All"/> (<c>Clients.All</c>), <see cref="None"/>, <see cref="Users"/> (<c>Clients.Users</c>, by the
/// connection's user identifier, which the host's <c>IUserIdProvider</c> supplies) or <see cref="Groups"/>
/// (<c>Clients.Groups</c>). The default value is <see cref="None"/>. Two values are equal when they name the same
/// recipients in the same order.
/// </summary>
public readonly record struct EventRecipients
{
    private readonly string[]? names;

    private EventRecipients(RecipientKind kind, string[] names)
    {
        this.Kind = kind;
        this.names = names;
    }

    /// <summary>Every connection to the hub (on this server, or on every server through a backplane).</summary>
    public static EventRecipients All { get; } = new(RecipientKind.All, []);

    /// <summary>No connection: the event is not pushed.</summary>
    public static EventRecipients None => default;

    internal RecipientKind Kind { get; }

    internal IReadOnlyList<string> Names => this.names ?? [];

    /// <summary>Every connection of each of <paramref name="userIds"/>. An empty list pushes the event to nobody.</summary>
    /// <param name="userIds">The user identifiers, as the host's <c>IUserIdProvider</c> names a connection's user.</param>
    /// <returns>The recipients.</returns>
    /// <exception cref="ArgumentException">An identifier is null or empty.</exception>
    public static EventRecipients Users(IReadOnlyList<string> userIds)
    {
        return new(RecipientKind.Users, Copy(userIds, nameof(userIds)));
    }

    /// <summary>Every connection in each of <paramref name="groupNames"/>. An empty list pushes the event to nobody.</summary>
    /// <param name="groupNames">The SignalR group names (<c>Groups.AddToGroupAsync</c>).</param>
    /// <returns>The recipients.</returns>
    /// <exception cref="ArgumentException">A group name is null or empty.</exception>
    public static EventRecipients Groups(IReadOnlyList<string> groupNames)
    {
        return new(RecipientKind.Groups, Copy(groupNames, nameof(groupNames)));
    }

    /// <inheritdoc />
    public bool Equals(EventRecipients other)
    {
        return this.Kind == other.Kind && this.Names.SequenceEqual(other.Names, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(this.Kind);
        foreach (var name in this.Names)
        {
            hash.Add(name, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Kind switch
        {
            RecipientKind.All => "All",
            RecipientKind.Users => $"Users({string.Join(", ", this.Names)})",
            RecipientKind.Groups => $"Groups({string.Join(", ", this.Names)})",
            RecipientKind.None => "None",
            _ => "None",
        };
    }

    // Copied, so a list the caller changes afterwards does not change who receives the event.
    private static string[] Copy(IReadOnlyList<string> names, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(names, parameterName);
        var copy = names.ToArray();
        if (Array.Exists(copy, string.IsNullOrEmpty))
        {
            throw new ArgumentException("A recipient name must not be null or empty.", parameterName);
        }

        return copy;
    }

    internal enum RecipientKind
    {
        None,
        All,
        Users,
        Groups,
    }
}
