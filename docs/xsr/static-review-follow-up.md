# 性能与架构静态审查跟进

2026-10-01。两份补充审查没有运行或 profile；以下核对当前代码，不把估算成本
当成测量结果，也不因旧问题已经修复就宣称运行期 SLA 达标。

## 性能

| 审查项 | 当前事实与剩余工作 |
| --- | --- |
| 每次变化重画所有控件、逐项 IndexOf | `AvaloniaUiSceneNodeControl.Apply` 已比较 node，未变化时直接返回；clip、文本有复用，visual 字段变化才 invalidate。surface 已有 child index 表。dirty scene 仍遍历可见节点，重排仍有范围更新；复杂 rich text / image 的相等与缓存命中率、OS/GPU 帧预算仍待实测。 |
| 启动全量 hash 与线性去重 | 当前候选文件是路径字典；内容寻址资产按已知大小复用，库有服务生命周期 receipt，修复下载强制 hash。receipt 达到 8192 后全清仍会造成抖动；不能将路径/大小/mtime 的跨重启缓存当成可信完整性证明。 |
| 文件逐个下载、重复线性查询 | install 与 launch 已使用 `FileBatchProgress.RunAsync`，并发上限 8，路径字典去重，连接通过 pooled HTTP。当前共享 admission 还限制 HTTP/disk；真实网络和 Minecraft 启动争用仍缺证据。 |
| Recovery 每次重新压缩、再次校验、串行 | `RecoverySnapshotStore` 已按源文件与 blob stamp 复用，最多 4 个 capture worker；新 blob 替换旧对象，不再额外解压 dedupe hit。恢复仍校验实际内容。单文件 fsync 与 8 GiB 预算是数据持久性/资源约束，不能直接删除；大包策略和后台 IO admission 仍待验收。 |
| 每条日志重排、同步 flush | ring 使用有界队列，batch timer 只在有新消息时一次性唤醒，file sink 空闲时仅等待 channel；单流下载进度也按100ms合并并保留最终字节。`PublishDelta` 仍复制/排序 collection；真实 idle 与高压日志成本继续测量，详见 [XSR-727](migrations/XSR-727-idle-logging-and-download-progress.md)。 |
| FramePreparing 全量投影 | controller 已缓存部分 state ID、按变化更新投影；FramePreparing 仍有工作，必须量化热点并保留正确的查询完成/活动切换通知。 |
| JAR 元数据无缓存 | 当前已有路径/大小/mtime 缓存、1024 项与 64 MiB 预算及归档索引；它只缓存展示信息，不认证可执行内容。FIFO/跨窗口持有仍待压力测试。 |
| HTTP、Java、component lookup | HTTP 采用 pooled handler，Java 探测最多 4 个并发且 cache 属于 locator 生命周期，UI component 已采用 typed slot。剩余调用与长时间资源趋势仍需测量。 |

## 架构

| 审查项 | 当前事实与剩余工作 |
| --- | --- |
| Services 九模块循环、缺少 Contracts/Platform | 当前已拆分稳定 contracts / implementation，shared launch models 位于 Domain，Recovery.Storage 独立，Platform.Abstractions 与 Runtime 已存在；68 项目架构检查通过。项目 DAG 不能证明所有运行期耦合合理，implementation back-edge、IVT 和 type forward 仍须按实际职责评估。 |
| 空项目 | Domain、Contracts 已有值类型和 JVM bootstrap codec；旧 Xsr.Transport、Xsr.Generators、UI.Next.DevTools 不在当前 solution。架构检查拒绝无源码的非 generator 项目，避免仅为命名而拆程序集。 |
| Desktop controller / Program composition | Launch controller 已分 partial 文件；联网、更新、遥测组合位于 Services.Composition。文件拆分不能证明状态职责正确，install draft、共享状态与 UI styling 继续按可测试边界收口。 |
| Jvm.Host 引用全 Services | 当前 host 只引用 Nexa.Contracts；NativeAOT JNI 生命周期有独立 CI。真实 Minecraft 兼容矩阵仍是独立验收。 |
| 同步 shutdown / keyring | 所列 controller/process 目录已无 `GetAwaiter().GetResult` / `Wait`；账户存储有异步初始化流程。所有平台实际 shutdown、keyring 失败路径和用户体验仍需验收，不能仅靠搜索证明全无阻塞。 |
| Java 静态 cache、重复架构文档 | locator cache 已为实例字段；docs/xsr 当前不再有 `-updated` 文档分支。规范锁继续以 README 链接的文档为准。 |

以 [runtime-performance.md](runtime-performance.md) 和
[alpha6-beta-acceptance.md](alpha6-beta-acceptance.md) 的指标及证据边界推进。
不通过新增一批程序集或取消 hash、恢复校验、durability 来回应静态性能估算。
