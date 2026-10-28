# Windows 任务栏 Codex 额度

设置 → 任务栏 → **显示 Codex 剩余额度**，默认开启。tok/s 数值旁显示两行：

```text
5 小时  31%
周额度  89%
```

百分比表示剩余额度。开启后立即查询，随后每 3 分钟刷新。关闭开关、隐藏任务栏组件、切换到仅图标模式或关闭全局联网后停止查询并取消正在进行的读取。重新开启后立即查询。

使用本机正在运行的 Codex CLI，找不到时从本机安装目录和 PATH 查找 `codex.exe`。通过隐藏的 `codex app-server --stdio` 子进程完成 `initialize` → `initialized` → `account/rateLimits/read`，复用 Codex 自己的登录及网络配置。只读取额度，不创建对话或发起模型请求。读取完成或取消后结束自己启动的子进程，单次读取最多等待 30 秒。

优先采用 `rateLimitsByLimitId.codex`，兼容 `rateLimits`；通过 `windowDurationMins=300/10080` 区分窗口。`100-usedPercent` 限制在 0–100%，向下取整显示。缺失、未登录、API key 账户或读取失败显示 `—`，不把未知数据当成 0%。不会把会话 token 数推算成账户额度。设置保存在 `taskbar.codexQuota`。

查询只通过 Codex 官方接口完成，不上传到额外的第三方服务。Agent Speed 不读取、复制或记录登录令牌，不记录账户 ID 或 RPC 原文。额度数值和错误类型会写入本机诊断日志。

协议参考：[OpenAI Codex app-server](https://learn.chatgpt.com/docs/app-server)。
