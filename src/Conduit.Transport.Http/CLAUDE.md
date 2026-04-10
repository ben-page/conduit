# Conduit.Transport.Http

- `HttpTransport` — outbound C#→C# request/response via HTTP POST. `UseHttp(httpClient)` registers as keyed singleton by endpoint name and calls `UseTransport<HttpTransport>()`
- `MapConduitEndpoint()` — inbound HTTP wire dispatch endpoint, maps POST `/api/conduit/dispatch` and `/api/conduit/publish` (base path configurable)
- Wire format: request body = plain serialized message JSON, type in `X-Conduit-Type` header, `MessageContext` in `X-Conduit-Message-Id`/`X-Conduit-Causation-Id`/`X-Conduit-Conversation-Id` headers. Matches the broker wire format (type in transport-native properties, body is the raw message).
- `ConduitRestHandlers` — typed handler helpers for RESTful endpoint mapping (query→route binding, command→body binding)
- RESTful extension methods (`MapConduitGet<T>`, `MapConduitPost<T>`, etc.) are source-generated into `ConduitRestEndpoints` — conditionally emitted only when `Conduit.Transport.Http` is referenced
- Maps `DispatchResult` error codes to HTTP status codes via `ErrorCode.ToHttpStatusCode()` (wire dispatch endpoint); `ConduitException` → Problem Details (RESTful endpoints)
