# Modular Monolith Composition

- [Overview](#overview)
- [The rule: one analyzer in the composition root](#the-rule-one-analyzer-in-the-composition-root)
- [Project layout](#project-layout)
- [Handlers must be public](#handlers-must-be-public)
- [Diagnostics](#diagnostics)
- [Why only one analyzer](#why-only-one-analyzer)

## Overview

In a modular monolith, command/query handlers and event listeners are spread across
several module assemblies (for example `Orders.Application`, `Sales.Application`,
`Hr.Application`), but the application runs as a single process with one dispatch
pipeline. Conduit's source generator discovers handlers and listeners across **every
referenced assembly**, not just the assembly being compiled. This lets one
composition-root assembly emit a single `AddConduit()` that wires up the handlers owned
by all of the modules — each dispatched through the full pipeline (validation,
authorization, transactions, audit, and so on).

## The rule: one analyzer in the composition root

Reference the `Conduit.NET.SourceGen` package from **exactly one** assembly: the composition root
that references every module's handler assembly. That is
usually the host (`Web`/`Api`) project, or a dedicated thin `Bootstrap` assembly that
references all the module `*.Application` projects.

The generator on that assembly walks the whole compilation — including the referenced
module assemblies — discovers their handlers and listeners, and emits one
`ConduitRegistration` (DI), one `ConduitMediator` (dispatch), and the matching pipeline
wiring. Calling the generated `AddConduit()` at startup registers and dispatches every
module's handlers.

## Project layout

```text
Web (composition root)
 ├─ PackageReference  Conduit.NET            1.0.0-preview.1
 ├─ PackageReference  Conduit.NET.SourceGen  1.0.0-preview.1   (the one generator)
 ├─ ProjectReference  Orders.Application   (handlers: references Conduit.NET, not Conduit.NET.SourceGen)
 ├─ ProjectReference  Sales.Application    (handlers: references Conduit.NET, not Conduit.NET.SourceGen)
 └─ ProjectReference  Hr.Application       (handlers: references Conduit.NET, not Conduit.NET.SourceGen)

Orders.Contracts, Sales.Contracts, ...      (messages only: reference Conduit.NET.Contracts)
```

```xml
<!-- Web.csproj, the composition root -->
<PackageReference Include="Conduit.NET" Version="1.0.0-preview.1" />
<PackageReference Include="Conduit.NET.SourceGen" Version="1.0.0-preview.1" />

<!-- Orders.Application.csproj, a module with handlers -->
<PackageReference Include="Conduit.NET" Version="1.0.0-preview.1" />

<!-- Orders.Contracts.csproj, a library that only declares messages -->
<PackageReference Include="Conduit.NET.Contracts" Version="1.0.0-preview.1" />
```

A module assembly references the `Conduit.NET` runtime package, which holds the handler interfaces
(`ICommandHandler`, `IQueryHandler`, `IEventListener`). An assembly that only declares messages
(`ICommand`, `IQuery`, `IEvent`, the attributes and the exceptions) can reference `Conduit.NET.Contracts`
instead, which has no package dependencies, so a domain or contracts library pulls in nothing else.
Neither references `Conduit.NET.SourceGen`: it is a development dependency, so it does not flow to the
projects that reference them either. The host's generated registration is what gets called at
startup:

```csharp
using Conduit.Generated; // AddConduit() is generated into this namespace in the host
builder.Services.AddConduit();
```

## Handlers must be public

Cross-assembly discovery is gated by accessibility: the composition-root assembly can only
reference a handler that is `public`. A handler that is `internal` (or otherwise not
accessible) in a referenced module assembly will **not** be auto-registered. Make every
command/query handler and event listener that should be dispatched `public`.

(Handlers in the *same* assembly as the analyzer may still be `internal` — they are
accessible to the generated code there. The `public` requirement only applies to handlers
discovered across an assembly boundary.)

## Diagnostics

- **CONDUIT004 — Handler not accessible.** Reported when a command or query has a handler
  in a referenced assembly that is not accessible to the generating assembly (for example,
  an `internal` handler). The contract would otherwise silently have no registered handler.
  Fix it by making the handler `public`.

## Why only one analyzer

If more than one assembly in the dependency graph holds the analyzer, each generates its
own `AddConduit()`/registration over its own (overlapping) view of the handlers, which
leads to duplicate registrations. Keep the analyzer in a single composition-root assembly
and have the module assemblies carry handlers only.
