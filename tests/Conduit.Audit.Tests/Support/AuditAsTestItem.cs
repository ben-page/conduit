using System.Text.Json;
using Conduit.Attributes;

namespace Conduit.Audit.Tests.Support;

/// <summary>An envelope item: a serialized message named by <see cref="Type"/>, typed at audit capture.</summary>
public sealed record AuditAsTestItem(string Type, [AuditAs(nameof(Type))] JsonElement Payload);
