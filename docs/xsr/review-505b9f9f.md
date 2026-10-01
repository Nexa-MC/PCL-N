# 505b9f9f 审查跟进

审查来自代码阅读，未运行构建或测试，也未覆盖 VCDIFF、Jvm.Host native、OAuth
和 Avalonia accessibility。以下分开记录代码事实、修复范围和仍缺少的证据；不把
静态审查或 CI 全绿当成 Alpha.6 / Beta 的全部验收结果。

| 发现 | 当前核对与处理 |
| --- | --- |
| 分发 PFX 私钥 | 发布 workflow 从 secret 恢复 PFX，Desktop 可嵌入并读取私钥。这种共享客户端身份不能证明请求来自可信安装。轮换、每安装令牌、服务端校验与限流需要 API 服务端改动及部署证据，尚未关闭；不通过移除认证让现有联网流程绕过服务端策略。 |
| Forge / NeoForge 安装器 | 原校验请求已使用官方 `.sha1` URL，安装器字节可由镜像提供；“同一个镜像给出两者”不符合当前默认路径。修复增加来源重定向检查、官方安装器来源和缓存策略版本；本地显式选择安装器保留原路径。 |
| Minecraft 元数据 | 原实现是官方优先、失败后回退镜像，且未验证 version manifest 的原始 JSON 摘要。修复将权威元数据锁定官方 HTTPS 来源，验证版本与资源索引的原始字节，取消后续无摘要的重复索引下载；安装、启动、缓存和旧任务策略见 [download-trust.md](download-trust.md)。 |
| 更新 GPG 策略 | 指纹固定及内容验签已有；本批收口二进制签名、SHA-256/384/512、可信内置 keyring 的过期/吊销、已认证签名过期及实际封套预算，见 [签名策略](update-signature-policy.md)。XSR-730 补齐发布端签名身份清单和运行时原始字节准入，见 [发布准入](release-admission.md)；受保护 helper 的独立消费、持久防回退状态与自动替换仍开放。 |
| 补丁清单 / hpatchz | 自动更新未接入；unsigned patch index 不能授权最终文件。签名发布清单已交付，受保护 helper 和固定工具身份仍未交付，保持 [更新边界](update-privilege-boundary.md) 的拒绝策略。 |
| ZIP / TAR 特殊权限 | 修复共享 updater mode 边界，覆盖解包 inventory、scatter 清单与恢复权限。只保留原有 `0755` 范围内的普通权限；标准 ZIP 文件类型位不再导致执行权限丢失。无 mode 元数据仍使用平台默认，不能据此宣称 staging 已受保护。 |
| 账户保护 / token 参数 | [XSR-741](migrations/XSR-741-private-launch-argument-transport.md) 关闭缺少 Host 时的公开参数回退：共享服务在进程端口前拒绝未声明私有传输的路径，覆盖自定义 JVM/game 参数、旧版计划和全部组合入口；Desktop 始终配置 sibling Host。合成 token 的前后边界复现、真实 Host stdin/取消控制及独立候选审查保留证据。DPAPI 公开 entropy 不能建立同账户隔离；macOS keychain 迁移、同账户内存和共享客户端安装身份仍是独立问题。 |

## 性能与结构

单流进度发布已经100ms有界；verification receipt 已改为逐项LRU并撤销显式校验失败
记录，分别见 XSR-727/XSR-729。collection delta 全量排序、dirty scene 遍历、
rich text/image 复用命中率仍待量化。持久化 receipt 必须说明跨重启的文件身份
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
