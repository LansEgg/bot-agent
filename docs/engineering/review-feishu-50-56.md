# Feishu review remediation: #50 / #56

## Objective and scope

Fix Feishu numeric bridge identity persistence and token hot-configuration isolation. User approved isolating existing legacy sessions: preserve old data, never infer native bindings or automatically inherit history. Changes are limited to Feishu, the reusable ID map, an independent probe and this document. CompositionRoot wiring belongs to the parent agent; no production data, server, commit or push operations.

## Current architecture and impact

- FeishuBotGateway emits both structured InboundMessage and legacy QqChatMessage. The active numeric bridge currently hashes only raw IDs into an in-memory map (32 bits) shared by group/user/message IDs; restart loses reverse routing.
- ConversationIdCodec already represents platform/account/kind/native target. Reuse that vocabulary, without changing conversation storage or settings.
- OfficialIdMap already owns locking, bidirectional lookup and atomic JSON replacement. Reuse it with opt-in strict, synchronous persistence and a distinct Feishu alias range; keep legacy official behavior unchanged.
- SettingsBox publishes immutable-by-convention settings references. Feishu currently reads Current repeatedly during token refresh/send; token and endpoint can cross configurations.

## Contracts and data flow

1. FeishuIdMap wraps the reusable map. Keys contain version/platform/app identity/kind/native ID. Group, participant (also private destination) and message kinds remain distinct. New aliases occupy FeishuBase + 1e12 through + 2e12, disjoint from old 32-bit IDs. Atomic persistence must succeed before a new binding is exposed; corrupt/ambiguous maps fail closed. No legacy import or alias fallback.
2. Gateway accepts an injected FeishuIdMap. Parent wiring supplies data/feishu-ids-v2.json; existing constructor remains source-compatible for pure in-memory probes. Persisted reverse lookup checks current app identity and expected kind before sending.
3. Each send captures app ID, secret and normalized API base once. An immutable cache entry includes that complete credential key, token and expiry. Semaphore serializes refresh; no torn fast-path reads. Token request and message request use the same captured configuration, even if hot reload occurs while waiting/in flight.

## Milestones

1. Independent FeishuRemediationProbe using synthetic settings, fake IHttpFetcher and temporary directories. First reproduce token invalidation; then identity persistence/isolation.
2. Extend OfficialIdMap only via optional strict/durable behavior. Add FeishuIdMap wrapper; use typed bindings in webhook, whitelist, reverse route and outbox.
3. Replace mutable token fields with snapshot-keyed immutable entry. Preserve cancellation and existing controlled failure results.
4. Verify restart/reversed arrival, kind/account isolation, legacy alias rejection, parallel allocation, immediate persistence and damaged map rejection. Verify app ID/secret/API base changes, cleared credentials, refresh concurrency and old/new in-flight sends.
5. Send parent exact CompositionRoot injection patch; run probe and relevant existing regression/build checks, then document observed output.

## Risks and mitigations

- Numeric IDs cannot mathematically encode arbitrary identities without collisions. Use wide SHA-256-derived candidates, persisted collision detection and fail-closed collision handling rather than silently reassigning another identity; do not claim impossible collision-free mapping.
- An existing corrupt identity file fails closed during startup and is retained for explicit operator recovery. A missing file is opened as a fresh map (first installation and deletion of an already-used map are not distinguished): prior numeric aliases have no reverse binding and are rejected until native events create new deterministic typed bindings. Without the former file, the store cannot check all prior collision ownership; restore an operator-managed backup rather than infer bindings from old sessions or assume collision-free reconstruction.
- Legacy sessions and numeric whitelist entries intentionally do not migrate. Raw native whitelist remains usable. Existing files are left untouched.
- Hot account changes must not reverse-route an old account alias to new credentials. Binding validation rejects mismatches.
- Probe sets BOTAGENT_DATA_DIR to a unique temporary directory before initializing AppDatabase. Its synthetic SQLite dedup table exercises the real production dedup path; no production AppPaths/data is accessed. Identity files and database files are temporary, and all HTTP calls are fake.

## Verification

```powershell
dotnet run --project tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj -c Release
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release
```

## Implemented behavior and remaining boundaries

