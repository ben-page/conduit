namespace Conduit.Persistence;

/// <summary>
/// An index a table is created with, through
/// <see cref="ISqlDialect.BuildCreateTableIfNotExistsSql(string, string, IReadOnlyList{TableIndex})"/>.
/// </summary>
/// <param name="Name">
/// The index's name. Unique in the database's schema, not only in its table: PostgreSQL names indexes per schema.
/// </param>
/// <param name="Columns">
/// The key columns, in order. Each must have an indexable type: a text column on SQL Server or MySQL needs
/// <see cref="ISqlDialect.KeyTextType"/>, since their general text type is unbounded.
/// </param>
public sealed record TableIndex(string Name, IReadOnlyList<string> Columns);
