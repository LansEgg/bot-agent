# Review remediation plan: #54 settings/audit/disk and #55 recall/own-message scope

## Objective & scope

Deliver two narrow remediation slices in the shared `fix/review-remediation` worktree:

- #54: settings save must not report success or publish a new runtime snapshot when persistence fails; settings and the optional audit event must use one consistent SQLite transaction/lock contract; runtime state and disk state must not diverge.
- #55: recalled flags must survive ConversationRecord -> SQLite -> ConversationRecord roundtrip; own-message lookup must be scoped by platform/account/conversation/native message id, while legacy ambiguous long-only data remains retained but fail-closed.

Allowed implementation files are the settings/audit/persistence/domain/service files and new independent synthetic probe files. The parent initially owned all [ReplyPipeline](<../../src/BotAgent.Headless/Services/Reply/ReplyPipeline.cs>) edits and later explicitly permitted only prepending `conversation.SourceKey` at its cadence-send callsite; the query callsite was updated by the parent. The parent also approved the narrow own-message-related [SafetyProbe](<../../tests/BotAgent.SafetyProbe/Program.cs>) contract updates.

## Pre-change architecture & impact surface

- `SettingsBox.Apply` clones and publishes before `SettingsHotReload.ApplyRuntimeSettings` calls `ISettingsRepository.Save`; `SettingsStore.Save` catches and logs persistence failures. This allows a failed save to leave a new runtime snapshot and still return HTTP 200.
- `SettingsStore.Save` runs the settings upsert inside `AppDatabase.Write`; when the audit implementation is `AuditLogStore`, it calls `AppendInTransaction`. `AuditLogStore.Append` currently locks `_gate` before entering `AppDatabase.Write`, while `AppendInTransaction` enters `_gate` from inside the writer callback. This is the lock-order inversion.
- `BotConversation.ToRecord` and `FromRecord` carry `MessageRecord.Recalled` in the domain model but currently omit it in both directions. `ConversationStore` already persists the field in `messages.recalled`.
- `OwnMessageLedger` and `OwnMessageStore` use one global `long` key. Existing `ConversationId`, `MessageRef`, `ConversationIdCodec`, `PlatformId`, and `AccountScope` already provide the platform scope model. The old `own_messages` table and legacy JSON must remain readable/retained; ambiguous old rows must never be returned as a scoped hit.

## Contracts & data flow

### Settings/audit

1. Validate and build the next settings snapshot without publishing it.
2. Persist the candidate settings and optional audit event in one SQLite writer transaction under one lock ordering.
3. Only after commit, publish the candidate snapshot and rebuild runtime dependents/notifications.
4. Any persistence or audit failure propagates to the panel handler; the handler returns a non-success status and does not emit a success payload.
5. `AuditLogStore` uses the SQLite writer transaction as the serialization boundary; no second lock may be acquired in the opposite order. If a compatibility lock remains, it must be acquired before entering the writer in every path.

Because existing panel mutation currently includes secret/mood/TTS side effects, those side effects must not be claimed atomic unless the repository contract is extended. Keep changes minimal and document any remaining independent-secret persistence limitation rather than inventing a transactional secrets API.

### Recalled roundtrip

`ChatMessage.Recalled` -> `ConversationRecord.MessageRecord.Recalled` in `ToRecord`, and back in `FromRecord`. Existing SQL already writes/reads this flag; no schema change is needed.

### Scoped own-message ledger

Introduce a domain value/key containing a validated `ConversationId` (encoded with `ConversationIdCodec`) and the native message id string. Persist scoped rows in a new table or equivalent schema keyed by `(conversation_key, native_message_id)`. The ledger query and upsert use the full scope. Legacy `own_messages(message_id INTEGER)` and old JSON migration remain retained; legacy rows are not used for scoped lookups because a bare numeric id is ambiguous across platform/account/conversation. A separate explicitly named compatibility method may support old callers only where the caller supplies a unique legacy context; the default long-only path must fail closed.

## Milestones

1. Add `docs/engineering/review-54-55-plan.md` (this plan) and an independent synthetic probe project/source files; no production data or existing probe edits.
2. RED #54: probe a throwing settings repository and assert current behavior publishes/returns success; probe audit concurrency/lock-order contract as a deterministic source-linked or fake-writer check.
3. GREEN #54: refactor settings persistence/publish order and error propagation; make audit transaction ordering consistent; move panel success-only side effects after successful save where the current seam permits.
4. RED #55: probe recalled record roundtrip and two same-native-id messages from different scopes; assert no cross-scope hit and legacy long-only is fail-closed.
5. GREEN #55: add recalled mappings and scoped storage/ledger API; keep legacy data intact and add idempotent migration only if required by the existing schema.
6. Run isolated probe(s), target builds, diff checks, and the architecture probe where possible. Do not claim full application build if concurrent parent changes keep it red.
7. Send parent the exact files changed, command evidence, and a minimal `ReplyPipeline.cs` callsite patch recipe.

