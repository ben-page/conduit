# Why Conduit

- [One application, not two](#one-application-not-two)
- [What REST is for](#what-rest-is-for)
- [The cost of the boundary](#the-cost-of-the-boundary)
- [A screen is a query](#a-screen-is-a-query)
- [A button is a command](#a-button-is-a-command)
- [The slice](#the-slice)
- [The browser is a peer](#the-browser-is-a-peer)
- [REST when you want it](#rest-when-you-want-it)
- [What it looks like at scale](#what-it-looks-like-at-scale)

## One application, not two

A web application has a front half and a back half. They are written by the same team, versioned
together, deployed together, and nobody else calls the back half. Yet the usual way to connect them
is to put an API between them designed for strangers: a REST surface with a resource vocabulary of
its own, a set of DTOs that are neither the domain nor the screen, and a mapping layer to translate
in both directions.

Conduit starts from the other premise. The front-end is not an independent consumer of your
back-end. It is the other half of one application. So it should call the application directly: the
browser dispatches the same commands and queries your C# code dispatches, typed end to end, and the
handler that runs is the handler, not a controller that re-expresses it.

This page is the argument for that, with the code it produces. If you already agree, skip to
[Getting Started](getting-started.md).

## What REST is for

REST is a good design for an API you publish to people you will never meet. A stable resource
model survives any one consumer. Cacheable `GET`s work with every proxy on the internet. Uniform
verbs mean a new consumer needs no documentation to guess how deletion works. These are real
properties, and Conduit keeps REST available for exactly that audience
([REST when you want it](#rest-when-you-want-it)).

None of those properties is worth much between your own Angular page and your own handler. The
page already knows the application's vocabulary. Caching is decided per screen, not by a proxy. And
there is no third party to make the surface uniform for.

## The cost of the boundary

The independence of the front-end from the back-end is a fiction, but keeping the fiction up has a
price, and it is paid on every feature:

- **A third model.** Besides the domain and the screen there is now a resource representation that
  is neither, and someone has to decide what `GET /employees/{id}` returns for every consumer at once.
- **Mapping.** Entity to DTO, DTO to resource, resource to view model. AutoMapper exists because this
  layer is tedious enough to automate, which is the strongest evidence that it should not exist.
- **Logic in the wrong tier.** A REST update is a state diff. Deciding what the diff means, in what
  order to apply several of them, and what to check first moves into the front-end, where it cannot
  be reused by a job, a service, or a test.
- **A controller per resource** that exists to translate HTTP into method calls the application
  already has.

If you also use CQRS, the fiction stops being merely expensive and becomes contradictory. CQRS says
every read is bespoke: a query exists to serve one consumer, so its read model is shaped for that
consumer and no other. A general resource model is the opposite claim. You cannot have both, and
in practice the resource model wins and every screen over-fetches or chains calls.

## A screen is a query

Two screens never want the same model. An employee grid wants five columns. The employee page wants
thirty fields, grouped. In Conduit each is its own query with its own read model, projected straight
from the data store, and nothing is shared between them because nothing needs to be.

```csharp
// Hr.Contracts/Employees/ListEmployeesQuery.cs
[ClientVisible]
public sealed record ListEmployeesQuery(bool ActiveOnly = true)
    : IQuery<IReadOnlyList<EmployeeListItem>>;

[ClientVisible]
public sealed record EmployeeListItem(Guid Id, string Name, string Department, string Shift, bool Active);
```

```csharp
// Hr.Application/Employees/ListEmployeesQueryHandler.cs
public sealed class ListEmployeesQueryHandler(HrDbContext db)
    : IQueryHandler<ListEmployeesQuery, IReadOnlyList<EmployeeListItem>>
{
    public async Task<IReadOnlyList<EmployeeListItem>> HandleAsync(
        ListEmployeesQuery query, CancellationToken cancellationToken)
    {
        return await db.Employees
            .AsNoTracking()
            .Where(e => !query.ActiveOnly || e.Active)
            .OrderBy(e => e.Name)
            .Select(e => new EmployeeListItem(e.Id, e.Name, e.Department.Name, e.Shift.Name, e.Active))
            .ToListAsync(cancellationToken);
    }
}
```

The detail page is a second query, `GetEmployeeQuery(Guid Id) : IQuery<EmployeeDetail>`, with its
own thirty-field record. No DTO is reused, no mapper runs, and the SQL each screen issues is the
SQL that screen needs.

The browser calls the query by name. The name, the request shape and the response type are all
generated from the C# above, so the call is checked by the TypeScript compiler:

```typescript
const result = await client.fetch('Hr.Contracts.Employees.ListEmployeesQuery', { activeOnly: true });
// result.data: EmployeeListItem[]
```

Rename a field in C# and the page fails to compile. That is the whole contract.

## A button is a command

A command names what the user is trying to do, and carries only what that intent needs. Compare the
two ways to deactivate an employee.

Over REST, the front-end decides what deactivation means:

```typescript
// The page knows the rules. Nobody else can run them.
await http.patch(`/api/employees/${id}`, {
  status: 'Inactive',
  terminationDate: effectiveOn,
  badgeActive: false,
});
await http.delete(`/api/employees/${id}/shifts`);
```

With Conduit, the intent is a type, and the rules live behind it:

```csharp
// Hr.Contracts/Employees/DeactivateEmployeeCommand.cs
[ClientVisible]
public sealed record DeactivateEmployeeCommand(Guid EmployeeId, DateOnly EffectiveOn, string Reason, string ETag)
    : ICommand
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(this.Reason))
        {
            throw new BadRequestException("A reason is required.");
        }
    }
}
```

```csharp
// Hr.Application/Employees/DeactivateEmployeeCommandHandler.cs
public sealed class DeactivateEmployeeCommandHandler(IEmployeeRepository employees, IMediator mediator)
    : ICommandHandler<DeactivateEmployeeCommand>
{
    public async Task HandleAsync(DeactivateEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employee = await employees.GetAsync(command.EmployeeId, cancellationToken)
            ?? throw new NotFoundException("That employee wasn't found.");

        employees.PinConcurrency(employee, command.ETag);
        employee.Deactivate(command.EffectiveOn, command.Reason);

        await mediator.PublishAsync(new EmployeeDeactivated(employee.Id), cancellationToken);
    }
}
```

```typescript
const result = await client.execute('Hr.Contracts.Employees.DeactivateEmployeeCommand', {
  employeeId, effectiveOn, reason, eTag,
});
if (result.error?.code === 'PreconditionFailed') {
  // someone else changed the record; reload and show the diff
}
```

The page sends one message and gets one answer. What deactivation does to badges and shifts is the
handler's business, and it is the same whether the command came from the browser, from a nightly
job, or from a test.

The things that must happen to *every* write happen once, in the pipeline, instead of in every
controller or every component:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddPipeline(typeof(LoggingBehavior<,>));
    conduit.AddPipeline(typeof(PermissionBehavior<,>));   // reads [RequirePermission] off the command
    conduit.AddPipeline(typeof(TransactionBehavior<,>));  // one unit of work per dispatch
});
```

A behavior wraps commands, queries and events alike, so authorization, auditing and the unit of work
are written once and apply to the browser's dispatch and to in-process dispatch identically. See
[Pipeline Behaviors](pipeline-behaviors.md).

## The slice

Because the browser calls the command directly, a feature has no horizontal layers to cut across.
Everything one use case needs sits together, and the only thing another module may touch is the
contract:

```text
Hr/
├─ Hr.Contracts/            commands, queries, events, read models  ← the only project others reference
│  └─ Employees/
│     ├─ ListEmployeesQuery.cs
│     ├─ DeactivateEmployeeCommand.cs
│     └─ EmployeeDeactivated.cs
├─ Hr.Application/          handlers, one per message
│  └─ Employees/
│     ├─ ListEmployeesQueryHandler.cs
│     └─ DeactivateEmployeeCommandHandler.cs
├─ Hr.Domain/               the Employee aggregate and its rules
└─ Hr.Infrastructure/       EF mappings, repositories

ClientApp/src/app/hr/employees/
├─ employees.page.ts        fetch('…ListEmployeesQuery')
└─ employee.page.ts         execute('…DeactivateEmployeeCommand')
```

Reads and writes separate naturally. A query handler projects with `AsNoTracking` and never touches
an aggregate. A command handler loads an aggregate through a repository and never returns a view.
The two never share a model, so neither constrains the other.

Across modules the rule is the same as inside one: a module changes another module's data only by
sending that module's command, and reads it only through that module's queries or published read
models. The source generator discovers every handler in every referenced assembly from one
composition root, so adding a module is adding a project reference. See
[Modular Monolith](modular-monolith.md).

## The browser is a peer

The TypeScript client is generated at build time from the `[ClientVisible]` types, with no
OpenAPI step, no hand-written API service, and no npm package to version against the server:

```typescript
import { ConduitClient, SignalRTransport } from './generated/conduit.generated';

const transport = new SignalRTransport();
const client = new ConduitClient(transport);
await transport.connect();
```

Over SignalR the same connection carries events back. A handler publishes an event, the server
pushes it, and the page reacts:

```csharp
[ClientVisible]
public sealed record EmployeeDeactivated(Guid EmployeeId) : IEvent;
```

```typescript
client.on('Hr.Contracts.Employees.EmployeeDeactivated', e => grid.remove(e.employeeId));
```

A browser can only dispatch what you allow. `AddTypeFilter` names the client-dispatchable types,
`[LocalOnly]` keeps an event in the process, and ASP.NET Core's own authentication guards the hub
and the endpoint. See [Security](security.md).

## REST when you want it

Sometimes a consumer really is a stranger: a partner integration, a mobile app on its own release
cycle, a script. For them, map the same handler to a route. Nothing is duplicated; the query above
becomes an endpoint with OpenAPI, Problem Details and the full minimal-API chain:

```csharp
app.MapConduitGet<ListEmployeesQuery>("/api/employees")
    .RequireAuthorization("partners")
    .WithTags("Employees");
```

REST is a transport you add for the consumers who need it, not the shape your application is built
around. The same is true of every other transport: route a command to another service over SignalR
or a message broker, and the handler and the browser page are unchanged. See
[Transports](transports/index.md).

## What it looks like at scale

Conduit was built for, and is in production under, a manufacturing ERP being migrated one module at
a time from a desktop application with several hundred screens, while the desktop application keeps
shipping against the same database. A single Angular SPA fronts a .NET host that composes a dozen
module slices like the one above.

Every screen in that application is one or more queries with read models of their own. Every
button is a command. Authorization, validation, auditing and the unit of work are pipeline behaviors
written once. The module boundary (reads free, writes only through the owning module's commands) is
checked by architecture tests over the compiled assemblies. There is no REST layer between the SPA
and the host, no DTO mapping, and no API client to maintain by hand: the TypeScript the pages import
is regenerated on every build.

---

[Home](../README.md) | Next: [Getting Started](getting-started.md)
