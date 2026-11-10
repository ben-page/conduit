using System.Text.Json.Serialization;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// A class command whose JSON contract differs from its CLR shape: <see cref="ScreenName"/> is <c>screen_name</c> on
/// the wire, and <see cref="Role"/> is not on the wire at all.
/// </summary>
public class ClaimScreenNameCommand : ICommand<string>
{
    [JsonPropertyName("screen_name")]
    public string ScreenName { get; set; } = string.Empty;

    [JsonIgnore]
    public string Role { get; set; } = "reader";
}