- [FeishuIdMap](../../src/BotAgent.Headless/Adapters/Persistence/FeishuIdMap.cs) reuses [OfficialIdMap](../../src/BotAgent.Headless/Adapters/Persistence/OfficialIdMap.cs). The persisted JSON key is a five-item array: version `v2`, platform `feishu`, app identity, kind (`group`/`participant`/`message`), native ID. This escaping-safe representation is not a legacy alias import. Private destination and participant intentionally share a binding because the numeric private bridge routes via UserId.
- Durable mode uses an explicitly endian-independent 64-bit SHA-256 candidate, synchronous temporary-file replacement, restoration validation and collision rejection. A failed write rolls back the new in-memory binding. The original official constructor retains its original range, 32-bit candidate, collision probing and throttled/best-effort saves. Its default note is unchanged.
- [FeishuBotGateway](../../src/BotAgent.Headless/Adapters/Platforms/Feishu/FeishuBotGateway.cs) validates account and kind on reverse lookup. Targets, quoted messages, successful send IDs and outbox IDs use the same mapping. Unknown/old-account/old-range aliases do not trigger token requests or send calls. Production CompositionRoot injection was performed by the parent agent; this task did not modify that file.
- Token entries are immutable `snapshot/token/expiry` records published/read with Volatile. The complete snapshot contains app ID, exact secret (never trimmed), and normalized API base. Both token POST and message POST use that snapshot; queued/in-flight old requests may finish on their old account/endpoint, while new requests use new configuration. Clearing app ID or secret never reuses a cached token. Failed refreshes are not cached.
- Dedup registration was moved after successful typed identity allocation, without changing database schema, keys, TTL or the successful duplicate response. Thus a wildcard/raw-whitelisted event with a binding write failure gets `503 identity_unavailable`, can retry with the same event ID/nonce after repair, and does not emit callbacks. Numeric whitelist allocation failure instead safely resolves to zero and returns the existing `200 not_whitelisted`; no callbacks or outgoing requests occur. These two paths deliberately do not share the same status claim.
- This is not an exactly-once webhook transaction: callbacks and dedup registration still are not transactional together. Callback crashes/cancellation after registration retain the existing residual risk. Multiple processes writing one identity file are not supported; one production gateway owns the injected store.
- Legacy numeric sessions/whitelists/history remain on disk, are not guessed or inherited, and are not routable through new bindings. PlatformRegistry and structured account scope remain `default` for compatibility; only the numeric bridge identity includes the app identity. A full structured-ingress account-scope redesign is outside this task.

## Observed red/green evidence

| Check | Red | Green |
| --- | --- | --- |
| Secret hot reload | Existing gateway performed 1 token request instead of required 2 (`pwsh-366`, exit 1) | Focused probe refreshed after secret/app ID/API base changes and rejected cleared credentials |
| Restart reverse routing | Temporary exact HEAD gateway fixture lost reverse identity before replay (`pwsh-423`, exit 1) | Persisted reverse route, quote and send/outbox IDs passed; temporary fixture removed |
| Persistence error boundary | Unwritable synthetic map threw instead of controlled response (`pwsh-424`, exit 1) | Typed path returns 503 and emits nothing |
| Same-event recovery | Binding failure consumed dedup marker and prevented retry (`pwsh-493`, exit 1) | Same event/nonce succeeds after repair; normal/concurrent duplicates emit once |
| Exact secret key | Secret whitespace was trimmed into stale cache key (`pwsh-499`, exit 1) | Exact secret snapshot test passed |

Final focused [FeishuRemediationProbe](../../tests/BotAgent.FeishuRemediationProbe/Program.cs): **15 scenarios passed, 0 failed** (`pwsh-508`, exit 0). Coverage includes 100 persisted identities with reversed/fresh arrival order, kind/account isolation, legacy rejection, 64 parallel allocations, immediate persistence, damaged map retention, original OfficialIdMap behavior, 32-way refresh reuse, queued old/new configuration snapshots, signed retry, 32 concurrent duplicate events, numeric whitelist IO failure, and forced strict-store candidate collision.

Existing [ProductionSpecProbe](../../tests/BotAgent.ProductionSpecProbe/Program.cs): **59 passed, 0 failed** (`pwsh-488`, exit 0), including prior Feishu authentication, malformed payloads, signature/nonce/event dedup, routing, context rejection and token-response failures. Core rebuilt by ProjectReference as part of focused probe execution; no Feishu warnings. Five shared-tree pre-existing warnings were observed (nullable references and unused official events). One concurrent-build attempt failed with CS2012 because another agent was writing the same core output (`pwsh-497`); the later sequential build/run succeeded. This was a build-file contention, not a passing test claim.

Final direct `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release`: **0 warnings, 0 errors**, followed by the focused probe **15/0** and scoped `git diff --check` (all in `pwsh-518`, exit 0). The parent reports ArchitectureProbe **92/0** after the webhook helper extraction; baseline was not raised. Full integration scheduling remains the parent agent's responsibility to avoid shared build/port conflicts.
