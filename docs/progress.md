# Implementation ledger — plan: docs/superpowers/plans/2026-11-06-windows-foundation.md

授权：用户要求实施基础版，并明确继续不依赖密钥的工作。新密钥已选择独立创建，仓库外持久保存仍待用户确认。

Pre-flight: Task 1 produces MessageBatch/AnalysisReport for Task 2 and Task 3. Task 2 produces scoped single-use consent and external service methods for Task 3. No conflicting interfaces found.

Ruling: 选择单一 .NET 核心与 WinUI 3，保持 UPCAM 原生风格而不复用 Python worker — 减少首版打包和进程协议复杂度 — 代价为不能直接复用 UPCAM 的 Python 业务模块。

Ruling: 在干净的任务专属仓库创建 feature branch，不额外创建 git worktree — 当前仓库仅有初始 README，无需共享其他任务工作区 — 代价为同一 checkout 只承载本功能。

Ruling: 按用户明确要求连续执行实施和模拟测试，不再为设计/执行方式追加审批；密钥和外部真实动作仍逐项确认。

需求更新：用户已在本机验证 DWS 可行，重新启动项目，增加长期低价模型监控与按需搜索整理两条流程。创建凭据仍待具体安全设置确认，开发不依赖真实密钥。

Task 1: complete — 数据范围、完整性与证据校验测试 21/21。
Task 2: complete — 单次确认、OpenAI 请求、Webhook 目标/签名/超时和 DWS 固定命令合成测试通过。
Task 3: complete — 核心 47/47 合成测试通过。覆盖缺口、去重修订、加密恢复、保存失败、请求前预留、未知结果重启不重试、UTC 日期回退的 RED→GREEN 修复均已验证。
Task 4: in progress — WinUI 3 页面、原生控件 smoke 与 Windows 打包工作流已实现，待 Windows CI 实际编译与运行验收。

Ruling: 首版采用显式启动的应用内时间窗口轮询，不引入 DWS 长连接事件订阅 — 当前目标优先可恢复增量、可核对范围和费用控制 — 代价为无法保证实时捕获较早消息的编辑与撤回，界面与文档明确提示。

Ruling: 模型不明结果保留尝试记录及费用预留，不自动重试同一消息修订 — 防止重启重复扣费 — 代价为该批次需要用户核对后在按需流程显式重试。

Repair 2026-11-06: 核心 73/73 合成测试通过，包含原有六项 RED→GREEN 回归与新保留期、恢复、未来日期、加密回执覆盖。修复 HTTP 正文期限、A→B→A 尝试保护、DWS 完整性范围/终止验证、持久监控起点和报告历史。保留期在启动、保存、使用前执行，清索引保留预算；历史报告按最早来源到期。人工确认明确标为当前预览临时状态。

Repair native: 已修复 CS0252 引用比较与设置页 FrameworkElement.IsEnabled 编译错误（改用 Control 宿主）。原生合成 smoke 增加手动失败不停止监控、监控失败暂停、设置键盘禁用以及独立重启恢复历史/起点/到期清理。9201066 的 Windows 实际编译、DPAPI/控件 smoke、自包含 ZIP 和解压后重启 smoke 已由父任务核对 run 42187070409 通过；不代表后续新增输入/故障场景已覆盖。无真实服务调用、无新凭据、无推送或合并操作。

Fix round 1/5 — R1/R2: 历史选择事件本地捕获保留/保存失败，过期预览先失效，列表清理后同步刷新并抑制重入；失败选择不展示报告、不取消无关监控。原生合成 smoke 持有不允许删除共享的 state.dpapi 句柄，强制真实保存失败，检查过期/有效历史均不错误展示、监控存活、释放后可恢复选择，随后仍检查轮次失败暂停。

R2 的三条最短一天保留回归 RED 73/76 → GREEN 76/76（Release），包含 2034-03-02 10:00 UTC+8 的范围、入库和本机搜索，以及午夜、早晨、夜间参考。模拟完整范围改为窗口捕获参考时间前的三小时；真实保留规则不变。原生正常 smoke 保存一天保留设置后执行模拟读取/搜索/开始。此轮精确提交的 Windows 编译与新增原生故障/一天保留检查仍待父任务 CI，不依赖此前通过记录。

