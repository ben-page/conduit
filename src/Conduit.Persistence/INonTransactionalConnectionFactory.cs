namespace Conduit.Persistence;

/// <summary>
/// Public marker for connection factories that can never enlist the handler's ambient transaction.
/// </summary>
/// <remarks>
/// The built-in connection-string-based factories (e.g. the SQLite engine package's factory) only
/// open fresh connections for relay and store sweeps — they have no visibility into the handler's
/// transaction, so they always return <see langword="null"/> from
/// <see cref="IConduitConnectionFactory.GetCurrentTransaction"/> and implement this marker. The
/// transactional outbox uses an <c>is</c> check against this marker to fail fast at startup when it
/// is enabled on a factory that cannot provide a transaction (capture would otherwise silently
/// persist nothing). The marker is public so consuming modules (durability) can perform that check.
/// </remarks>
#pragma warning disable CA1040 // Marker interface by design
public interface INonTransactionalConnectionFactory;
#pragma warning restore CA1040
