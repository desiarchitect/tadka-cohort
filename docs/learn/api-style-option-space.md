# API style option-space (Day 3)

Tadka's public API is **REST/JSON** under `/api/v1` (ADR-005). That is the fit for client-facing CRUD, not a universal law.

| Style | One line | When |
|---|---|---|
| REST/JSON | Resources over HTTP verbs | App-facing CRUD. Tadka today. |
| gRPC | Fast typed RPC | Internal calls after a service extract, not the public app. |
| GraphQL | Client picks fields | Mobile over-fetch pain. Extra cache and N+1 cost. |
| SSE / WebSockets | Server pushes | Live tracking (Day 6). SSE is one-way; WebSocket is two-way. |
| Kafka (async) | Fire an event, consumers react | Cross-service facts (Week 5). Not a request/response API. |

Cost is not only rupees: infra, 2am ops, developer learning, how the bill grows with load. REST is low on all four; you pay the others when the interaction **requires** it.

Java / Node / Go: same decision. The verb and status-code contract is the lesson, not the C# controller.

If you are not on .NET, design 3-5 endpoints on your own Day 2 repo. Do not clone all 14 Tadka routes. Stop before Redis.
