using Microsoft.Build.Framework;

namespace Conduit.TypeGen.Tests.Support;

public sealed class FakeBuildEngine : IBuildEngine
{
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "";

    /// <summary>Every error the task logged, so a test's failure message can say why it failed.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Every message the task logged, with its importance.</summary>
    public List<BuildMessageEventArgs> Messages { get; } = [];

    public bool BuildProjectFile(string projectFileName, string[] targetNames,
        System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => true;

    public void LogCustomEvent(CustomBuildEventArgs e) { }
    public void LogErrorEvent(BuildErrorEventArgs e) => this.Errors.Add(e.Message ?? string.Empty);
    public void LogMessageEvent(BuildMessageEventArgs e) => this.Messages.Add(e);
    public void LogWarningEvent(BuildWarningEventArgs e) { }
}
