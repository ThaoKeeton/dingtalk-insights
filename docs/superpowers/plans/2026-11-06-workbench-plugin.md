# Workbench plugin Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline. The supplied task forbids subagents and extra design gates. Steps use checkbox tracking.

**Goal:** Deliver the reusable, owner-safe runtime needed by a native Workbench plugin, then pause at a reviewed interface boundary.

**Architecture:** Reuse Core's engine/supervisor. Add one .NET 8 controller and Windows storage library surface; select one existing canonical live ledger with its legacy-compatible lease. Keep host registration and native views in the following task.

**Tech Stack:** .NET 8, C# 12, DPAPI P/Invoke, synthetic console test harness. Future adapter: .NET SDK 10.0.403, net10.0-windows10.0.19041.0, x64, WindowsAppSDK 1.8.260921001, SDK API 1.

**Spec:** `docs/superpowers/specs/2026-11-06-workbench-plugin-design.md`

## Global Constraints

- Work only on codex/workbench-plugin; preserve standalone release worktrees and pins; no pushes/PRs/merges, subagents or platform edits.
- Core and standalone remain .NET 8. No native UI in Task 1.
- No real chats, keys, paid APIs, DWS login/business reads or Webhook messages. Tests use fake lower transports and explicit temporary roots.
- Same canonical state file and existing exclusive state.dpapi.lock for live ownership. No copied budget/attempt ledger and no inherited recovery consent.
- Initialization and navigation never authorize business I/O; every Stop/Lifetime retires work before releasing stores.

## Review Focus

- A cancelled outer hash/readiness continuation cannot create a supervisor, save Running or dispatch after Stop.
- A transport that ignores cancellation keeps the canonical lease held until retirement and cannot publish stale raw history.
- Expired or corrupt canonical content must not silently grant scope, retention, credential or recovery permission.
- Signing-secret/name edits and interrupted HTTP must not bypass persisted duplicate-send suppression.
- Old released v0.2 JSON rewriting must preserve the send sidecar; its own sends cannot honor it. Test the rewrite and retain model/budget state in the same store.

### Task 1: Runtime, ownership, lifecycle and shared durable sends

**Files:**
- Create `src/DingTalkInsights.Runtime/{DingTalkInsights.Runtime.csproj,WindowsStorage.cs,BusinessStore.cs,RuntimeContracts.cs,OperationPreviews.cs,RuntimeServices.cs,InsightController*.cs}`.
- Create `src/DingTalkInsights.Core/DurableWebhookSender.cs`; persist a bounded retained send ledger in a canonical DPAPI sidecar under `BusinessStore`'s state lease.
- Modify standalone `LocalData.cs`, `DingTalkInsights.Desktop.csproj`, `MainWindow.xaml.cs` only for shared storage/send boundaries.
- Create `tests/DingTalkInsights.Tests/RuntimeTests.cs`, `DeliveryTests.cs`; register in `Program.cs` and reference Runtime.
- Create `docs/workbench-sdk-requirements.md`; update README/background docs with explicit plugin/rollback limitations.

**Interfaces produced for Task 2:**
- `InsightController(string dataDirectory, string ownerContext, IDataCipher cipher, IRuntimeServices? services = null, IMonitorClock? clock = null, CancellationToken lifetime = default)`; no business I/O in construction or `InitializeAsync`.
- Controller: `OpenDemoAsync`, `ConnectAsync(string canonicalRoot, bool confirmed, ...)`, `UseCredentialsAsync(CredentialLocation, CredentialUse, bool confirmed, ...)`, `SaveCredentialsAsync(Credentials, bool confirmed, ...)`, `PreviewMonitoring(MonitoringRequest)` + `StartMonitoringAsync(MonitoringPreview, bool confirmed, ...)`, explicit read/search/history actions, `PreviewAnalysisAsync(ModelSettings)` + `AnalyzeCandidatesAsync(AnalysisPreview, bool confirmed, ...)`, `PreviewSendAsync(string)` + `SendReportAsync(SendPreview, bool confirmed, bool humanConfirmedRetry = false, ...)`, `StopMonitoringAsync`, `DisconnectAsync`, `DeactivateAsync`, `DisposeAsync`.
- `Snapshot`, `SetDraft`, `Subscribe(Action<RuntimeSnapshot>)` (returns IDisposable), explicit encrypted workspace load/save. Snapshots expose content/status only, never secret values. `MonitoringCompletion` is the actual supervisor Completion after successful Start; `Deactivation` waits for Lifetime cleanup. No view subscription starts work.
- `IRuntimeServices` is the lower DWS/hash/model/Webhook transport seam. `RuntimeServices` implements it using existing Core clients; tests replace only this seam, not controller/storage/supervisor.
- `DurableWebhookSender` serializes/checks/saves a hash-only attempt before calling a supplied lower send transport; returns `SendResult`, suppresses existing attempts unless a separately confirmed human retry is requested. Both controller and maintained standalone use it.