## v0.2 background-monitoring worktree — 2026-11-06

Design/plan: `docs/background-monitoring.md`. Implemented a core exact-configuration recovery policy and serialized supervisor, conservative structured DWS retry classification, bounded persisted health/backoff, UTC budget deferral, AI suppression for uncertain/authentication failures, cancellation retirement, native notification-area lifecycle and App SDK single-instancing. Explicit Stop revokes recovery; Exit follows a separately approved policy. No service/autostart installation or real external calls.

Release synthetic suite: 111/111 on Linux. Offline official DWS v1.0.67 help/schema check: exit 0. Native Windows publish, controls/DPAPI/background/restart/activation and extracted ZIP smoke are implemented but pending parent-run Windows CI at the fixed commit. Actual Shell_NotifyIcon exercise is reported separately from mandatory unavailable-tray fallback. v0.1 branch/artifact remains unchanged.

Ruling: use stable DWS corp_id:user_id and app-vault ciphertext revision; official readonly status has no reliable same-identity login-generation field. OAuth expiry changes must not force periodic consent. Matching identity/readiness and structured read authorization failures remain authoritative; same-identity re-login detection is a documented limitation.

v0.2 review corrections: portable Windows fixtures committed separately at `4c94eca` (112/112 Linux Release; parent Windows core/publish and real-shell/activation/fallback checks passed before a Stop-smoke assertion race). Pending-start cancellation now signals before the lifecycle gate and rejects stale generations; model reservations sample the current injected UTC date at the shared dispatch boundary. Queued-context and midnight/restart regressions plus shared one-off analysis raise Release coverage to 119/119. Stop smoke awaits the full host Stop task and checks persisted revocation; the four-message dedup requirement is retained. Final Windows verification is pending at this correction head.

v0.2 final integration: reproduced a queued initial native configuration continuation that could issue a synthetic read after completed Stop. The host now owns and retires startup/restore before Stop/Exit completion, guards authority writes and links pending supervisor cancellation. Pinned DWS DualValidate legacy errors now cross the actual bounded stderr capture/classification boundary into persisted, finite read backoff; uncertain/partial/auth/oversized results remain conservative. Production-linked host and fake-process regressions: RED 145/152 → host GREEN 150/152 → combined Release GREEN 152/152. No CLI arguments changed; no real chats, model/Webhook calls, credentials or startup/service settings were used. Windows native/ZIP verification and one scoped re-review at the new commit are pending with the parent.

2026-11-09 持久人工核实：基于已核实本地/远端 c0c1d33，在 codex/durable-review 独立分支实现独立版0.2.1及插件0.3.3的精确报告确认/撤销、历史恢复和跨应用接手。原始报告/账本格式、SDK27154465、预算与发送尝试不改。跨文件故障使用持久失效屏障；必须清理的核实失败阻止规范写入和自动恢复，旧恢复权限不能持久撤销则拒绝插件接手。独立审查后本地227/227、桥接48/48、包维护11/11、包策略5/5、SDK132字节检查与三条Linux引用编译通过；合成net8→net10→net8核实互操作通过。实际Windows/DPAPI与完整包证据以本次精确提交Actions及交付报告为准。范围/限制见 [持久核实说明](durable-review.md)。

2026-11-09 按事项整理：基于 f8d198a4、分支 codex/task-aware-analysis，修复插件因全局来源尝试隐藏新事项及独立版同事项未拦重的双重缺口。同事项/来源修订持久去重；不同事项复用旧来源须先明确确认可能再次计费，再确认精确范围。旧版歧义保守提示，预算与发送保护不变。合成回归、固定宿主 Windows验收及精确提交见本轮交付记录，行为与回退边界见 [按事项整理](task-aware-analysis.md)。

2026-11-09 常规整理：基于 7b03098，保留并展示模型原始解决判断，明确不等同人工确认；人工核实为可选操作。原规范 Markdown 和旧快照仍可读取，来源变化时结果标为历史。合成新旧版本交接及原生验证见 [模型判断说明](routine-model-assessments.md)。
