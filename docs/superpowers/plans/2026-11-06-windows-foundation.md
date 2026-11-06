# DingTalk Insights Windows Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 交付可运行的 WinUI 3 Windows MVP，支持持续增量检查和按需搜集整理，使用模拟数据验证断点、成本、证据与确认发送边界。

**Architecture:** .NET 8 核心负责 DWS 契约、证据验证、Responses API 与 Webhook，WinUI 3 负责原生工作台和逐次确认。凭据由 Windows DPAPI 保存，原文默认只在内存。

**Tech Stack:** C# 12, .NET 8, WinUI 3 / Windows App SDK 2.6.5, System.Text.Json, HttpClient。

**Spec:** [../../design.md](../../design.md)

## Global Constraints

- Windows x64、Windows 10 19041+；界面为中文。
- 默认模拟模式；持续检查只在用户确认启动后、当前窗口运行期间执行，无自动发送。
- 模型的 resolved 一律变为 pending_verification。
- 原文仅内存与本机 DPAPI 加密索引保存，导出需用户显式选择。
- 所有开发测试仅用合成消息和合成凭据，不上传真实聊天或发送真实消息。

## Review Focus

- DWS 缺完整性字段、失败退出、混入不同会话、时间边界、重复 ID：拒绝或保留不完整。
- 模型伪造 ID/引用、resolved、截断/拒绝响应：拒绝无证据结果，状态需人工确认。
- 取消确认、确认后改载荷/目标、超时：禁止未确认请求和自动重复发送。
- 秘密出现在异常、持久设置、子进程参数：仅 DPAPI 凭据、泛化错误和参数白名单。
- 无 DWS、默认语言、解压 ZIP 后首次启动：应用仍能打开并运行模拟链路。

### Task 1: 本机数据与证据契约

Files: `src/DingTalkInsights.Core/{Models,DwsParser,Analysis,Demo}.cs`; `tests/DingTalkInsights.Tests/Program.cs`。

Interfaces: `QueryScope(profile, conversationId, start, end)`；`DwsParser.ParseMessages(json, scope, exitCode)` → `MessageBatch`；`AnalysisValidator.Validate(json, batch)` → `AnalysisReport`。

- [ ] 写测试并观察失败：完整性缺失、不同会话/时间、重复消息、伪造证据、resolved 降级、人工确认。
- [ ] 实现范围验证、DWS 稳定契约解析与合成数据、严格整理结果校验和 Markdown。
- [ ] 运行 `dotnet run --project tests/DingTalkInsights.Tests`，全部通过后提交。

### Task 2: 外部服务与逐次确认

Files: `src/DingTalkInsights.Core/{DwsClient,Consent,OpenAiClient,WebhookClient}.cs`；扩展核心测试。

Interfaces: `ConsentGate.Issue(action, fingerprint)` / `Consume(token, action, fingerprint)` 单次许可；服务方法消费绑定当前载荷的许可；HTTP transport 可替换供模拟测试。

- [ ] 写失败测试：未确认零请求、改载荷许可失效、HTTP 重定向与错误脱敏、Responses 结构、Webhook 加签和不重试。
- [ ] 实现固定命令/端点、限额、超时、取消、错误信息与单次发送。
- [ ] 全套测试通过后提交，不调用真实服务。

### Task 3: 增量监控、加密状态和搜索

Files: `src/DingTalkInsights.Core/{StateStore,MonitorEngine}.cs`，`tests/DingTalkInsights.Tests/MonitorTests.cs`。

Interfaces: `InsightState.Ingest` 合并原文与完整覆盖区间；`MonitorEngine.NextScope` 从连续覆盖断点读取一小时窗口、重叠两分钟；`TickAsync` 保存状态后才分析；`LocalSearch.Find` 仅查所选身份、会话、时间。

- [ ] 写失败测试：重叠去重、修订、不完整不能推进、覆盖缺口、费用上限、UTC 日期回退、模型结果不明及重启、保存失败不能调用模型。
- [ ] 实现 DPAPI 可插拔加密存储接口与原子替换、单实例文件锁、每日预算预留、模型尝试标识、30 天默认保留与容量上限。
- [ ] 全套测试通过，不调用真实服务；把纯模拟分析与真实费用计算区分。

### Task 4: Windows 原生应用与构建交付

Files: `src/DingTalkInsights.Desktop/*`，`.github/workflows/windows.yml`，`README.md`，`docs/DWS.md`。

Interfaces: 工作台消费前两项；DPAPI CredentialStore 仅在用户保存/使用时操作；原生 smoke 仅模拟数据且产生退出码及截图。

- [ ] 实现持续检查、按需搜集、结果与发送、设置、来源/时间选择、证据核对和确认对话框；主题默认跟随系统，不覆盖原生语言引导。
- [ ] 核心全套测试；Windows CI 构建、自包含 ZIP、解压后原生启动/交互 smoke、合成 DPAPI 测试。
- [ ] 独立代码审查，修复重要问题并重跑有关验证。
- [ ] 提交分支和 draft PR，报告构建产物与仍需用户进行的 DWS 企业授权、真实密钥配置和实际服务验收。
