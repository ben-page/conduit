namespace Conduit.Messages;

/// <summary>
/// Transport-agnostic file payload — pairs a <see cref="BinaryData"/> content blob
/// (media type lives in <see cref="BinaryData.MediaType"/>) with an optional filename.
/// Used for HTTP responses that emit <c>Content-Disposition: attachment; filename=...</c>
/// and for multipart uploads that supply a filename.
/// </summary>
/// <param name="Content">The binary payload. Media type flows through <see cref="BinaryData.MediaType"/>.</param>
/// <param name="FileName">Optional filename. When set on a response, emitted as <c>Content-Disposition: attachment; filename="..."</c>.</param>
public sealed record ConduitFile(BinaryData Content, string? FileName = null);
