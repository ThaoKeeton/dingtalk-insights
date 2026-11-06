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
