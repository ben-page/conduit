using Conduit.Messages;

namespace Conduit.Jobs.Tests.Support;

/// <summary>The command every scheduled test job dispatches.</summary>
public sealed record RecordedJob(string Label = "job") : ICommand;
