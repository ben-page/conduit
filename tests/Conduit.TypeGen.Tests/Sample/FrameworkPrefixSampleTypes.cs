using Conduit.Attributes;
using Conduit.Messages;

// Namespaces that only LOOK like a framework's: a host's own types under ConduitApp.*, SystemsInc.* or Conduit.<its own
// name>.* (the Notes sample is Conduit.Sample.Notes) are walked like any other. Only System, System.*, Microsoft.* and
// Conduit's own namespaces are not. Each root reaches a DTO it does not mark [ClientVisible].

namespace ConduitApp.Billing
{
    public sealed record ShapeInvoice(string Number, decimal Amount);

    [ClientVisible]
    public sealed record GetShapeInvoiceQuery(string Number) : IQuery<ShapeInvoice>;
}

namespace SystemsInc.Fleet
{
    public sealed record ShapeTruck(string Plate);

    [ClientVisible]
    public sealed record ShapeDepot(string Name, IReadOnlyList<ShapeTruck> Trucks);
}

namespace Conduit.TypeGen.Tests.Sample.Shapes
{
    public sealed record ShapeShelf(string Aisle);

    [ClientVisible]
    public sealed record ShapeWarehouse(string Name, ShapeShelf Shelf);
}
