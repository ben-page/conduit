using System.Data;
using System.Data.Common;

namespace Conduit.Persistence;

/// <summary>
/// Helpers for adding parameters to a <see cref="DbCommand"/>, extracting the copy-pasted
/// <c>AddParameter</c> pattern shared by the persistence stores.
/// </summary>
public static class DbParameterHelper
{
    /// <summary>Adds a named parameter with the given value.</summary>
    /// <param name="command">The command to add the parameter to.</param>
    /// <param name="name">The parameter name (including its leading <c>@</c>).</param>
    /// <param name="value">The parameter value. Use <see cref="DBNull.Value"/> for SQL null.</param>
    public static void Add(DbCommand command, string name, object value)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Adds a named parameter with an explicit <see cref="DbType"/>.</summary>
    /// <param name="command">The command to add the parameter to.</param>
    /// <param name="name">The parameter name (including its leading <c>@</c>).</param>
    /// <param name="value">The parameter value. Use <see cref="DBNull.Value"/> for SQL null.</param>
    /// <param name="dbType">The parameter's database type.</param>
    public static void Add(DbCommand command, string name, object value, DbType dbType)
    {
        ArgumentNullException.ThrowIfNull(command);

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        parameter.DbType = dbType;
        command.Parameters.Add(parameter);
    }
}
