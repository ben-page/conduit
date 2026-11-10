using Microsoft.Extensions.Options;

namespace Conduit.Audit.Configuration;

/// <summary>
/// Runs <see cref="AuditOptions"/>' rules on the bound <c>IOptions&lt;AuditOptions&gt;</c>, so a value a host sets
/// itself (<c>services.Configure&lt;AuditOptions&gt;</c> after <c>AddAudit</c>) is refused as the host starts
/// (<c>ValidateOnStart</c>), as a value set in the <c>AddAudit</c>/<c>AddAuditListener</c> callback is refused there.
/// </summary>
internal sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = options.Failures();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