- [x] Write production-boundary tests for initialization, canonical/credential gating, ownership, pending cancellation, rollback and durable sends. Run RED and record the missing-production-interface result.
- [x] Implement the shared encrypted storage and durable send boundary; run delivery tests and existing suite.
- [x] Implement controller/services with outer-operation registration, generation cancellation, supervisor reuse and same-file connection; run new/full tests to GREEN.
- [x] Add targeted regressions for any observed bug; run RED->GREEN before fixing.
- [x] Document exact SDK gaps and rollback/native gates; self-review once and fix material findings with regression coverage.
- [x] Run full Release suite and git diff --check; commit locally. Write `/tmp/dingtalk-workbench-task1-report.md` with tests/interfaces/decisions/remaining native checks and pause for parent review.

### Task 2: Native adapter, pages and installable package (native-CI-ready)

**Files:** new native plugin project, manifest, staging/smoke scripts, native acceptance workflow.
**Consumes:** reviewed Task 1 interfaces above, pinned Workbench SDK/WinUI SDK.
**Produces:** actual installable DLL plugin, native pages/settings and simulated host install/enable/navigation/monitor/disable/exit/remove/relaunch evidence.

- [x] Parent reviews Task 1 commit/interfaces before dispatching a fresh implementer.
- [x] Adapter forwards DataDirectory/Lifetime and owns one controller; factories create standard WinUI controls and disposable subscriptions only.
- [x] Implement private-dependency staging, shared-identity guards and actual Windows host/extracted ZIP phase drivers.
- [x] Parent API1 preflight run 42279542384 passed actual native publish/lifecycle and the entire re-extracted ZIP matrix; checkpoint 83c75e8 remains the rollback. Task3 SDK82 is a distinct pending Windows gate; legacy offline deletion stays labeled.

## Task 1 verification ledger

183/183 Release synthetic tests passed (152 existing, 31 new). Evidence: `/tmp/dingtalk-task1-final-release.log`; `git diff --check` clean. RED->GREEN covered missing runtime/delivery interfaces, first plugin-local credential save into a missing data directory, idle expiry under UTC rollback, immutable Start/analysis/send previews and the undurable-final-report rollback. Pending/ignored model/send/configuration work retains the lease until retirement. `StateStore.cs` is unchanged from e85dbd5, and its actual legacy JSON load/save drops an injected unknown field while the encrypted send sidecar survives. Parent independently reviews this task before native work; no reviewer dispatched by this implementer.

## Task 2 implementation ledger

Ruling: retain implemented/verified 6565180b API1; later 0.2 contracts are not a basis for unverified capabilities. Ruling: use exact source-linked presenter bootstrap for isolated acceptance and production demo Core/MonitorSupervisor transport path; no real account/key/network work. The shipping EXE, SDK uninstall, Windows11 manual and enterprise-service/multiday acceptance remain distinct.

Files: `src/DingTalkInsights.Workbench`, exact 24-blob `eng/vendor/workbench-6565180b`, `tests/DingTalkInsights.Workbench.AcceptanceHost`, PackageTool, net8/net10 InteropFixture, portable adapter tests, build/interop/native launcher scripts and dedicated Windows2025 workflow. Details and current evidence boundaries: `docs/workbench-plugin.md`, `/tmp/dingtalk-workbench-task2-report.md`.

