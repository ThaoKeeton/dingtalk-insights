# v0.2 background monitoring design and implementation ledger

Authorization: the supplied background-monitoring brief authorizes design, implementation and synthetic testing in this isolated worktree. No further design gate; no real service calls, startup/service installation, pushes, PRs or merges. The v0.1 trial stays unchanged.

## Runtime and approach

Use a user-session WinUI tray host and a dependency-free core supervisor around `MonitorEngine`. A Windows Service would require a different identity/DPAPI/UI boundary and installation; embedding more timing policy in `MainWindow` would make restart and race tests unreliable. The user must remain logged in and launch the application. Sleep, logout and exit stop real-time collection. Saved recovery only runs when launched again. There is no Windows autostart or updater.

The native host owns one notification-area icon (Open, Stop, Exit), restores after taskbar recreation and never hides without a working icon. Close-to-tray is separately opt-in and confirmed; a failed icon keeps/restores the visible window. App SDK single-instancing redirects only a show-window signal; activation arguments never run commands or enable recovery. Smoke uses separate data and activation namespaces. The encrypted state file lock remains the final safeguard.

## Authority and persisted data

`MonitoringConfiguration` binds source, stable DWS identity, conversation, original start, executable path/content revision, AI enabled flag, complete model/rate/output/budget settings, interval, retention, and credential revision. `RecoveryPolicy` version 1 stores this exact configuration only after concrete UI approval. Old state has no policy and cannot recover. Read-only recovery never supplies an analyzer. Explicit Stop/clear revokes recovery before another launch can start; Exit retires current work while retaining a separately approved recovery policy. Settings/credential mismatch, missing readiness, invalid/unknown policy and attention state block unattended work.

DWS `auth status --readonly` is a local readiness check, not a login/refresh. Match the returned stable identity. Bind stable corp_id:user_id and the app vault revision. DWS exposes no stable login-generation identifier; OAuth expiry timestamps roll during normal refresh and are not revisions. Same-identity DWS re-login cannot be detected from these non-secret fields. Structured authorization failures still pause. The executable content digest prevents silent replacement. Any uncertainty requires attention. No keys are stored outside the DPAPI vault, passed in process arguments or logged.

A single bounded health record stores safe enums, transient count, next read/model due times, AI suppression and last complete progress. It never stores provider exception text, bodies or URLs. Schema stays at state version 1 so missing fields migrate default-off; incompatible background policy versions are refused.

## Supervision, retries and cost

One supervised session and one in-flight tick at a time. Start/wake calls coalesce through a semaphore; Stop cancels and awaits old work before a new session starts. Cancellation checks after asynchronous transport completion prevent stale results from writing checkpoints/results or reviving a session. Scheduling uses an injected UTC clock/delay; each wake catches up through the engine's one-hour windows and two-minute overlap without changing the original scope.

Only an official structured `error.retryable=true` API failure with matching exit semantics and no authorization/permission/validation/partial exclusions is transient. Unknown errors, schema/incomplete responses, retention and storage faults pause. Backoff respects the configured interval, doubles, caps at max(interval, 15 minutes), has a finite retry count and persists before waiting. Explicitly longer server retry hints require attention rather than early retry. Read failures never advance coverage.

Budget limit before a model call keeps unattempted sources pending, keeps safe authorized collection, and defers AI to the next UTC day. Actual saved counters are rechecked; restart, Stop/Start and settings never reset them. Unknown model outcomes retain attempts/reservations, disable automatic AI work for that session and subsequent recovery, and allow safe local collection. A fresh explicit start can re-enable AI for new/unattempted revisions only; already attempted revisions are never replayed. Model authentication errors cannot create paid loops. No fallback models and no Webhook authority.

Completeness remains conservative. No attachment/event subsystem; old edits, retractions, source visibility, pagination limits and retention gaps remain limitations, not a complete audit.

## Implementation plan / task ledger

- [x] Policy and health: add exact-match, default-off/revoke and safe-message tests; run RED, implement `BackgroundPolicy.cs`; extend encrypted state.
- [x] Supervisor and engine: deterministic tests for no authority, read-only recovery, bounded gaps, backoff, incomplete/storage pause, cancellation/coalescing and UTC budget/unknown outcomes; run RED, implement `MonitorSupervisor` and narrow typed engine outcomes.
- [x] DWS boundary: structured error classification/readiness parser tests; run RED, implement fixed readonly status and safe typed failures against pinned v1.0.67 wire contract.
- [x] Native host: notification icon lifecycle, close/Stop/Exit, single-instance activation, explicit recovery confirmation and startup policy validation. Extend native smoke with synthetic-only recovery, second launch and tray failure fallback; native shell availability must be reported.
- [x] Update v0.2.0 version/help/README/design; run full Release suite and offline DWS contract; self-review, local commit and report exact pending Windows CI checks.

Verification: `/tmp/dingtalk-dotnet/dotnet run --project tests/DingTalkInsights.Tests -c Release -p:UseSharedCompilation=false -m:1 -nr:false` with task-isolated SDK/NuGet/data environment. Offline DWS help/schema only via `scripts/check_dws_contract.py`. Parent runs native Windows publish, controls/DPAPI/restart/background/ZIP checks at the committed head.

## Review focus

