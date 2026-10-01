# Alpha.6 / Beta 非商业验收

2026-10-01。基线 `e9f9ae8a` / Alpha.5。此计划取代“功能数”作为下一阶段的完成标准；不引入订阅、支付、云盘套餐或商业市场。CI 全绿是必要条件，不是实机支持声明。

## 完成口径

| 优先级 | 工作 | 已有基础 | 关闭条件 / 当前缺口 |
| --- | --- | --- | --- |
| P0 | Minecraft 兼容性 | JNI / Host / preflight / observation | `eng/acceptance/minecraft-matrix.json` 中候选组合逐项留存真实客户端进入世界、持续运行、正常退出的证据；候选不代表支持。不适用组合附理由，不计算笛卡尔积。JVM smoke 和 unverified-client pilot 不算通过。 |
| P0 | 长期稳定性 | 有界缓存、日志、任务保留、会话生命周期 | 真 Desktop 8h idle、8h Minecraft running；另测2h导航；500 搜索、100 切换、50 启动/取消、20 安装/取消/恢复、整合包连续安装/删除。记录 working set、managed live bytes、allocation、handles、threads、entities 及各子系统计数。合成/fixture 测试单列，不替代真实使用。 |
| P0 | 更新事务 / 原生签名 | metadata / GPG / hash / inventory / 安装包 | 预安装受保护 helper、内部独立验签、对象绑定 staging/replace/rollback/restart，三平台替换竞争与断电测试。`UpdateStaging.ApplyPlan` 继续拒绝；不得提权用户可写的 helper。Authenticode、Developer ID、notarization 需发布者证书及 SmartScreen/Gatekeeper 实机证据。 |
| P0 | 拆分后的稳定性 | 68 个程序集，异步账户初始化已收口 | 契约、架构、NativeAOT、trim、三平台原生生命周期通过；新增依赖边界先修改架构锁。 |
| P1 | Sidecar 执行 / Patch compiler | 验签 EXE、认证 IPC、注册、snapshot、监督 | UI Patch / Event / Intent / Function 全部执行 adapter；显式 patch point、强类型 ABI、有界指令、capability 与 host validation；HEAD/ARGS/TAIL/RETURN/REPLACE 的错误/取消/退役会话测试。注册声明不能算执行完成。 |
| P1 | 内容图 / 更新 | 本地 inventory、版本查询、删除的间接影响预览；只读内容依赖页含双向关系、别名、停用、缺失/未知/歧义及循环 | 选择/批量更新、changelog、同一事务 rollback 尚未交付。更新检查复用连接池并在站点响应后复验本地 hash；实际替换仍须独立事务。metadata 图不等于实际 loaded set 或版本范围验证。 |
| P1 | 实例工作区 | 概览、内容、文件、恢复、回收、设置，有界截图缩略图/详情预览 | 世界 datapack/版本/大小/最近游玩/备份/锁；servers.dat 编辑/favicon/延迟/Quick Join；截图元数据/裁剪；整合包导出。不能将文件列表算成管理完成。 |
| P1 | Recovery 产品化 | 成功基线、diff、分页、逐项/全部恢复 | 按模组/配置/Java 等展示变化；只对有证据的风险作解释，不能把时间相关性称为原因；提供类别恢复与逐项恢复。快照、fingerprint 和冲突验证继续由 Service 执行。 |
| P1 | Resource Center | 双来源、过滤、依赖规划、安全安装/删除 | 导入、更新、changelog、失败恢复及各内容类型闭环；所有异步搜索结果保留 generation 与取消边界。 |
| P1 | Preflight / estimator | 分层 facts / estimate / provenance | 已验证硬约束才 Block，估计 Warn；保守静态模型 + 同实例可信历史 + 受控 benchmark priors。schema-1 online model 保持停用；真实 false-positive 语料须经审阅。 |
| P0 / P1 | 运行期性能 | clean scene skip、dirty relayout、虚拟列表、连接复用 | 以idle CPU/RAM、长期稳定性、常规UI P95 <3ms / P99 <6ms、Minecraft启动让路和可见规模为核心。图片/IO预算、零分配和线程控制列P1；完整启动降为regression约束。完整目标及待实现项见 [runtime-performance.md](runtime-performance.md)。共享CI默认只阻断确定性不变量，受控硬件才启用时间门槛。 |
| P2 | 本地化 QA | 简中 / 繁中 / English | language × DPI 125/150/200% × 三平台真实字体截图；文本溢出、CJK fallback、快捷键、日期数字、accessibility 文本。翻译表齐全不等于验收通过。 |
| P2 | Accessibility | 焦点、语义、Reduced Motion | Tab/方向键/读屏/高对比/键盘/触摸/笔/控制器/200% DPI；读屏必须包含实际 OS accessibility tree，不能只测 scene label。 |
| P2 | 诊断包 | 脱敏日志、preflight、crash、tasks、inventory | 用户主动导出有界 ZIP，allowlist 版本/OS/Java/MC/loader/问题分类/Mod identity/完整性/任务失败；无账户、token、绝对路径、任意个人文件；红队样例和取消/失败清理。 |
| P2 | PCL N 1.x 迁移 | 目录 / 版本导入、账户导入 | 迁移助手列出可迁移、转换、不能迁移；目录、实例、Java、参数、资源、设置、收藏。无密码明文复制，无静默覆盖、可撤销、导入不会执行旧代码。 |
| 持续 | 版本行为一致 | Alpha/Beta 额外诊断 | 安装/更新/账户/网络策略不因 channel 改变；额外诊断和实验开关单独列出并验证。 |

