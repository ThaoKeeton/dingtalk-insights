# Workbench SDK integration requirements

## Current verified source contract

Read-only source pin: `6565180b17130f6d494978291df4138de3fdb0b4`, `feat/native-plugin-workbench`. Source provenance is held by the parent in `/tmp/dingtalk-workbench-sdk-source.json`. The Runtime and adapter are coded against the capabilities actually present in that snapshot, not later design documents.

- API 1: `IWorkbenchPlugin.InitializeAsync(IPluginContext, CancellationToken)` and `StopAsync(CancellationToken)`.
- Context: `PluginId`, `DataDirectory`, `Lifetime`, JSON `Settings`; registration of views, commands and string setting metadata during Initialize only.
- Native adapter target: `net10.0-windows10.0.19041.0`, x64, .NET SDK `10.0.403`, WindowsAppSDK `1.8.260921001`. Factories return code-created `FrameworkElement` through `WinUiView`, on the host UI thread. Navigation recreates controls.
- Trusted same-process code. DataDirectory is a storage convention, not a sandbox. SDK/WinUI/WinRT/framework type identity is shared; none of those DLLs may be staged in the business plugin package. Stage the private Core/Runtime DLLs and deps. No standalone Desktop EXE/window/WebView, external XAML or external PRI loading.
- Disable/shutdown cancel Lifetime and request Stop; assemblies remain loaded. Removal/update require exit/restart. There is no business-specific smoke callback.

## Current gaps and Task 1 ruling

| Gap at the pin | Safe integration now | Cost / future decision |
| --- | --- | --- |
| Stop has no reason | Every Stop/Lifetime calls `DeactivateAsync`, retires work and revokes recovery. Re-enable restores no live authority. | Explicit reconnect, credential-purpose confirmation and Start after re-enable/restart. Standalone v0.2 recovery remains separate. |
| No background task registry/observer | Controller owns the real supervisor. Adapter must await manual actions and terminal deactivation; observe failures safely. | Host cannot expose/own an unregistered business loop through the current SDK. |
| No generic secret store | Plugin-local or explicitly selected canonical DPAPI vault; no secrets in Settings. | No automatic credential migration or grant inheritance. A future provider seam needs separate review. |
| No uninstall API / collectible unload | Stop and release resources; package stays loaded until process exit. | Do not self-delete/replace DLLs or claim removal while loaded. Actual removal/relaunch is a native acceptance gate. |
| No arbitrary business smoke API | Task 1 runs the real runtime with fake lower services and synthetic DWS process. Task 2 must exercise actual native views/commands in a real host. | Test-driver file deletion is not a host uninstall API, and cannot stand in for it. |

## Planned, not implemented / not assumed

The parent supplied `de5a316`'s design-only documents (`/tmp/dingtalk-plugin-contract-v1.md`, `/tmp/dingtalk-2026-11-06-business-plugin-platform-design.md`). They describe an optional host 0.2 `IPluginRuntimeContext` with BackgroundTasks, Secrets, StopReason; API 1 / SDK assembly identity 1.0.0.0 compatibility; and restart-time package maintenance. These services are **not in the pinned SDK source**. Task 1 does not reference them or treat the documents as a shipped capability. Parent must refresh/verify the SDK code and its CI before Task 2 opts into a minimum host 0.2 dependency.

`InsightController.MonitoringCompletion` exposes the actual `MonitorSupervisor.Completion`: capture it after a successful `StartMonitoringAsync`, then await that task in any future registered host background execution. It is not a delay/placeholder task. `StopMonitoringAsync`, `DisconnectAsync`, `DeactivateAsync`, `DisposeAsync` await actual pending/manual/supervisor work before completion. `Deactivation` exposes Lifetime-triggered terminal cleanup. A future StopReason integration must not silently reinterpret this runtime's default-off recovery policy; distinguish host task intent from concrete chat/model/send authority, and implement/review any preservation path separately.

## Task 2 wiring contract

1. Construct one controller with context DataDirectory, an owner label including PluginId, `new DpapiCipher()`, and context Lifetime. Initialize registers factories/commands only; it performs zero business/storage I/O. Default in-memory demo is visible; OpenDemo/load/save are explicit local actions.
2. Keep controller and form drafts in the plugin instance. Factories create controls and disposable `Subscribe` handles; marshal snapshots onto the UI dispatcher and dispose subscriptions when a page unloads. Never call Start or send from factory/subscription/change events.
3. To Start, capture `MonitoringPreview preview = controller.PreviewMonitoring(request)` and show its Request, NextRead, PreviousCoveredThrough, AbandonedGap, retention effects, upload/rates/limits and selected connection. Pass that same preview to Start after confirmation.
4. To analyze, capture `AnalysisPreview? preview = await controller.PreviewAnalysisAsync(model)`, show its exact Batch/Task/Model, then pass the same object to AnalyzeCandidatesAsync after approval. No recomputing from editable controls after the dialog.
5. To send, capture `SendPreview? preview = await controller.PreviewSendAsync(name)`; show exact Markdown, DestinationName, masked DestinationFingerprint and prior attempt status. Pass the same preview after confirmation. `humanConfirmedRetry` needs a separate explicit warning/check of the destination: duplicate delivery is possible. Runtime previews are immutable, single-use, owner/generation/retention/credential bound and expire in five minutes. Any connection, Stop, credential change or editing of the relevant content invalidates old approval.
6. Ordinary read/search/discovery inputs are captured immutable request/scope/keyword values before confirmation. No saved credential value is read into an editable control. SaveCredentials replaces plugin-local entered values only, revokes credential use and does not alter the standalone vault.
7. Runtime Connect proves exclusion only for the exact canonical physical state file it leases. The production adapter must, on an explicit connection action, verify that the selected location is the standalone app's standard canonical location before claiming exclusion with that old app. Selecting a copied or arbitrary root cannot support that claim. Do not derive/read LocalAppData during initialization or isolated smoke.
8. Disable/exit calls DeactivateAsync and awaits retirement even when the host request token is already cancelled. Removal/updates happen after host exit/restart. No self-uninstall.

## Remaining Windows evidence

Task 1 Linux tests do not prove WinUI creation, DPAPI Windows interoperability, packaging or actual host lifecycle. Parent/Task 2 must build/test .NET 8 standalone changes, real DPAPI with synthetic values, native adapter .NET 10/WinUI 1.8, shared-assembly exclusion/private-dependency staging, ZIP install/trust/enable/recreated-navigation/settings/demo collection/search/analysis/manual-send-preview/disable/exit/relaunch/remove. Use isolated roots/fake services only; record actual host capabilities and process outcomes. Windows Server CI does not substitute for Windows 11 human acceptance.

## Task 2 delivered adapter and acceptance

The real API1 plugin, native code-created workspace/settings and immutable-preview dialogs are implemented. `CanonicalStoreLocation` defers standard known-folder resolution until Connect review and verifies the exact standard path before Runtime takes the old-compatible lease. The native one-shot read overload consumes a MonitoringPreview and fixes its exact NextRead end across dialog delay. JSON host preferences contain only model/limits; sensitive connection/task drafts use Runtime's bounded encrypted-document helper. Product resource observations count actual dispatcher subscriptions, operations, source/model/send dispatch and owned-store writes; MonitoringCompletion is the real supervisor loop.

The source-linked host/launcher/workflow and packaging rules are documented in `docs/workbench-plugin.md`. Native driver implementation is not executed Windows evidence. The adapter remains pinned to verified 6565180b while later implemented platform runtime-service changes await their own green Windows CI/review; no unsupported API use or auto-recovery authority is introduced.
