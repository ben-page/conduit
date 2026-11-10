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

A web application has a front end and a back end. One team writes both, they are versioned and deployed together,
and nothing else calls the back end. Yet the usual way to join them is an API designed for strangers: a REST surface
with a resource vocabulary of its own, DTOs that are neither the domain nor the screen, and a mapping layer that
translates in both directions. All of it exists to keep up the idea that the front end is independent of the back
end, which was never true of one application.

Conduit starts from the other premise. The front end is the other half of the application, so it calls the
application directly: the browser dispatches the same commands and queries your C# code dispatches, typed end to end,
and the handler that runs is the handler, not a controller that re-expresses it.

This page makes that argument, with the code it produces. If you already agree, skip to
[Getting Started](getting-started.md).

## What REST is for

REST is a good design for an API you publish to people you will never meet. A stable resource model outlives any one
consumer. Cacheable `GET`s work with every proxy on the internet. Uniform verbs let a new consumer guess how deletion
works without reading anything. These are real properties, and Conduit keeps REST available for exactly that audience
([REST when you want it](#rest-when-you-want-it)).

None of them is worth much between your own page and your own handler. The page already speaks the application's
vocabulary, caching is decided per screen rather than by a proxy, and there is no third party to make the surface
uniform for. REST was never a good pattern for your own API.

## The cost of the boundary

The front end's independence is a fiction, and keeping it up is paid for on every feature:

- **A third model.** Besides the domain and the screen there is a resource representation that is neither, and someone
  has to decide what `GET /employees/{id}` returns for every consumer at once.
- **Mapping.** Entity to DTO, DTO to resource, resource to view model. AutoMapper exists because this layer is tedious
  enough to automate, which says a good deal about whether it should exist at all.
- **Logic in the wrong tier.** A resource update is a state diff. Deciding what the diff means, in what order to send
  several of them and what to check first moves into the front end, where a scheduled job, another service or a test
  cannot reuse it.
- **A controller per resource**, which exists to translate HTTP back into calls the application already has.

With CQRS and bespoke read models, the fiction is not only expensive but contradictory. Each read model is shaped for
one consumer and no other, and two screens never use the same model, so there is no shared model for a mapping layer
to protect them from. A general resource model makes the opposite claim, and in practice it wins: every screen
over-fetches, or chains calls to assemble what one query would have returned.

## A screen is a query

An employee grid wants five columns. The employee page wants thirty fields, grouped. In Conduit each is its own query
with its own read model, projected straight from the data store, and the two share nothing because nothing needs
sharing.

```csharp
// Hr.Contracts/Employees/ListEmployeesQuery.cs
namespace Hr.Contracts.Employees;

[ClientVisible]
public sealed record ListEmployeesQuery(bool ActiveOnly = true) : IQuery<IReadOnlyList<EmployeeListItem>>;

public sealed record EmployeeListItem(Guid Id, string Name, string Department, string Shift, bool Active);
```

```csharp
// Hr.Application/Employees/ListEmployeesQueryHandler.cs
namespace Hr.Application.Employees;

public sealed class ListEmployeesQueryHandler(HrDbContext dbContext)
    : IQueryHandler<ListEmployeesQuery, IReadOnlyList<EmployeeListItem>>
{
    public async Task<IReadOnlyList<EmployeeListItem>> HandleAsync(
        ListEmployeesQuery query, CancellationToken cancellationToken)
    {
        return await dbContext.Employees
            .AsNoTracking()
            .Where(employee => !query.ActiveOnly || employee.Active)
            .OrderBy(employee => employee.Name)
            .Select(employee => new EmployeeListItem(
                employee.Id, employee.Name, employee.Department.Name, employee.Shift.Name, employee.Active))
            .ToListAsync(cancellationToken);
    }
}
```

The employee page is a second query, `GetEmployeeQuery(Guid Id) : IQuery<EmployeeDetail>`, with its own thirty-field
record. No DTO is reused, no mapper runs, and each screen issues the SQL that screen needs.

The browser calls the query by its full type name. The name, the request and the response are generated from the C#
above (`EmployeeListItem` is found through the query's result type), so the TypeScript compiler checks the call:

```typescript
const result = await client.fetch('Hr.Contracts.Employees.ListEmployeesQuery', { activeOnly: true });
// result.data: EmployeeListItem[], or result.error: the server's refusal
```

Rename a property in C# and the page stops compiling. That is the whole contract. A tab loaded before a deploy is
caught on the server: a request missing a member the new contract added without a default is refused as
`ContractMismatch`, which tells the user the page is out of date, rather than run with that member silently defaulted.

## A button is a command

A command names what the user is trying to do, and carries only what that intent needs. Compare two ways to
deactivate an employee.

With resource-style REST, the page decides what deactivation means:

```typescript
// The page knows the rules, and nothing else can run them.
await http.patch(`/api/employees/${id}`, { status: 'Inactive', terminationDate: effectiveOn, badgeActive: false });
await http.delete(`/api/employees/${id}/shifts`);
```

You can model the intent in REST instead, as an action resource: `POST /api/employees/{id}/deactivation` with the
effective date and the reason. That is a remote procedure call with a route, a controller and a DTO in front of it: a
command, plus the boilerplate.

With Conduit the intent is a type, and the rules live behind it, with no controller or DTO in front:

```csharp
// Hr.Contracts/Employees/DeactivateEmployeeCommand.cs
namespace Hr.Contracts.Employees;

[ClientVisible]
public sealed record DeactivateEmployeeCommand(Guid EmployeeId, DateOnly EffectiveOn, string Reason, int Version)
    : ICommand
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            throw new BadRequestException("A reason is required.");
        }
    }
}
```

```csharp
// Hr.Application/Employees/DeactivateEmployeeCommandHandler.cs
namespace Hr.Application.Employees;

public sealed class DeactivateEmployeeCommandHandler(IEmployeeRepository employees, IMediator mediator)
    : ICommandHandler<DeactivateEmployeeCommand>
{
    public async Task HandleAsync(DeactivateEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employee = await employees.GetAsync(command.EmployeeId, cancellationToken)
            ?? throw new NotFoundException("That employee wasn't found.");

        if (employee.Version != command.Version)
        {
            throw new PreconditionFailedException("Someone else changed this employee. Reload it and try again.");
        }

        employee.Deactivate(command.EffectiveOn, command.Reason); // ends open shifts, disables the badge
        await mediator.PublishAsync(new EmployeeDeactivated(employee.Id), cancellationToken);
    }
}
```

```typescript
const result = await client.execute('Hr.Contracts.Employees.DeactivateEmployeeCommand', {
  employeeId, effectiveOn, reason, version,
});
if (result.error?.code === 'PreconditionFailed') {
  // someone else changed the record: reload it
}
```

The page sends one message and gets one answer. `Validate()` holds the checks that need no data, and the generated
mediator runs it just before the handler; whatever either of them refuses comes back as `result.error`, not as an
exception the page has to catch. What deactivation does to badges and shifts is the handler's business, and it is the
same whether the command came from the browser, a nightly job or a test.

What every dispatch needs happens once, in the pipeline, instead of in every controller or every component:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddPipeline(typeof(LoggingBehavior<,>));
    conduit.AddPipeline(typeof(PermissionBehavior<,>));  // reads a permission attribute off the message
    conduit.AddPipeline(typeof(TransactionBehavior<,>)); // one unit of work per dispatch
});
```

The three are your own `IPipelineBehavior<TRequest, TResult>` implementations, run first-registered outermost. A
behavior wraps commands, queries and events alike, so logging, authorization and the unit of work are written once and
apply the same way to the browser's dispatch as to an in-process one. See [Pipeline Behaviors](pipeline-behaviors.md).

## The slice

Because the browser calls the command directly, the layers that exist only to cross the boundary are gone: no
controller, no DTO, no mapper, no hand-written API service. A feature is a contract, a handler and a page with the
same name, and the only part another module may touch is the contract:

```text
Hr/
├─ Hr.Contracts/            commands, queries, events, read models  ← the only project other modules reference
│  └─ Employees/
│     ├─ ListEmployeesQuery.cs
│     ├─ DeactivateEmployeeCommand.cs
│     └─ EmployeeDeactivated.cs
├─ Hr.Application/          one handler per message
│  └─ Employees/
│     ├─ ListEmployeesQueryHandler.cs
│     └─ DeactivateEmployeeCommandHandler.cs
├─ Hr.Domain/               the Employee aggregate and its rules
└─ Hr.Infrastructure/       EF mappings, repositories

ClientApp/src/app/hr/employees/
├─ employees.page.ts        client.fetch('Hr.Contracts.Employees.ListEmployeesQuery', …)
└─ employee.page.ts         client.execute('Hr.Contracts.Employees.DeactivateEmployeeCommand', …)
```

Reads and writes stay apart without effort: a query handler projects with `AsNoTracking` and never loads an aggregate,
and a command handler loads one through a repository and never returns a view.

Across modules the rule is the same as within one: any module may run another's queries, but only the owning module's
commands change its data. The source generator in the host discovers the public handlers of every assembly the host
references, so adding a module is adding a project reference. See [Modular Monolith](modular-monolith.md).

## The browser is a peer

`Conduit.TypeGen` regenerates the TypeScript client on every build, from the `[ClientVisible]` types: no OpenAPI step,
no hand-written API service, and no Conduit npm package to version against the server. The host serves the
application on a SignalR hub and names the messages a browser may send. Without a type filter the hub accepts every
message the application handles.

```csharp
builder.Services.AddConduit(conduit => conduit.AddTypeFilter(filter => filter
    .AllowType("Hr.Contracts.Employees.ListEmployeesQuery")
    .AllowType("Hr.Contracts.Employees.DeactivateEmployeeCommand")));
builder.Services.AddConduitSignalR();

var app = builder.Build();
app.MapHub<ConduitHub>("/conduit").RequireAuthorization();
```

```typescript
import { ConduitClient, SignalRTransport } from './generated/conduit.generated';

const transport = new SignalRTransport(); // the hub at /conduit
const client = new ConduitClient(transport);
await transport.connect();
```

The same connection carries events back. When a handler publishes a `[ClientVisible]` event, the hub pushes it to
every connected client, and the page reacts. Register an `IEventRecipientResolver` to send events to some users or
groups instead, and mark an event `[LocalOnly]` to keep it inside the process.

```csharp
// Hr.Contracts/Employees/EmployeeDeactivated.cs
namespace Hr.Contracts.Employees;

[ClientVisible]
public sealed record EmployeeDeactivated(Guid EmployeeId) : IEvent;
```

```typescript
client.on('Hr.Contracts.Employees.EmployeeDeactivated', deactivated => grid.remove(deactivated.employeeId));
```

A page that needs no push can use `HttpTransport` instead, against the endpoint the host maps with
`app.MapConduitEndpoint()`. ASP.NET Core's own authentication and authorization guard the hub and the endpoint. See
[TypeScript Client](transports/typescript-client.md) and [Security](security.md).

## REST when you want it

Sometimes a consumer really is a stranger: a partner integration, a mobile app on its own release cycle, a script. For
them, map the same handler to a route. Nothing is duplicated: the query above becomes an endpoint with OpenAPI,
Problem Details and the whole minimal-API chain.

```csharp
app.MapConduitGet<ListEmployeesQuery>("/api/employees")
    .RequireAuthorization("partners")
    .WithTags("Employees");
```

REST is a transport you add for the consumers who need it, not the shape the application is built around.

Moving a handler to another process is the same kind of change. When the HR module becomes a service of its own, its
handlers move with it unchanged, the service maps `app.MapConduitEndpoint()`, and the host that serves the page routes
`Hr.Contracts.Employees` there:

```csharp
builder.Services.AddConduit(conduit => conduit.AddEndpoint("hr", endpoint => endpoint
    .UseHttp(new HttpClient { BaseAddress = new Uri("https://hr.internal") })
    .RouteNamespace<DeactivateEmployeeCommand>()));
```

The page still sends `DeactivateEmployeeCommand` to its own host, which forwards it. SignalR and the four message
brokers (Azure Service Bus, RabbitMQ, Amazon SQS, ActiveMQ Artemis) route the same way. See
[HTTP](transports/http.md) and [Transports](transports/index.md).

## What it looks like at scale

Conduit was built for, and runs in production under, a manufacturing ERP that is replacing a desktop application of
some 280 forms one module at a time, while the desktop application keeps shipping against the same database. One
Angular single-page application fronts one .NET host, which composes module slices like the one above.

Every screen is one or more queries with read models of their own, and every button is a command. Authorization,
validation, auditing and the unit of work are pipeline behaviors, written once. The module boundary (any module may
run another's queries; only the owning module's commands write its data) is checked by architecture tests over the
compiled assemblies. There is no REST layer between the front end and the host, no DTO mapping, and no hand-maintained
API client: the TypeScript the pages import is regenerated on every build.

---

[Home](../README.md) | Next: [Getting Started](getting-started.md)