## Edge cases & risk mitigation

- Candidate mutation may throw: do not publish and do not report success.
- Save may throw after opening SQLite transaction: transaction rollback must leave the old settings snapshot active.
- Audit validation failure must also leave settings unchanged.
- Concurrent saves must serialize from the latest candidate and never overwrite a committed sibling update.
- Recalled messages must retain text and be marked true after reload; absence of a row remains distinct from a recalled row.
- Native ids are strings (preserve case/format); platform/account/target/thread are part of scope.
- Old bare numeric rows and duplicate native ids across scopes fail closed rather than guessing.
- Probes use synthetic ids such as `10001`, `group-test`, and `example` values only; never inspect runtime data.

## Verification strategy

Targeted, synthetic commands from `qqchat-src`:

```powershell
dotnet run --project tests/BotAgent.SettingsScopeProbe/BotAgent.SettingsScopeProbe.csproj -c Release
dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
node tests/BotAgent.FrontendProbe/probe.mjs
git diff --check -- <changed-files>
```

The probe uses only synthetic fixtures in a fresh temporary root set through both data-root environment variables before any static database initialization. No production server, database, messages, members, logs, commit or push is involved. Full harness/integration execution remains owned by the parent. The parent explicitly approved a narrow update to the own-message fake repository and its scenario in [SafetyProbe](<../../tests/BotAgent.SafetyProbe/Program.cs>).

## Implemented contracts and limitations

