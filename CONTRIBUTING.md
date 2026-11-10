# Contributing to Conduit

Thank you for helping. This page covers building and testing the repository, the rules the code follows, and how
commits are written. Report a security vulnerability privately, as [SECURITY.md](SECURITY.md) describes, never in a
public issue.

## Prerequisites

- The .NET 10 SDK (`global.json` selects 10.0 with feature roll-forward).
- Node.js 22.12 or later, with npm: the TypeScript transport tests need it, and the root build runs `npm install` and
  `vite build` for the Notes sample (see [samples/README.md](samples/README.md)).
- Docker or Podman, for the container-backed tests below.

## Build

```bash
dotnet build          # builds everything including sample apps
```

The solution file is `Conduit.slnx`. Warnings are errors: `TreatWarningsAsErrors`, `AnalysisMode=all` and
`EnforceCodeStyleInBuild` are on for every project, so an analyzer finding or an `.editorconfig` violation fails the
build. CI builds with `--configuration Release`; check that it is clean before opening a pull request.

## Test

```bash
dotnet test                                      # runs every .NET test project
dotnet test --project tests/Conduit.Tests        # one project
(cd tests/transport-ts && npm ci && npm test)    # the TS transport: strict tsc + vitest
```

`global.json` selects Microsoft.Testing.Platform, so name a single project with `--project`.

### Container-backed tests

The broker transport projects (`Conduit.Transport.RabbitMQ.Tests`, `Conduit.Transport.ActiveMQ.Tests`,
`Conduit.Transport.AmazonSqs.Tests`, `Conduit.Transport.AzureServiceBus.Tests`) and
`Conduit.Persistence.IntegrationTests` start their brokers and databases with Testcontainers. Without a container
runtime their tests report inconclusive rather than failing. CI does not run the broker projects, so run the ones your
change touches locally.

- **Podman:** Testcontainers looks for Docker's socket. Point `DOCKER_HOST` at your Podman socket, e.g.
  `export DOCKER_HOST=unix:///run/user/$UID/podman/podman.sock` (start it with `systemctl --user start podman.socket`).
- **One broker test project at a time:** each starts its own containers, and several at once compete for the machine.
  The Azure Service Bus project, on the emulator, alone runs for several minutes, and a broker failure seen under load
  should be re-run with that project alone before it is investigated.
- `CONDUIT_REUSE_CONTAINERS=true` keeps a project's containers running between runs;
  `scripts/cleanup-test-containers.sh` removes the containers an interrupted run left behind (Podman).

### The transport test matrix

[`docs/transport-test-matrices.md`](docs/transport-test-matrices.md) defines the tests every broker transport must
have: behavior tests, the universal topology tests, transport-specific tests, and container-free per-endpoint
registration and configuration-binding tests. A new transport must implement the full matrix.

## Documentation

The guides in `docs/` are published as a documentation site built from them; [site/README.md](site/README.md) says
how to preview it locally, and how a new guide gets its place in the sidebar.

## Code style

- Explicit variable names: `serviceProvider`, not `sp`; `cancellationToken`, not `ct`.
- Line breaks for control flow: never an `if` condition and its body on the same line.
- Tests use MSTest v4 and AwesomeAssertions. No mocking libraries: tests use real types, stubs, or in-memory state.
- A test project uses the `Microsoft.NET.Sdk` SDK (not `MSTest.Sdk`) with `<PackageReference Include="MSTest" />`.
- Test directories: `Unit/` for single-class tests, `Integration/` for multi-component tests, `Support/` for test
  types (one class per file).
- Test classes are named `[Subject]Should` (`MediatorShould`, `RoutingShould`), and each test follows
  Arrange/Act/Assert with `// Arrange`, `// Act`, `// Assert` comments.
- Shared test infrastructure lives in `tests/Conduit.TestUtils/` (`TestHost`, `EventSpy<T>`, `SpyLog`,
  `TestTopologyPrefix`, ...). Every test host is disposed with `await using`, the only disposal mechanism.
- Broker transport tests derive from their transport's abstract test base (`RabbitMqTestBase`, `AsbTestBase`, ...),
  which owns the container lifecycle and the availability guard, and name their resources with `TestTopologyPrefix`.

## Commits

Commit subjects follow [Conventional Commits](https://www.conventionalcommits.org/):

```text
fix(rabbitmq): recovery-safe reply queue
feat(signalr)!: broadcast only [ClientVisible] events by default
docs(audit): describe the retention window
```

- Types: `feat`, `fix`, `refactor`, `test`, `docs`, `build`, `chore`.
- The scope names the area: a package (`http`, `signalr`, `rabbitmq`, `sourcegen`, `typegen`) or a feature (`outbox`,
  `inbox`, `jobs`, `resilience`).
- `!` after the type or scope marks a breaking change; the body says what breaks and what to do instead.
- The subject says what the change does; the body says why, and names the tests that pin it.

Add a user-visible change to the unreleased section at the top of [CHANGELOG.md](CHANGELOG.md).
