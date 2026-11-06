# DWS 接入核查

核查日期：2026-11-06。官方稳定发行版：[v1.0.67](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli/releases/tag/v1.0.67)。开发环境下载 Linux 官方二进制并校验发行 `checksums.txt`；仅执行 help/schema，不登录或读取真实聊天。

官方 [Getting Started](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli#getting-started) 要求企业管理员开通 CLI 访问。普通成员可选择组织并申请，管理员批准后再登录。用户已在本机验证可行，不等同于本开发环境已具有企业身份和聊天权限。

## 固定命令边界

- 使用明确的 `--profile=corpId:userId`，不修改全局默认身份，不猜测组织或用户。
- 会话发现：`chat +recent-conversations --start=... --end=... --page-limit=10 --format=json`，仅显示有消息的会话摘要；名称未知仍显示稳定 ID，列表不完整则停止选择。
- 时间读取：`chat +chat-messages --conversation-id=... --start=... --end=... --order=asc --page-all --page-limit=10 --max-items=1000 --format=json`。`--conversation-id` 是官方隐藏兼容别名，直接绑定稳定会话 ID；实现使用只读 `list_conversation_message_v2` 且 `mark_as_read=false`。
- 按需搜索：`chat +search-msg --group=... --query=... --start=... --end=... --order=asc --page-all --page-limit=10 --limit=100 --format=json`。官方 v1.0.67 搜索不接受 `--max-items`，因此使用每页 100 条、最多 10 页。不会在失败后扩大成全部会话检索。
- 不使用消息发送、已读清理、撤回、升级、auth export/import 或任何密钥读取命令。DWS 可执行文件由用户独立安装；应用不执行在线安装脚本。

程序使用 `ProcessStartInfo.ArgumentList` 和 `UseShellExecute=false`；所有选项单独传递。聊天正文从不作为命令或参数执行。子进程标准错误不会写入应用日志；输出有 4 MiB、5 分钟和取消上限。

## 契约与可见范围

官方 [消息读取说明](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli/blob/v1.0.67/skills/multi/dingtalk-chat/references/chat/message-query.md) 与 [稳定契约](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli/blob/v1.0.67/skills/multi/dingtalk-chat/references/contracts.md) 是解析依据。仅接受 `im.message-list.v1`，保留消息 ID、会话 ID、发送人及 ID、时间、正文和原始完整性 ledger。

只有明确 `complete=true`、`paginationKnown=true`、`hasMore=false`、无 partial/截断/失败且退出码 0 时，才允许该批次推进覆盖断点。补充截断字段存在时必须为正确的 false 布尔值；下一页游标不能与完整性矛盾。必须匹配官方 queryRange 的 startTime/endTime、order=asc 与 semantics=[start,end)，即使返回空列表也校验。终止原因只接受本应用固定升序读取可产生的 source_complete 或 range_end；缺失、未知、page_limit、降序 range_start 不推进。字段缺失、非零退出或未知形状按不完整/拒绝处理。不同会话、时间范围外消息和冲突 ID 被拒绝，不静默纳入。

DWS 无时区时间按该接口约定 UTC+8 解释，带时区时间保持原始时区；查询为 `[start,end)`。分页耗尽不证明服务端历史、索引、附件或撤回记录完整。资源不下载，不把已提交修复当作已验证解决。

企业权限和可见范围仍应由用户在本机用一个已获授权的会话、短时间范围与钉钉原会话逐条比对。开发测试不创建登录、不复制用户凭据，也不代替这种实际授权验收。

终止原因与范围的直接依据：[message_time_range.go](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli/blob/v1.0.67/internal/shortcut/smart/message_time_range.go) 的 metadata()/stopReason()（RFC3339Nano、[start,end)、升序 range_end/降序 range_start），以及 [chat_messages.go](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli/blob/v1.0.67/internal/shortcut/smart/chat_messages.go) 的 source_complete 与终止时移除 nextPage。搜索结果仍强制视为关键词子集，不推进全量覆盖。
