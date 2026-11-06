# DingTalk Insights

独立 Windows 原生应用：持续检查选定钉钉会话，发现可能的沟通难点；也可以输入整理事项，搜索相关消息后形成带来源的待核实摘要。界面查看、导出 Markdown，按需发送到已确认的钉钉 Webhook。

首版使用 WinUI 3 与 .NET 8。默认模拟模式，不自动登录、采集、上传或发送。适用于 Windows 10 19041+ / Windows 11 x64。

## 两种用法

- **持续检查**：在“连接与设置”指定官方 DWS、明确身份和单一会话，在“持续检查”设置开始时间、间隔及是否允许模型上传，确认后运行。完整批次才推进断点；重新开始从断点追赶，保留两分钟重叠并去重。模型每次处理最多 40 条新增或修订消息，结论都是待核实候选。
- **按需搜集整理**：输入整理事项，核对关键词、会话与时间；选择搜索本机索引，或确认一次 DWS 搜索。先查看实际候选原文，再确认是否交给模型。搜索可能遗漏同义表述和范围外内容，不表示全面覆盖。

当前窗口关闭、电脑休眠或断网时不会继续实时采集。首版不是 Windows 后台服务；没有托盘、自启动、自动更新、附件正文读取或自动发送。

## 先试模拟模式

1. 打开应用，保持默认模拟来源，点击“检查一次并预览原文”。
2. 在“按需搜集整理”核对模拟消息，再点击“整理当前候选”。
3. 在“结果与发送”查看问题、待确认事项与消息 ID、时间、发送人、原文引用。
4. 模拟发送只显示预览，不会请求真实 Webhook。无需任何真实密钥或聊天记录。

## 连接真实服务

用户已在本机验证 DWS 可行。新电脑仍需独立安装[官方 DWS](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli)，在自己的电脑完成登录并核对企业授权。详情及核对过的固定命令见 [DWS 接入边界](docs/DWS.md)。本应用不复制 DWS 登录，也不从钉钉客户端数据库提取数据。

先用一个已获授权会话和短时间范围核对原始消息，再按公司政策决定是否允许上传 OpenAI。应用使用专用 OpenAI API 密钥；通过安全流程创建后，在本机设置页录入，确认后由 Windows DPAPI 加密保存。已保存凭据不会回显，不要将密钥、Webhook 或真实聊天提交到仓库。

默认低价模型是 `gpt-6-luna`，可修改模型和单价。默认每日费用预留上限 USD 0.50、最多 200 次请求、每次最多 2000 输出 tokens。输入/输出参考价为 USD 0.10/0.50 每百万 tokens，核对日期 2026-11-06，见 [OpenAI 官方模型价目](https://developers.openai.com/api/docs/models/gpt-6-luna)。费用在请求前保守预留，失败或崩溃不返还，避免自动重复扣费；该限制不是 OpenAI 平台账户的全局消费上限，单价需按实际模型核对。

Webhook 每次都预览完整正文和明确的接收群，再确认发送一次。支持官方自定义机器人 HMAC 加签，禁止全员提醒，禁止重定向。超时标记为结果不确定，不会自动重发。

## 本机数据与结果可信度

消息、断点、修订去重、模型尝试记录、费用预留及最近结果加密保存到 `%LOCALAPPDATA%\DingTalkInsights\state.dpapi`；凭据保存到同目录 `credentials.dpapi`。普通设置不含密钥。默认保留 30 天，最多 50000 条消息、32 MiB 状态及 100 份结果。超限、保存失败、缺页、权限失败或预算耗尽时暂停。

消息正文和模型输出都当作不可信数据；正文中的命令不执行，模型没有工具。每个问题必须引用实际消息 ID 和逐字原文。模型提出“已解决”也先降为待验证；只有人工回原会话核实后，才能在当前结果中确认解决。引用匹配不能证明模型理解正确，候选结论仍需核实；不进行人员能力、人格、动机或绩效评价。

本地保留期、可见历史、分页、索引延迟、附件、撤回和较早编辑会限制结论，完整分页也不代表完整审计。完整架构与限制见 [设计说明](docs/design.md)。

## 开发与验证

核心没有第三方运行时依赖，Linux、macOS、Windows 均可运行合成测试：

```sh
dotnet run --project tests/DingTalkInsights.Tests -c Release
```

Windows 构建：

```powershell
dotnet publish src/DingTalkInsights.Desktop/DingTalkInsights.Desktop.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist/app
```

GitHub Actions 的 [Windows native MVP](.github/workflows/windows.yml) 运行核心测试、自包含构建、真实 WinUI 控件与 DPAPI 合成检查，并从解压后的 ZIP 再执行一次原生检查。通过后在 Actions 产物下载 `DingTalkInsights-Windows-x64`，完整解压内部应用 ZIP 后运行 `DingTalkInsights.exe`，保留所有 DLL 与 PRI。产物未进行 Authenticode 签名。

代码和测试均使用合成消息及合成凭据；持续监控、真实 DWS 可见范围、真实模型用量和 Webhook 接收仍需用户在授权本机验收。第三方许可说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
