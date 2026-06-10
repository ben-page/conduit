# Conduit.Analysis (netstandard2.0)

- Target netstandard2.0 for Roslyn analyzer compatibility
- Use PolySharp NuGet for modern C# features (`required`, file-scoped namespaces, etc.)
- Contains shared type model only (`DispatchableTypeInfo`, `DispatchableKind`)
- **`DispatchableTypeInfo.FullTypeName` is the metadata full name** — nested types use the `+` separator (e.g. `Ns.Outer+Inner`), identical to the runtime `Type.FullName`. This is the single canonical key shared by the runtime wire discriminator, `ConduitTypeRegistry`, `Conduit.SourceGen.RoslynScanner`, and `Conduit.TypeGen.ReflectionScanner`. Both scanners MUST agree on this form: `ReflectionScanner` returns `type.FullName` directly; `RoslynScanner.GetMetadataFullName` reconstructs it by walking the symbol's `ContainingType` chain. `TypeName` remains the short, unqualified name. (See `Conduit.SourceGen/CLAUDE.md` for the emitter-side `+`→`.` conversion used when writing the name into C# source positions.)
- Model types are plain reference-equality `class`es (not value-equatable). The source generator is intentionally non-incremental — see the incrementality tradeoff in `Conduit.SourceGen/CLAUDE.md`.
