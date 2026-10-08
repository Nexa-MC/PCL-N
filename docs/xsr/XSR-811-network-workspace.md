# XSR-811 — 网络诊断工作区

设置中的网络 trace 是有界事实查询，生产路由为 `network.diagnostics.trace`。
最多保留 256 条 hostname、请求或 probe 类型、状态码、耗时与错误分类；不保留 URL
路径、查询参数、正文、认证头或代理凭据。Desktop 从 typed query 读取不可变快照，
显式刷新并显示最近 32 条；不在渲染器中解析服务或发送探测请求。关闭 trace 与自动
诊断后，传输层清空历史。自动 probe 保持已有取消、间隔和数量预算。

验证涵盖禁用 provider、脱敏 trace 和 query 路由；云环境不证明生产网络质量。
