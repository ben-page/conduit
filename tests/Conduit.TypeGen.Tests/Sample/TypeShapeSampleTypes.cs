using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Conduit.Attributes;
using Conduit.Messages;

// The shapes a real host's contracts take, every one in the golden conduit.generated.ts that tests/transport-ts
// type-checks (EmittedGoldenShould). A namespace outside Conduit's own, so TypeCollector walks these the way it walks a
// host's domain types. Only the roots are [ClientVisible]; everything else is reached through them.
namespace SampleDomain.Shapes;

// --- A generic wrapper reached as two instantiations: one `PagedResult<T>` declaration, its type arguments discovered. ---

public sealed record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed record ShapeOrder(Guid Id, decimal Total);

public sealed record ShapeCustomer(Guid Id, string Name);

[ClientVisible]
public sealed record ShapeOrderBook(string Owner, PagedResult<ShapeOrder> Orders, PagedResult<ShapeCustomer> Customers);

[ClientVisible]
public sealed record ListShapeCustomersQuery(int Page) : IQuery<PagedResult<ShapeCustomer>>;

// --- [JsonPolymorphic]: System.Text.Json writes the discriminator itself, so the base and each derived type carry it. ---

// A named discriminator, string-valued. The C# property of the same name is [JsonIgnore]d, as STJ requires (a property
// named like the discriminator conflicts with its metadata), so only the attributes say the key is on the wire.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ShapeCircle), "circle")]
[JsonDerivedType(typeof(ShapeSquare), "square")]
[JsonDerivedType(typeof(ShapeBlob))]
public abstract record ShapeFigure
{
    [JsonIgnore]
    public string Kind { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;
}

public sealed record ShapeCircle(double Radius) : ShapeFigure;

public sealed record ShapeSquare(double Side) : ShapeFigure;

// Registered without a discriminator: STJ writes it with no "kind", so it carries no literal of its own.
public sealed record ShapeBlob(int Points) : ShapeFigure;

// The default discriminator name ("$type") and integer discriminators.
[JsonPolymorphic]
[JsonDerivedType(typeof(ShapeFirstStep), 1)]
[JsonDerivedType(typeof(ShapeLastStep), 2)]
public abstract record ShapeStep;

public sealed record ShapeFirstStep(string Start) : ShapeStep;

public sealed record ShapeLastStep(string End) : ShapeStep;

[ClientVisible]
public sealed record ShapeDrawing(IReadOnlyList<ShapeFigure> Figures, IReadOnlyList<ShapeStep> Steps);

// --- A generic wrapper reached only as a result: emitted as its definition, not stripped to its argument. ---

public sealed record Envelope<T>(T Payload, DateTimeOffset IssuedAt);

public sealed record ShapeReceipt(string Number);

[ClientVisible]
public sealed record GetShapeReceiptQuery(string Number) : IQuery<Envelope<ShapeReceipt>>;

// --- A void command and an event, so the golden carries a CommandMap `void` response and an EventMap. ---

[ClientVisible]
public sealed record ArchiveShapeCommand(Guid Id) : ICommand;

[ClientVisible]
public sealed record ShapeArchivedEvent(Guid Id, DateTimeOffset ArchivedAt) : IEvent;

// --- An array result: the element gets the interface. ---

public sealed record ShapeTag(string Label);

[ClientVisible]
public sealed record ListShapeTagsQuery : IQuery<ShapeTag[]>;

// An array of a generic wrapper: the wrapper's definition and its argument.
public sealed record ShapeShipment(string Carrier);

[ClientVisible]
public sealed record ListShapeShipmentPagesQuery : IQuery<PagedResult<ShapeShipment>[]>;

// A dictionary result: its VALUE type is reached, and a non-string key is still a JSON string.
public sealed record ShapeVote(int Count);

[ClientVisible]
public sealed record TallyShapeVotesQuery : IQuery<Dictionary<bool, ShapeVote>>;

// --- BCL types: every integer type and Half is a JSON number; char, Uri and TimeSpan are JSON strings; set-like and
// immutable collections are JSON arrays; a dictionary's key is always a JSON string, whatever its C# type. ---

public enum ShapeColor
{
    Red,
    Green,
}

[ClientVisible]
public sealed record ShapeScalars
{
    public ulong Sequence { get; init; }
    public ushort Port { get; init; }
    public sbyte Offset { get; init; }
    public Half Ratio { get; init; }
    public Int128 Huge { get; init; }
    public UInt128 HugeUnsigned { get; init; }
    public char Grade { get; init; }
    public Uri? Homepage { get; init; }
    public TimeSpan Elapsed { get; init; }
    public HashSet<string> Tags { get; init; } = [];
    public IReadOnlySet<Guid> Owners { get; init; } = new HashSet<Guid>();
    public ISet<int> Codes { get; init; } = new HashSet<int>();
    public Collection<string> Notes { get; init; } = [];
    public ObservableCollection<ShapeTag> Labels { get; init; } = [];
    public ImmutableArray<string> Frozen { get; init; } = [];
    public ImmutableList<int> Scores { get; init; } = [];
    public ImmutableHashSet<string> Keys { get; init; } = [];
    public SortedDictionary<string, int> Ranks { get; init; } = [];
    public ConcurrentDictionary<string, string> Cache { get; init; } = new();
    public Dictionary<int, string> Names { get; init; } = [];
    public Dictionary<bool, int> Votes { get; init; } = [];
    public Dictionary<ShapeColor, string> Swatches { get; init; } = [];
}

// --- System.Text.Json marks, honored by ConduitRuntime.JsonOptions on the wire. ---

public enum ShapeStatus
{
    [JsonStringEnumMemberName("in-progress")]
    InProgress,
    Done,
}

[Flags]
public enum ShapePermissions
{
    None = 0,
    Read = 1,
    Write = 2,
}

[ClientVisible]
public sealed record ShapeJsonMarks
{
    [JsonPropertyName("order_no")]
    public string OrderNumber { get; init; } = string.Empty;

    [JsonPropertyName("x-trace")]
    public string Trace { get; init; } = string.Empty;

    [JsonIgnore]
    public string Secret { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int Revision { get; init; }

    public ShapeStatus Status { get; init; }

    public ShapePermissions Permissions { get; init; }

    // Never on the wire, so never walked: ShapeAuditTrail is reached by nothing else and gets no interface.
    [JsonIgnore]
    public ShapeAuditTrail? Audit { get; init; }

    // On the wire for some values, so walked: ShapeRemark gets an interface.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShapeRemark? Remark { get; init; }
}

public sealed record ShapeAuditTrail(string Who, IReadOnlyList<ShapeAuditTrail> Earlier);

public sealed record ShapeRemark(string Text);
