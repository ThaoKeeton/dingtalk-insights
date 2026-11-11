# Persistent human verification Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline; one fresh independent whole-change review before final verification/commit.

**Goal:** Make explicit human verification survive history selection and restart in standalone and plugin.

**Architecture:** Core owns bounded, evidence-bound snapshot/decision models; Runtime BusinessStore persists a separate encrypted review archive under the existing lease. Both presenters use the same archive, without changing canonical original reports or monitor/query attempt semantics.

**Tech Stack:** C#12/net8 Core+Runtime+standalone; existing net10 WinUI plugin, fixed27154465 SDK/host.

**Spec:** `docs/superpowers/specs/2026-11-09-durable-review-design.md`.

## Global Constraints

- Preserve original state format, standalone/plugin paths, credentials, budget and durable attempt ledgers.
- No SDK/host upgrade or vendor edits; no new credential authority.
- Synthetic/local tests only; no business interfaces, messages, user computer, PR, merge or deploy.
- Public Actions authorization applies only to source and sanitized synthetic validation.

## Review Focus

- Reordered/different evidence or a different saved report must not inherit a confirmation.
- A dialog opened before Stop/selection/reopen must not confirm a replacement view.
- Storage failure must not mutate the visible Issue or lose prior decisions.
- Expiry/clear and old-state rewriting must preserve appropriate retention and budget/send protections.
- Oversized/corrupt/legacy records must not silently become reviewable or overwrite valid review data.

### Task1: Shared encrypted archive and proof model

**Files:** add Core `ReportReview.cs`, Runtime `ReportReviewArchive.cs`; modify BusinessStore; add portable `ReportReviewTests.cs`.

**Interfaces:** Capture a persisted original AnalysisReport as a bounded snapshot; Open an exact SavedReport as `ReportReviewView`; Decide using its fingerprint/issue hash and explicit boolean; prune/clear under the owner store. Original SavedReport values stay unchanged.

- [x] Write behavioral tests, observe missing-interface/behavior RED, implement and run portable tests GREEN.
- [x] Cover persistence across a new owner, confirm/revoke/idempotency, independent report/revision, bad data/capacity/write failures and retention/clear. Keep the tested shared boundary uncommitted until independent review.

### Task2: Both native paths and controller authority

**Files:** controller report/actions/previews/contracts; standalone report/review helpers; InsightPage; native smoke/acceptance tests.

**Interfaces:** Runtime immutable resolution preview + confirmed apply. History restores structured reviewable reports when present, with text-only legacy fallback. New reports capture snapshots through shared store. Both UIs expose confirm and revoke, and explain persistence.

- [x] Add controller stale-preview/write-failure/restart tests RED then implement GREEN.
- [x] Add synthetic real-control standalone/plugin review/history/restart assertions; update package versions/fixtures and docs without touching SDK pins.

### Task3: Independent review and delivery

- [x] Fresh Terra Max read-only review; material findings fixed and independently227/227 verified.
- [x] Run portable, bridge, package/checkout and appropriate Linux reference compilation.
- [ ] Public Windows Actions for the committed branch; inspect exact commit results and packages.
- [ ] Report selected gap, code/commit/test evidence, actual remaining gates and any action blockers; no PR/merge/deploy.
