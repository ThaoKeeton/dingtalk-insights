# 持久人工核实（独立版0.2.1 / 插件0.3.3）

此前人工确认只改变当前预览，重开历史即丢失。本轮为独立版与插件加入同一有界闭环：选择当前报告或历史中的问题，回原会话核对证据和验收结果后确认解决；也可明确撤销该项人工确认。重开此报告会恢复最新人工记录及 UTC 时间，不需要重新调用模型。

确认仅绑定精确保存报告、问题内容及所引原文修订。不把模型的 `resolved` 视为人工确认，不传播到另一个报告，也不跨批次合并。索引发现证据变化/缺失后，失效标记持久保留；即使 A→B→A 也不恢复原确认。没有被重新读取的源端编辑/撤回无法由本机自动发现，仍须回钉钉核实。

## 使用与存储

独立版在“结果与发送”，插件在工作区选择问题序号。确认框绑定当时的报告与证据；取消不改状态。保存失败不显示持久成功。停止、历史选择、报告替换或证据变化后，旧确认框不能作用于新报告。核实与撤销没有模型、采集或发送操作。

`report-reviews.dpapi` 与 `state.dpapi` 使用同一 Windows DPAPI/用户和规范独占锁。每份报告保存经过结构与逐字引文校验的必要证据快照及每项最新人工记录，最多100份报告、每份512KiB、整文档16MiB。原始保存 Markdown、模型尝试、费用预留和发送账本不改写。超限/损坏会安全报核实不可用，保留损坏字节并只读显示保留期内原始文本，不能把损坏文档当成空文档覆盖。新报告的可选快照捕获失败仍保留原报告可读，但不宣称可持久核实。

缩短保留期、报告超出最近100份或明确清理索引，会清理相关核实。保留期使用规范账本的单调参考时间；延长保留期或时钟回退不能恢复已删除确认。清理索引保留当日预算及不确定发送尝试。

保存使用规范状态最终保留期清理后的同一时点：先持久保存证据失效/清理，再替换规范状态。核实维护失败先写入并刷盘 `report-reviews.dpapi.blocked`，所有严格核实读取都拒绝该屏障；即使旧独立版随后把来源 B 改回 A，也不会恢复旧确认。屏障本身写入失败则不提交来源变化。屏障只有明确清理索引才解除，不因下次读取成功而自动恢复核实。

若失败涉及到期或历史淘汰的证据清理，会拒绝该规范状态写入，不宣称物理清理成功。独立版可只读查看/导出仍在保留期内的原始历史，自动恢复不会读取凭据或启动 DWS；修复存储或明确清理索引后才能继续新工作。损坏/被占用文件仍待清理，不保证磁盘故障下能即时删除字节。插件接手规范独立版账本必须先持久撤销恢复权限，因此这类清理失败时会拒绝连接；先在独立版恢复存储或明确清理索引，再连接插件。插件自身模拟库的保留历史仍可只读查看/复制并明确清理。

## 两条应用路径与回退

新版独立应用和插件在停止、断开并释放锁后，使用同一物理数据目录可恢复同一核实。不得复制活动账本冒充交接。插件模拟库与独立版默认目录本来分离；自动化只用隔离合成目录明确交接，不导入真实用户数据。

旧纯 Markdown 历史不推断结构，仍可查看/导出，没有核实按钮的有效目标。旧独立版只重写 `state.dpapi` 时不会删除新核实文件，但不展示/更新它；返回新版仍校验现有来源和保留期。不能保证旧二进制正确处理新版能力；旧0.2手动发送不查询共享发送侧账，回退期间不要发送。包更新/回滚/卸载保留整个用户数据目录。

## 可复现合成验证

```sh
dotnet run --project tests/DingTalkInsights.Tests -c Release
python scripts/check_workbench_source_checkout.py --report artifacts/workbench-checkout/result.json
```

Windows PowerShell 使用固定 SDK/宿主27154465，保留所有原有版本与包维护门槛：

```powershell
./scripts/build-workbench-plugin.ps1
dotnet build tests/DingTalkInsights.Workbench.RuntimeTests -c Release -o artifacts/workbench-plugin/runtime-tests -p:ArtifactsPath=$PWD/artifacts/workbench-plugin/runtime-tests-build
dotnet artifacts/workbench-plugin/runtime-tests/DingTalkInsights.Workbench.RuntimeTests.dll artifacts/workbench-plugin/control-fixture/DingTalkInsights.Workbench.RuntimeFixture.dll
dotnet artifacts/workbench-plugin/tool/Microsoft.DingTalkInsights.PackageTool.dll test-maintenance artifacts/workbench-plugin/package
./scripts/test-workbench-interop.ps1
./scripts/test-workbench-plugin-native.ps1 -BuildDirectory artifacts/workbench-plugin -TimeoutSeconds 180
```

可移植用例覆盖保存/撤销、替换所有者、旧状态重写、模型降级、不同报告隔离、证据A→B→A、到期与时钟回退、损坏/容量/保存失败和旧确认拒绝。Windows 原生用例通过真实按钮及对话框检查独立版确认/取消、被占用 DPAPI 文件导致的保存失败、历史与进程重启；插件检查安装包的确认/撤销、页面重建、停用重启，以及包维护与解压后重复。所有输入和凭据均为合成值，发送仅本地替身。

## 本轮验证记录

提交前本地验证：可移植227/227、桥接48/48、包维护11/11、包策略5/5、SDK132项字节检查通过；独立版/插件/固定宿主的 Linux 引用编译均为0警告0错误。.NET8写入→.NET10读取/撤销/再确认→.NET8恢复的合成互操作通过。独立 Terra Max 审查复现并复核了跨文件故障、保留期时序、模拟发送来源与恢复权限修复，最终独立运行227/227通过。

Windows 验证由本分支的 [独立版工作流](../.github/workflows/windows.yml) 与 [插件工作流](../.github/workflows/workbench-plugin.yml) 执行。每份包的 build-info 和 Actions head_sha 标识其精确源码提交；完整运行/产物链接随本轮交付报告提供，不能用旧提交成功替代。Linux 引用编译只检查类型，不证明 WinUI/XAML/DPAPI 实际运行。未运行真实 Windows11、真实企业接口和长周期用户使用；未创建PR、合并或部署。

下一项独立缺口：同一来源已被监控或事项A尝试整理后，用户更换事项B仍会被现有未知结果保护拒绝再次整理。父任务已用 c0c1d33 合成控制器复现；本轮保留预算预留、未知结果不自动重试和确认边界，不顺手移除该保护。