## 架构约束

仅稳定依赖边界、NativeAOT trimming 边界、跨进程 ABI 或独立测试生命周期需要新程序集；其余优先既有 namespace/module。此路线图不授权一般方法改写、反射 patch 或将 UI.Next 接入服务定位器。不得为通过验收而放宽授权、对象身份、数据 provenance 或回滚冲突检查。

## 证据工具

`python eng/acceptance/verify.py --policy-only` 验证计划本身；`--evidence DIR --commit SHA` 验证指定代码版本的真实 Minecraft 记录及归档 hash，输出每个候选的 pending/verified/not-applicable 状态；加 `--require-all` 才是全矩阵门禁。没有记录时不能返回“已兼容”。测试 fixture 仅证明验证器会拒绝错误输入，不是 launch evidence。

原始证据保存在测试者选择的目录，不提交游戏文件、账户和完整日志。记录中的 artifacts 是相对路径及 SHA-256。审核者应确认视频/截图确为该次运行；hash 只绑定证据内容，不能自动证明人工观察真实。跨提交复用证据必须重新运行或经明确复核，不接受旧 SHA 自动覆盖新版本。

`Nexa.UI.Next.Benchmarks --output FILE [--timing-gate 120hz|60hz]` 输出 kernel 的各场景 percentile。`Nexa.Desktop.Tests --soak idle|navigation --seconds 7200 --output DIR` 运行真实 Desktop composition / UI.Next，使用隔离数据和 fixture 服务；不创建 OS 窗口，不下载/启动游戏，不宣称覆盖 GPU、OS accessibility、网络池或原生 Sidecar/JVM。

## 发布约束

Alpha.6 可明确列出尚待实机验收的候选能力；Beta 的支持列表应只取已审阅的当前版本 evidence。受保护更新器未交付前维持手动可信安装器流程，不能通过更改文案或放行旧 ApplyPlan 宣布完成。证书、各平台真机、合法游戏与 Loader 工件、长期测试时间均是独立验收条件。

## 首批收口结果

