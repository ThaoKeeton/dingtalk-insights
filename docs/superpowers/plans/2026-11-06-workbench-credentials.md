# Workbench credentials Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline; the user authorized ordinary implementation decisions without repeated confirmation. One fresh whole-branch review follows implementation.

**Goal:** Complete explicit host credential use while preserving lifecycle ownership and durable duplicate suppression.

**Architecture:** Runtime owns async provider grants and dispatch validation. A public-SDK-only bridge adapts the pinned host secret store using a last-written commit manifest. Native pages invoke explicit save/use/revoke/delete actions and never receive stored values.

**Tech Stack:** net8 Runtime/Core/standalone; net10 Bridge; WinUI/AppSDK1.8.260921001; fixed Workbench82 public SDK.

**Spec:** `docs/superpowers/specs/2026-11-06-workbench-credentials-design.md`

## Global Constraints

- Work only in dingtalk-insights; preserve existing branches, standalone and data.
- Keep fixed SDK/host `27154465c08d132798c8ea7b637f137f7340a90b` and its44 immutable inputs; preserve SDK82 and656 rollback snapshots; no vendor edits.
- No live chats, real credentials or real service tests; all sending uses a local simulation.
- Never retry the denied draft PR. No merge or release publication is part of this task.
- Initialization/navigation/load never read values or restore business/purpose authority.

## Review Focus

- A batch interrupted before its final marker must never mix old and new values or silently fall back.
- A provider ignoring cancellation must retain operation/lease ownership and never grant or dispatch late.
- Changed credentials while a confirmation/readiness check is pending must invalidate the exact preview.
- Revoke/disable/close must retire actual model/send work; old attempts and UTC budget remain authoritative.
- Unsupported host protection and generic single-field edits must produce safe failure without exposing values.

### Task 1: Async provider and host batch boundary

**Files:** add Runtime `CredentialProviders.cs`, controller credential partial; add bridge `WorkbenchCredentialProvider.cs`; modify controller/previews/actions, bridge project; add `CredentialTests.cs` to RuntimeTests.

**Interfaces:** `ICredentialProvider` exposes async revision/read/save/delete. Optional controller host provider; `CredentialLocation.Host`; explicit location-aware save/delete and `RevokeCredentialsAsync`. Existing local overloads remain compatible. The host bridge registers the three existing IDs plus `credentials.commit`.

- [ ] Write tests for no initialization I/O, confirmation/purpose enforcement, revision-bound local-only dispatch, partial save/read/change/deletion, unsupported store and ignored provider retirement. Run RED for the missing interfaces, then implement and run portable/bridge suites GREEN.
- [ ] Add revoke-during-model/send tests, using the real durable attempts and canonical lease; confirm stale results cannot commit or resend after re-enable. Run RED then GREEN for any newly exposed defect.
- [x] Commit the tested Runtime/bridge boundary (45f7a5d).

### Task 2: Native integration and real host verification

**Files:** modify InsightPlugin/PluginSession/InsightPage, runtime fixture/ManagerTests, AcceptanceDriver; retain the real package and source-linked presenter scripts.

- [ ] Inject/register the public SDK provider; native save/use/delete select exact locations; explicit revoke stays responsive. Keep empty password controls and safe notices.
- [ ] Exercise actual Manager secret storage on Windows and synthetic unsupported storage on Linux, plus installed-plugin native synthetic save/approve/revoke/delete and page recreation. No model or send network call in native tests.
- [ ] Build/verify portable, bridge, package/checkout and compile-only Linux native references. Commit and push the existing authorized feature branch, inspect fresh Windows run and package artifacts.
- [x] Parent task-state steering: delayed SDK Stop cannot leave explicit stop as Completed; natural business failure reaches real Manager Faulted. Regression/mutation RED→GREEN;39/39 bridge checks (2b4940a).
- [ ] Integrate fixed original27154465 presenter/HostRuntime and public SDK; native removal/rollback replay must occur in original startup without an apply helper.

### Task 3: Review, evidence and handoff

- [ ] Fresh whole-branch review from original standalone ancestry through final head, focusing on authority, retirement, budgets/attempts and package boundaries. Fix material findings with RED→GREEN and appropriate full suites.
- [ ] Document exact head, fixed SDK/CI/package checksums, reproducible commands and actual scope. Preserve incomplete shipping-platform/Windows11/live/multiday gates explicitly; do not claim them verified.
- [ ] Final verification and report code, evidence, decisions and remaining blockers. Do not create a PR.
