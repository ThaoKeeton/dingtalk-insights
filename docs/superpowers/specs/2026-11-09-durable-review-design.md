# Persistent human verification for saved reports

Baseline: clean local and remote `codex/workbench-plugin` at c0c1d3303ae8b59c36dd7c4c1de85ddc6578abcf. No repository or ancestor AGENTS.md/local skills were found. This bounded feature was selected under the delegated instruction to choose and implement a business gap; design/implementation choices below are the implementer's, not additional user-approved requirements.

## Observed gap and choice

Monitoring, explicit search, source previews, analysis and both native interfaces already exist. `ConfirmResolvedAsync` changes only an in-memory Issue; SavedReport retains the original Markdown and no structure. History selection deliberately sets the structured report to null, so confirmation disappears and historical issues cannot be reviewed. README explicitly documents this limitation.

Choose persistent review of exact saved reports, including reopening a confirmation. Keep monitor/query deduplication unchanged. A separate task-aware query ledger is valuable but has different budget/unknown-attempt semantics and is deferred. Cross-report semantic issue tracking is outside this bounded change.

## Storage and identity

- Retain the original SavedReport and state.dpapi format. Use a bounded `report-reviews.dpapi` sidecar under BusinessStore's existing canonical `state.dpapi.lock`, shared by standalone and plugin. No SDK/host change or credential access.
- Capture reviewable structure for newly generated reports: original scope/completeness/warnings, only source messages actually quoted, canonical model-output fields and original report identity. Omit collection response ledgers. Revalidate through AnalysisValidator and require rendered baseline Markdown to match the saved original before trusting a restored snapshot.
- Identity hashes saved creation time, scope, task, original Markdown, demo flag and oldest source. A review binds index plus a hash of issue text and exact source revisions/quotes. Confirmation never propagates to another report or a later message revision.
- Store at most100 active reports,30 latest per-issue decisions per report,512KiB per snapshot and16MiB encrypted document. Refuse capacity/corruption errors without deleting existing records or pretending to confirm. Prune records when their canonical report is removed/expired. Explicit index clearing also clears review content; budgets/model/send attempts remain unchanged.
- Keep original report text readable when an optional snapshot cannot be captured; confirmation itself must persist successfully before visible status changes. Corrupt archives fail closed, never promote model output to a human decision. Existing legacy reports without snapshots remain text-only.

## User flow and authority

- Both native interfaces can select a retained structured history report, inspect evidence, confirm resolved, and explicitly revoke that confirmation. Labels distinguish current saved human decisions from unverified model suggestions. Render timestamps as UTC, without storing OS usernames.
- Repeated identical decisions are idempotent. Only the latest decision for a given issue is stored. Reopening restores the original model status; it does not claim a new model assessment.
- Plugin confirmation uses an immutable one-use preview bound to report/view/issue, generation and five-minute expiry. Stop, selection changes or a revised view invalidate it. No provider, model, DWS or send operation is involved.
- Failed storage leaves current and saved visible status unchanged. A local review failure does not itself request monitoring Stop. Source/canonical retention continues to be enforced before use and after dialog waits.

## Constraints and validation

Only this repository; preserve standalone and plugin; fixed Workbench27154465 and44 immutable inputs remain unchanged. No real chats/keys/DWS/model/enterprise interfaces, no Webhook requests/messages, no user-computer access, no PR/merge/deploy. Public source/synthetic-only Actions testing is explicitly authorized by the supplied transcript; this does not authorize the denied PR.

Use production cipher/store/controller/core with synthetic fixtures for exact binding, confirm/reopen/restart, cross-adapter handover, idempotency, corrupt/capacity/save failures, expiry/clear, stale previews and unchanged budget/attempt ledgers. Add genuine standalone and installed-plugin native synthetic confirm/reopen/history/restart checks. Run independent review and all affected verification before the final commit/push.

## Review-driven durability boundary

Final state trim happens once in EncryptedStateStore before a pre-persist review callback, including startup rewrite. Review invalidation/pruning (or a flushed presence-only blocked marker) precedes canonical replacement. Marker failure aborts the canonical write; required retention/eviction prune failure also aborts it. Blocked archives still attempt privacy maintenance and cannot provide any human confirmation. Only explicit index clear resets the archive and marker. Optional archive corruption leaves retained original text readable; standalone recovery is read-only without readiness/credential access, while canonical plugin connection remains refused when it cannot durably revoke prior recovery. This connection limitation is deliberate and documented in docs/durable-review.md.
