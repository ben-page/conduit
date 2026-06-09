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

Reference the `Conduit.SourceGen` (and `Conduit.Analysis`) analyzers from **exactly one**
assembly: the composition root that references every module's handler assembly. That is
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
 ├─ <PackageReference Include="Conduit.SourceGen" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
 ├─ <PackageReference Include="Conduit.Analysis"  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
 ├─ ProjectReference -> Orders.Application   (handlers, NO analyzer)
 ├─ ProjectReference -> Sales.Application    (handlers, NO analyzer)
 └─ ProjectReference -> Hr.Application        (handlers, NO analyzer)
```

The module assemblies (`Orders.Application`, etc.) reference only the `Conduit` runtime
package for the `ICommand`/`IQuery`/`IEvent` and `ICommandHandler`/`IQueryHandler`/`IEventListener`
types. They do **not** reference the analyzers. The host's generated registration is what
gets called at startup:

```csharp
using Conduit.Generated; // the host's own generated namespace
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