- A stopped or changed policy must cause zero fetch/upload on restart, including corrupted/incompatible persisted policy.
- Transport ignoring cancellation must never commit a stale checkpoint, report or health after Stop/restart.
- Midnight during a deferred budget must preserve attempts, counters and source scope; clock rollback must pause safely.
- A runner without Explorer cannot validate actual tray interaction: mandatory fallback stays visible and reports shell coverage separately.
- Smoke flags must never select production data, activation keys or real service transports, even with recovery arguments.

References: [App SDK instancing](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing), [notification area/taskbar recreation](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar), pinned [DWS v1.0.67 envelope](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli/blob/v1.0.67/internal/output/envelope.go), [readonly auth](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli/blob/v1.0.67/internal/app/auth_status_readonly.go).

Milestone verification: 111/111 Release synthetic tests passed; offline DWS help/schema command exited 0. Native implementation and smoke await Windows CI; these are not claimed to have run on Linux. First RED log records missing new production interfaces; the next executable pass exposed the safe HTTP notice regression and persisted retry-due behavior, corrected before GREEN. No real external services, startup/service settings, pushes or PRs were used.

## Review corrections

Pending startup now registers a cancellation source before asynchronous waits. Stop signals pending and active sources immediately, advances a generation, and retires the session before releasing explicitly queued new starts. A cancelled readiness completion cannot persist Running health or launch a loop. Queued UI-context regressions cover confirmed starts and saved recovery, with readiness that either honors or ignores cancellation.

Budget reservation now samples the supervisor's injected UTC clock inside the shared engine reservation boundary, after fetch/readiness/semaphore waits. A typed budget failure carries the next UTC midnight calculated from that same reservation date. Deterministic tests cross midnight during fetch and pre-model readiness, preserve the actual-day cap after another tick and restart, and cover the shared one-off analysis path. The original review probes now show zero dispatch after pending-start Stop and one correctly reserved new-day request.

Native Stop smoke awaits the complete host Stop task, then verifies persisted revocation and the unchanged message count. Loop completion alone precedes the durable user-stopped write and is not accepted as Stop completion. Linux Release regression suite: 119/119. Final Windows native checks remain a parent-run gate at the correction commit.

## Final integration corrections

The native host now owns a cancellable startup generation before the first confirmation/configuration await, including saved recovery. Stop/Exit invalidate that generation immediately and await its retirement; a separately confirmed fresh Start waits for retirement and can then proceed. Configuration hashing checks cancellation after completion, authority writes and attachment check the current owner, and the same cancellation token links into the supervisor's pending start. Cancelling recovery cannot replace the durable stopped state with an attention/running update. Normal confirmation dialogs remain mandatory.

The complete production startup/configuration/attachment/restore/Stop/Exit methods live together in `MainWindow.Monitoring.cs`. The cross-platform regression project links that actual file, substituting only native controls, ordinary settings IO and the paid analyzer. Queued UI-context tests hold the completed initial executable hash continuation before supervisor creation, then verify Stop/Exit retirement, zero old reads/model attempts/reservations and durable stopped/preserved-exit policy. They cover confirmed start and saved recovery with AI on/off, plus a separately confirmed fresh Start after Stop. This is synthetic host-boundary evidence, not a claim to execute WinUI on Linux.

Pinned v1.0.67 DualValidate errors use legacy `error.category`/`error.code` on stderr. The client captures a private bounded diagnostic, drains oversized output without classifying its prefix, and validates the structured category/code/retry flag and actual exit/outcome before retry. Any simultaneous business/unknown stdout, malformed/duplicate metadata, auth/permission/validation/partial result or unsupported retry hint remains nonretryable. Error/provider text and URLs never enter health, state or notices. Successful incomplete stdout keeps its existing receipt and coverage guards. The fixed command arguments and completeness contract are unchanged.

Final-integration RED: 145/152 (all original 119 passed; five host retirement and two production process/retry regressions failed). Host correction: 150/152, leaving only the two DWS findings. Combined GREEN: 152/152 Release on Linux. The oversized diagnostic regression writes over 70 KiB, exceeding a 64 KiB pipe buffer, and completes with both stream tasks and process exit observed. New-head Windows core/publish, real native tray/Stop/restart/DPAPI and extracted-ZIP repetition remain parent-run checks; earlier 6732 platform evidence does not establish this new head.

## Workbench runtime extraction (Task 1, isolated plugin branch)

`DingTalkInsights.Runtime` reuses this supervisor and engine without WinUI/host SDK dependencies. Standalone recovery policy is preserved. Workbench's current SDK has no stop reason, so every plugin deactivation revokes recovery and waits for pending configuration/manual/model/send work before releasing the same canonical state-file lease. No legacy root/config/credential scan occurs during plugin initialization. The plugin uses one existing physical ledger, not a migration copy; its volatile authority is bound to owner context and credential purpose/revision.

Maintained standalone and runtime now share a DPAPI, atomic, hash-only `send-attempts.dpapi` sidecar, under the old state lease. Old-v0.2 state load/save loses unknown JSON fields but leaves this sidecar intact; the old binary's own sends still cannot honor it. Stop/clear never erase send attempts; manual retry requires renewed confirmation and may duplicate delivery. No automatic Webhook was added. The shared Core now rolls back only undurable report/returned-usage changes on a final save failure, preserving pre-HTTP reservations and attempts.

See [Workbench SDK requirements](workbench-sdk-requirements.md) for exact preview/retirement interfaces and remaining native gates. This Task 1 does not claim installable plugin/native-host acceptance.