Targeted RED->GREEN: warning-array/request-body freeze; missing adapter contracts; pending adapter persistence returning after Stop; nested inventory payload omission; exact native one-shot read scope across dialog delay. New read overload consumes the real MonitoringPreview and fixes the bounded end instead of recomputing it after the dialog. Core/standalone targets remain unchanged.

### Task 3: verified SDK services and registered real monitoring

Binding spec: `/tmp/dingtalk-workbench-task3-brief.md`; fixed base `03c320a`. Inline execution, no subagents/extra confirmation; root owns independent review/Windows/push. Runtime/Core/standalone stay net8; adapter/harness/tool use one exact 35-input `82bd958e220aaf94ccdbf1ea68b0ec53b770706a` graph. Plugin 0.3.1/API1/minimumHostVersion0.2.0. Host secret values are deferred to Task4; only three definitions are registered.

- [x] Carry A/B preflight in `83c75e8`: sticky failure/admitted real system request, completed native page action plus rendered source/report/history. Linux source-linked reference build: zero warnings/errors; parent API1 Windows 42279542384 passed the full published and re-extracted matrix.
- [x] Import exact verified 35-input graph and align plugin, host, PackageTool, build-info/checkout/scripts; retain `6565180b` rollback.
- [x] Add public-SDK-only `WorkbenchMonitorTask` and UI-neutral `IMonitoringCoordinator` seam. `StartAsync(MonitoringPreview,bool,CancellationToken)` hands the same reviewed preview to one registered `monitor` task, then awaits an actual controller acknowledgement. `StopAsync` immediately invalidates all controller operations even with no registered work; terminal `DeactivateAsync` joins the shared session retirement without awaiting its own worker.
- [x] Add deterministic net10 bridge tests with actual Runtime/Core/controller/supervisor and lower synthetic transports: queued scheduling versus actual start; one-use/expiry/generation, duplicate/rejected context starts, cancellation before/while/after start; actual original loop and lease retention, manual operation Stop, natural exit, terminal upgrade. A dedicated public-SDK-only runtime fixture binds the production bridge to real Manager operations without cloning platform algorithms.
- [x] Extend actual installed-plugin native assertions for task/secret metadata, rejected host starts/resume, context Start, host/manual Stop and recreated page callbacks. Make separate source-linked native pending-Stop fixtures use the registered bridge.
- [x] Add actual package ZIP/install/stage/cancel/remove/rollback and fresh-process startup/recovery checks. Own the user-root lease before ApplyPending and call before any discovery; preserve current data/settings/secret ciphertext/history and external canonical ledger. The unchanged shipping App does not apply maintenance; label the guarded helper honestly.
- [x] Build plugin/reference host/package with real new host references/catalog, run portable/bridge/package/source checks, perform one bounded self-check, commit locally and complete `/tmp/dingtalk-workbench-task3-report.md`; root handles independent review and actual generated PRI/XAML Windows gates.

Review focus: unsuccessful context Start after queued persistence/revocation must settle acknowledgement and retire work; Stop/terminal upgrade must have no self/cross-await cycle; platform Stopping never grants new Start or releases ownership; no value read/automatic authority during restore; package replay precedes all discovery and never restores historical business data.

Task3 local evidence: 200/200 portable checks, 16/16 bridge checks (including real Manager), 11/11 actual-package fresh-process maintenance/recovery checks, five package guards and exact 35-blob verification. Queue-as-readiness mutation compiled but failed the actual-controller admission assertion, then restored production passed. Linux plugin/source-linked host reference compilations are compile-only (no generated native PRI claim). Final committed-source checkout regression and Task3 report record the exact head; independent review and SDK82 Windows/PRI/full re-extracted matrix remain coordinator gates.

Implementation ruling: a small private public-SDK-only Bridge shares the actual coordinator/session/action gate with the separate real-Manager control fixture; the business plugin DLL is never preloaded in the host default context. A nonce-owned lease-taking fresh PackageTool process exercises new package replay before any discovery; the unchanged shipping App does not apply pending work. All legacy rollback inputs/Core/standalone bytes remain unchanged.
