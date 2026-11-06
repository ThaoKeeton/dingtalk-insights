# Workbench continuation verification

The resumed task verified remote `codex/workbench-plugin` baseline **b50586ff321f9932a35235de75f8c9b4996a5a61** and successful [Windows run42284239767](https://github.com/ThaoKeeton/dingtalk-insights/actions/runs/42284239767). No uncommitted work from the disconnected environment was assumed present. Existing standalone, canonical data format, lock, budgets and uncertain attempts were retained. The earlier denied draft PR was not retried.

Plugin0.3.2 uses fixed platform **27154465c08d132798c8ea7b637f137f7340a90b**, tree5d00a28c12e85c1bf0f77139c7c6524748c13223. [Platform CI42285023349](https://github.com/ThaoKeeton/project-workbench/actions/runs/42285023349) passed separately. All44 imported inputs match their Git blob identities/SHA256;132 checkout-filter checks passed. Public SDK contracts/AssemblyVersion match SDK82. SDK82/656 archives remain unchanged.

## Implemented and checked

- Host/local/connected credential selection; separate purpose approval; revision-bound async reads; explicit save/delete/revoke; no initialization/navigation/load value access or fallback. Host batch commit marker refuses interrupted or externally mixed fields. Connected standalone vault remains read-only.
- Actual controller/provider/transport ownership through ignored cancellation. Revoke/terminal retirement preserves model budget and uncertain model/send attempts through a fresh owner. No live Webhook transport is used by any new test.
- Parent integration status fix **2b4940a**: explicit page Stop retains its registered worker through delayed SDK persistence until cancellation admission. It never awaits its own registry Stop. Natural business NeedsAttention reaches real Manager **Faulted/BackgroundTaskFailed** rather than Completed. A removed-handshake mutation fails the delayed-Stop regression; restored suite passes.
- Fixed original HostRuntime applies pending work under its physical lease before Discover/Restore. Native removal/rollback no longer use an apply helper. Original management/presenter bytes are unchanged; the generated-XAML acceptance executable retains an isolated test bootstrap.

Local checks after review fixes: **200/200 portable**, **48/48 bridge/actual Manager**, **11/11 actual-package fresh-process maintenance/recovery**, **5/5 package policy**, **132/132 exact checkout**. Linux native/plugin references compile with0warnings/errors; XAML/PRI are deliberately disabled and the temporary InitializeComponent stub is compile-only.

## Whole-branch review and fixes

A fresh independent reviewer read the active branch from initial main a4da5b2 through5d008ed. It confirmed two Important async dispatch defects with local synthetic reproductions: credentials awaited after reservation could cross UTC midnight, and content could expire while credential I/O was pending. Nine new dispatch-boundary cases reproduced five failures before the fix. Async credentials now finish before engine reservation; supervised models use an explicit preparation hook; actual transport invocation has no further async credential work and rechecks monotonic retention. Fresh-owner tests enforce the dispatch day's request cap. All48 bridge and200 portable checks pass after the fix. No Critical or Minor finding was reported.

Windows run [42293943581](https://github.com/ThaoKeeton/dingtalk-insights/actions/runs/42293943581) at5d008ed passed real generated-XAML publish,39 bridge checks,11 package checks, net8/net10 interoperability and native synthetic credential/page/monitor controls. It failed at the close observer's old non-generic TaskCompletionSource cast. The observer now reads the fixed original presenter's current TaskCompletionSource<bool> at genuine closing/destruction/exit boundaries, including its per-attempt replacement; it does not initiate cleanup or substitute synthetic close callbacks. A fresh exact-commit Windows matrix is required for the final deliverable; use its build-info.json, source-provenance.json, inventory.json, zip-sha256.json and evidence/summary.json as the artifact's source of truth.

## Reproduce on Windows x64

Install .NET SDK10.0.403 and8.0.427. From this repository in PowerShell, use a fresh artifacts/workbench-plugin output directory (the build refuses overwriting an old owned build):

```powershell
python scripts/check_workbench_source_checkout.py --report artifacts/workbench-checkout/result.json
dotnet run --project tests/DingTalkInsights.Tests -c Release
./scripts/build-workbench-plugin.ps1
dotnet build tests/DingTalkInsights.Workbench.RuntimeTests -c Release -o artifacts/workbench-plugin/runtime-tests -p:ArtifactsPath=$PWD/artifacts/workbench-plugin/runtime-tests-build -p:UseSharedCompilation=false
dotnet artifacts/workbench-plugin/runtime-tests/DingTalkInsights.Workbench.RuntimeTests.dll artifacts/workbench-plugin/control-fixture/DingTalkInsights.Workbench.RuntimeFixture.dll
dotnet artifacts/workbench-plugin/tool/Microsoft.DingTalkInsights.PackageTool.dll test-maintenance artifacts/workbench-plugin/package
./scripts/test-workbench-interop.ps1
./scripts/test-workbench-plugin-native.ps1 -BuildDirectory artifacts/workbench-plugin -TimeoutSeconds 180
```

Focused platform reproductions use the same built test executable/fixture followed by a name filter: `"explicit plugin Stop"`, `"actual Manager maps a natural business monitor failure"`, or `"dispatch boundary"`. The first holds SDK Stop before cancellation, verifies the delegate remains owned, then releases it and expects Stopped. The second injects a synthetic configuration fault and expects real host Faulted with safe metadata. The third controls only synthetic credential transport/clock and uses real controller/Core/store/budgets.

The clean distributable is **DingTalkInsights.Workbench-0.3.2-win-x64.zip**. The separate **DingTalkInsights.Workbench.AcceptanceHost-win-x64.zip** is a complete source-linked acceptance host, not the shipping platform application. The GitHub workflow uploads both with generated XAML and evidence. Use the fixed platform commit/its distribution when integrating the ordinary host; no SDK/Core/WinUI/EXE/PRI payload belongs inside the plugin package.

Windows11 manual keyboard/theme/DPI/accessibility, unchanged shipping EXE execution, real enterprise/model services and multiday operation remain separate. No real chats, keys or live service calls were used. Real-interface testing must not use Webhook or send DingTalk messages. See [manual record](workbench-native-manual.md).