- 已落地 126 个候选的证据校验及 CI 契约测试；真实客户端证据仍为 0，不能宣称候选全部支持。
- 已落地两小时 composition soak 入口及三平台手动 workflow；日常 CI 仅运行 10 秒入口 smoke。本机 idle/navigation 各实跑 60 秒通过，scene entities 分别稳定在 285/319，state cells 均为 208；collected managed live bytes 未增长。此结果不替代两小时或完整原生 Desktop 验收。
- 已落地 kernel percentile JSON 和受控 runner 的可选 120Hz/60Hz 门禁。修复 clean entity 重复 dirty acknowledgement；本机 1,600 节点 paint P50 约 2.86 → 1.78ms，layout P50 约 3.24 → 2.31ms。10,000 个全部实例化节点仍超预算；它不是虚拟列表 SLA。时间数据来自当前开发环境，不能用作受控硬件认证。
- 已落地 Recovery 人类可读汇总与类别恢复、删除预览的间接依赖/别名影响、关于页主动诊断 ZIP。诊断包仅保留 typed operation facts，排除 raw message/exception/context；未知完整性与 Java 等事实保持 null。
- 已增加只读 Instance Content Graph，按需复用 inventory、12 项分页与双向关系；迭代循环分析和读取预算不把未知/截断关系判作缺失。更新检查取消、连接池及响应后内容身份复验见 `instance-content-graph.md`；这不关闭实际批量更新工作。
- 未新增程序集。受保护 updater、系统签名、Sidecar execution/Patch compiler、批量内容更新、完整工作区/迁移助手以及 OS accessibility、language × DPI 实机截图仍未关闭。上表是持续执行清单，不能把本批提交称作整份路线图完成。

## 性能优先级修订

运行期性能契约取代启动时间导向：不设置700ms冷启动或300ms热启动门槛。Time To Splash、真实阶段与可响应的最小Shell需单独实施；已有装饰Splash不代表这些能力已完成。fixture soak改为事件驱动idle并增加资源指标，kernel补充尾部数据；新增测量不等于目标已达到。

## 运行期 admission 收口

共享CPU/disk/HTTP调度与Launch Quiet Mode首批adapter见 [work-scheduling.md](work-scheduling.md)。目录主动读取会提升对应预取请求；图标释放HTTP后再等待CPU，整个encoded/decode流水线最多四项。游戏取消/失败立即释放，确认窗口后15秒grace或提前退出释放，多quiet scope独立计数。提示卡片仅在可见活跃状态计时，用户Reduced Motion不被临时策略改写。schema-1在线模型的生产轮询已移除。

本机managed验证：Release零警告/错误；452项Services、105项Desktop、88项UI.Next、9项Avalonia backend及68项目架构检查通过。60秒默认主页fixture记录0frames、0render requests、所有采样quiet/admission计数为0；285entities、210state cells。采样器在本进程内，CPU/allocation数字包含每秒Process/JSON工作和JIT；这不是idle CPU <0.2%、零分配或8h原生验收。

新增静态审查按 [review-505b9f9f.md](review-505b9f9f.md) 与
[static-review-follow-up.md](static-review-follow-up.md) 追踪。下载权威来源/index/旧任务
及归档mode单元通过457项managed与Linux NativeAOT Services、105项Desktop、68项目
架构和trim shell52nodes；`b20dec9e`的XSR CI通过，三平台native模式测试通过。
其后日志空闲唤醒与单流进度单元通过460项managed/Linux NativeAOT Services及68项目架构；图片分层
预算、collection delta、受保护更新、余下adapter和真实长期证据仍开放。

GPG 策略收口见 [XSR-728](migrations/XSR-728-detached-signature-admission.md)：
固定指纹、二进制文档和强摘要、可信内置 keyring 的吊销/过期、认证签名过期、
实际输入/解压预算及取消契约。managed 与 Linux NativeAOT Services 463 项、68项目
架构检查通过；该结果不关闭签名
发布身份、replay/downgrade 或受保护 updater。`b20dec9e` 的 Launcher Build 也已通过。

校验 receipt 不再在8192项满额时整表清空；改为逐项LRU，显式hash失败撤销旧记录，
见 [XSR-729](migrations/XSR-729-verification-receipt-lru.md)。managed/Linux NativeAOT Services 464项及
68项目架构通过。该服务生命周期缓存不持久化，也不替代实际启动和长期内存测量。

签名发布清单与运行期准入见 [XSR-730](migrations/XSR-730-signed-release-manifest.md)：
完整18包的版本/通道/RID/格式/native variant/配置/长度/hash、原始验签字节所有权、
严格版本推进和实际包字节检查。managed/Linux NativeAOT Services 467项、68项目架构
及20项非GnuPG Python发布回归通过。GnuPG集成保留在CI；当前环境的agent无法启动。
受保护helper必须独立消费这些契约，high-water版本需受保护持久化；尚未启用自动替换。
