# 505b9f9f 审查跟进

审查来自代码阅读，未运行构建或测试，也未覆盖 VCDIFF、Jvm.Host native、OAuth
和 Avalonia accessibility。以下分开记录代码事实、修复范围和仍缺少的证据；不把
静态审查或 CI 全绿当成 Alpha.6 / Beta 的全部验收结果。

| 发现 | 当前核对与处理 |
| --- | --- |
| 分发 PFX 私钥 | 发布 workflow 从 secret 恢复 PFX，Desktop 可嵌入并读取私钥。这种共享客户端身份不能证明请求来自可信安装。轮换、每安装令牌、服务端校验与限流需要 API 服务端改动及部署证据，尚未关闭；不通过移除认证让现有联网流程绕过服务端策略。 |
| Forge / NeoForge 安装器 | 原校验请求已使用官方 `.sha1` URL，安装器字节可由镜像提供；“同一个镜像给出两者”不符合当前默认路径。修复增加来源重定向检查、官方安装器来源和缓存策略版本；本地显式选择安装器保留原路径。 |
| Minecraft 元数据 | 原实现是官方优先、失败后回退镜像，且未验证 version manifest 的原始 JSON 摘要。修复将权威元数据锁定官方 HTTPS 来源，验证版本与资源索引的原始字节，取消后续无摘要的重复索引下载；安装、启动、缓存和旧任务策略见 [download-trust.md](download-trust.md)。 |
| 更新 GPG 策略 | 指纹固定及内容验签已有；本批收口二进制签名、SHA-256/384/512、可信内置 keyring 的过期/吊销、已认证签名过期及实际封套预算，见 [签名策略](update-signature-policy.md)。发布身份的密码学绑定仍开放；版本身份规划测试不能替代签名清单。受保护 updater 继续拒绝自动替换。 |
| 补丁清单 / hpatchz | 自动更新未接入；unsigned patch index 不能授权最终文件。受保护 helper、签名发布清单和固定工具身份未交付，保持 [更新边界](update-privilege-boundary.md) 的拒绝策略。 |
| ZIP / TAR 特殊权限 | 修复共享 updater mode 边界，覆盖解包 inventory、scatter 清单与恢复权限。只保留原有 `0755` 范围内的普通权限；标准 ZIP 文件类型位不再导致执行权限丢失。无 mode 元数据仍使用平台默认，不能据此宣称 staging 已受保护。 |
| 账户保护 / token 参数 | DPAPI 未设置额外 entropy 本身不足以证明存在漏洞；固定公开 entropy 也不能建立同账户隔离。macOS 存储迁移和所有 JVM/兼容启动路径的 token 可见性仍需逐条证据，不将“Minecraft 限制”当作当前所有路径的结论。 |

## 性能与结构

单流进度发布、collection delta 全量排序、dirty scene 遍历、rich text/image 复用命中率
和 verification receipt 淘汰仍为待量化项。持久化 receipt 必须说明跨重启的文件身份
和失效规则；仅保存路径/大小/mtime 不能直接充当重新验签或 hash 的替代品。

程序集拆分、type forwards、IVT、命名空间与大文件是维护风险，应按稳定边界与实际
变更成本评估。此次不通过再拆程序集来回应审查。migration-map 已补齐 722–726 的
链接，并纠正旧文档“更新端到端完成”的描述。

## 验证边界

回归必须覆盖正常官方来源、原始 JSON 格式、解析型自定义 provider、本地安装器、
旧任务回滚/取消、已经提交但缺少完成标记的旧任务，以及所有特殊权限入口。
恶意元数据应在发布或执行前失败。实际网络来源可达性、真实 Minecraft、OS 窗口、
三平台受保护替换与长时间运行证据仍按 [验收清单](alpha6-beta-acceptance.md) 独立追踪。

本批 managed Services 与 Linux NativeAOT Services 均通过 457 项，架构检查通过
68 个项目。新回归覆盖官方正常来源、原始 index 只下载一次、摘要/长度/重定向
拒绝、旧 installer cache、旧任务取消/回滚/已提交标记修复、ZIP/TAR/scatter mode。
三平台 CI 新增原生 archive mode 回归；该测试不证明受保护 staging 或 OS 签名。
补充性能/架构审查的当前代码核对见 [static-review-follow-up.md](static-review-follow-up.md)。