- [SettingsBox](<../../src/BotAgent.Headless/Services/SettingsBox.cs>) now serializes snapshot/mutate/persist/publish/runtime callback under one writer gate. The runtime-only `Apply`/`PatchSettings` path remains non-persistent; persisted updates use `ApplyPersisted`.
- [SettingsStore](<../../src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs>) propagates failures. With an audit event, only the concrete SQLite audit store is accepted; null/external chains are rejected before writing, rather than pretending cross-store atomicity. [AuditLogStore](<../../src/BotAgent.Headless/Adapters/Persistence/AuditLogStore.cs>) relies on the existing non-deferred SQLite writer transaction for previous-hash read + insert, without a second inverted lock.
- [SettingsHotReload](<../../src/BotAgent.Headless/Services/Settings/SettingsHotReload.cs>) saves before publishing/rebuilding. The [panel handler](<../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs>) returns HTTP 500 on a failed update; mood and host TTS writes run only after settings commit. Secret writes remain independent transactions and can already have succeeded when settings/audit persistence fails. This is an approved residual limitation, not an atomic-secrets guarantee.
- A postcommit runtime/notification callback can still throw: the error propagates/returns non-success, but committed settings remain in SQLite and published memory. It is not a rollback claim; some runtime dependents may require retry/rebuild. The probe asserts this distinction explicitly.
- [BotConversation](<../../src/BotAgent.Headless/Services/BotConversation.cs>) maps `Recalled` in both record directions; existing SQL already reads/writes the flag. Text remains retained.
- [OwnMessageLedger](<../../src/BotAgent.Headless/Services/Conversations/OwnMessageLedger.cs>) keys by the existing `MessageRef`, canonicalizing only through `ConversationIdCodec`. Platform/account/target/kind/thread/native-id are all part of identity; native id and account case remain significant. Invalid kind or incomplete scope is rejected/fail-closed. Concurrent initialization/lookups/writes share one gate.
- [OwnMessageStore](<../../src/BotAgent.Headless/Adapters/Persistence/OwnMessageStore.cs>) writes the additive `own_messages_scoped` table keyed by `(conversation_key, native_message_id)`. Legacy/scoped reads reuse one SQL query. Old table rows and JSON import/archive remain retained; neither is assigned guessed scope. Bare-id upserts/ledger remembers are compatibility no-ops, bare-id lookup always misses, and pruning touches scoped rows only. An old binary cannot read new scoped own-message entries; no dual-write or lossless downgrade is promised.
- The parent updated the query callsite in [ReplyPipeline](<../../src/BotAgent.Headless/Services/Reply/ReplyPipeline.cs>) to `TryGet(conversation.SourceKey, quotedId, out var mine)`. With explicit parent approval, its cadence-send call now supplies the same `conversation.SourceKey` through [IQqMessageSender](<../../src/BotAgent.Headless/Services/Ports/IQqMessageSender.cs>) to [PlainSender](<../../src/BotAgent.Headless/Services/Reply/PlainSender.cs>). The new overload reuses the existing sender core and uses the parsed full `ConversationId` only for own-message bookkeeping; strategy/risk-backoff checks and transport target routing are unchanged. Invalid source keys fail closed; unsupported old sender implementations explicitly throw instead of silently dropping thread/account scope. Existing bool/PlatformContext overloads remain available but cannot reconstruct missing thread/native-target information.
- The original [multi-platform storage plan](<multi-platform-botagent-plan.md#37-数据扩展与回滚门禁>) is historical; this remediation deliberately cuts all new own-message writes (including QQ) to scoped storage, without migrating ambiguous old identities.

## Observed RED → GREEN evidence

The independent [SettingsScopeProbe](<../../tests/BotAgent.SettingsScopeProbe/Program.cs>) reproduced these actual failures before their fixes:

1. Invalid audit rolled the settings transaction back but `Save` returned success.
2. A throwing repository caused hot reload to publish the new runtime reference before persistence.
3. `Recalled` was lost through record → actual SQLite → reload.
4. An unscoped legacy native id was treated as a valid own-message identity.
5. Invalid conversation kind silently became a valid default scope.
6. A real `PlainSender` cadence send succeeded but its new-account/thread conversation could not find the remembered message because the old signature lost thread scope.

After implementation, **13 checks pass / 0 failures**, including synthetic old-schema upgrade, legacy JSON import/archive byte retention and repeat-import safety, full scoped restart isolation, real PlainSender transport → ledger write → reload → source-key query with sibling account/thread/native-target identities, mutation/save/postcommit failures, unsupported audits, 20 concurrent settings writers with disk/publication/callback-order checks, 60 concurrent ordinary/settings audit events with complete SHA-256 chain verification, and independent scoped pruning.

Pre-integration probe verification: SafetyProbe **479 passed / 0 failed**; ArchitectureProbe **92 passed / 0 failed**; FrontendProbe **296 passed / 0 failed**. Normal main-project Release build passed with **0 errors** (first non-incremental run: 5 existing warnings / 4.01 seconds; final incremental run: 0 warnings / 1.03 seconds). SQL literals remain **137** and `CreateSchema` remains **200 lines**, without changing the architecture baseline. Synthetic expected-failure stderr in these probes is intentional. These probe results are not a claim that the full integration suite passed.

## Integration contract alignment (follow-up)

The first fresh compiled full integration run reported **774 passed / 2 failed** (776 assertions), not an all-green result. Read-only inspection of its [S19 failure](<../../../review-artifacts/remediation-run-20260930-150115/integration-all.log#L288-L292>) and [S27 failure](<../../../review-artifacts/remediation-run-20260930-150115/integration-all.log#L494-L503>) showed the new-send persistence assertions still queried the legacy table. The existing S19 restart/quote behavior checks passed; the S27 legacy three-row import, JSON archival and repeat-import checks passed. Exact scope could still have been wrong, so the fix does not weaken either assertion to a scoped-table total.

With explicit parent authorization, only [ReplyQuoteScenario](<../../tests/BotAgent.IntegrationHarness/ReplyQuoteScenario.cs>), [MigrationScenario](<../../tests/BotAgent.IntegrationHarness/MigrationScenario.cs>) and a limited [DbProbe helper](<../../tests/BotAgent.IntegrationHarness/DbProbe.cs>) were changed. The standalone harness has no bot ProjectReference; the helper mirrors the reviewed codec contract for positive numeric legacy QQ group IDs only. Each new-send assertion now waits up to 5 seconds for exactly its complete `conversation_key` plus string `native_message_id` row, and additionally checks that the new ID was not written into the ambiguous legacy table. S19 also verifies that the global mock receipt IDs before restart belong only to its current group. Existing behavior checks and legacy import/retention checks remain intact; synthetic subprocesses explicitly use file logging disabled.

The harness-only Release build passed (**0 errors, 2 existing warnings, 7.28 seconds**); core binary SHA-256 stayed `265432F7852531FCEB41DBBA08C6194F89AF5F78BD3A2713F90C2FE720F13078` before and after this build. The rebuilt harness then ran only S19/S27 against a new isolated synthetic temp root: **54 passed / 0 failed**, exit code 0, with [exact-scope and retained restart/legacy evidence](<../../../review-artifacts/scope-target-20260930-152003-e3a53af5/integration-s19-s27.log#L30-L63>). The same core binary hash remained unchanged after execution. This added three assertions without removing old behavior/import checks. The complete post-alignment integration rerun remains owned by the parent and is not claimed passed here.
