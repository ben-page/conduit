using System.Text.Json.Serialization;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>A positional record whose <see cref="Role"/> parameter binds a member that is not on the wire.</summary>
public record ClaimScreenNamePositionallyCommand(string ScreenName, [property: JsonIgnore] string Role = "reader")
    : ICommand<string>;
