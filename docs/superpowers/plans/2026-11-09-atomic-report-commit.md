# 报告与模型快照提交 Implementation Plan

> 使用 `superpowers:executing-plans`；用户已授权自主实施，提交前独立审查。

**Goal:** 已提交的规范报告在进程中断后仍可恢复其原始模型状态。
**Architecture:** 同锁加密提交日志先于规范状态，成功后合并原核实账本；容量淘汰延后，保留期清理、失效和撤权独立保证。
**Tech Stack:** .NET 8 Core/Runtime、WinUI 3、Windows DPAPI、固定 Workbench SDK。
**Spec:** `docs/superpowers/specs/2026-11-09-atomic-report-commit-design.md`

## 任务

- [x] 用按需和监控的合成报告重现 UI 回调前退出导致原模型状态丢失。
- [x] 在引擎和插件受控存储边界写入/恢复精确加密日志，验证成功、孤儿、冲突、损坏、缺失及幂等恢复。
- [x] 验证 100 条报告淘汰、到期清理、不同写入阶段失败与状态替换后异常；不丢失仍保留的人工确认。
- [x] 验证停止/撤权、取消、预算预留、未知模型尝试及发送记录；旧版重写/清空只使用虚构数据。
- [ ] 独立审查、完整本地回归与公开 Windows Actions；提交和产物按精确 SHA 报告，说明真实接口和断电未验证。本地 Release 合成套件已覆盖成功淘汰、改写提交标识、重复行冲突、孤儿日志、失效标记幂等、取消后的预留和未知模型结果。公开 Actions 只对应该提交的检查，不能当作 Windows 11 24H2 人工验收；断电与真实接口仍未验证。
