# Alpha.6 / Beta 非商业验收

2026-10-01。基线 `e9f9ae8a` / Alpha.5。此计划取代“功能数”作为下一阶段的完成标准；不引入订阅、支付、云盘套餐或商业市场。CI 全绿是必要条件，不是实机支持声明。

2026-10-05 按 `565e5143` 复核当前实现：下表及发布约束已更新，后面的逐批结果
保留当时的代码版本、测试数量和证据边界。历史批次的“尚未交付”应结合
[XSR-795 当前库存](XSR-795-unimplemented-inventory.md) 阅读，不能覆盖后续已交付消费者。

## 完成口径

| 优先级 | 工作 | 已有基础 | 关闭条件 / 当前缺口 |
| --- | --- | --- | --- |
| P0 | Minecraft 兼容性 | JNI / Host / preflight / observation | `eng/acceptance/minecraft-matrix.json` 中候选组合逐项留存真实客户端进入世界、持续运行、正常退出的证据；候选不代表支持。不适用组合附理由，不计算笛卡尔积。JVM smoke 和 unverified-client pilot 不算通过。 |
| P0 | 长期稳定性 | 有界缓存、日志、任务保留、会话生命周期 | 真 Desktop 8h idle、8h Minecraft running；另测2h导航；500 搜索、100 切换、50 启动/取消、20 安装/取消/恢复、整合包连续安装/删除。记录 working set、managed live bytes、allocation、handles、threads、entities 及各子系统计数。合成/fixture 测试单列，不替代真实使用。 |
| P0 | 更新事务 / 原生签名 | XSR-754/755：预安装受保护 helper、独立验签、对象绑定不可变 slot、持久恢复/激活/回滚/重启及差分回退 | 产品事务已接入；三平台物理替换竞争与断电仍待验收。`UpdateStaging.ApplyPlan` 继续拒绝；不得提权用户可写的 helper。Authenticode、Developer ID、notarization 需生产发布者身份及 SmartScreen/Gatekeeper 实机证据。 |
| P0 | 拆分后的稳定性 | 69 个项目的架构门禁，异步账户初始化已收口 | 契约、架构、NativeAOT、trim、三平台原生生命周期通过；新增依赖边界先修改架构锁。 |
| P1 | Sidecar 执行 / Patch compiler | 受限 Function compiler/point、Event/Intent、caption/text-card adapter；XSR-790 Host 协商、typed payload、stream/credit、health、卸载及监督 API | 额外 Function ABI/hook adapter 仍需独立契约；当前受限同步 string ABI 不支持 async/ref/迭代器或任意方法。任意交互 New UI、Plugin 引擎/SDK/package/UI IR freeze 属外部仓库契约，不能用 Host API 完成替代；真实插件长期验收仍开放。 |
| P1 | 内容图 / 更新 | 本地 inventory/版本查询/删除影响图；XSR-758/774 在线关联及资源包/光影单项可恢复更新 | 模组实际更新、选择/批量更新、changelog、同一多项事务 rollback 尚未交付。检查复用连接池并响应后复验本地 hash；metadata 图不等于实际 loaded set 或版本范围验证。 |
| P1 | 实例工作区 | 概览、内容、文件、恢复、回收、设置、截图预览；XSR-759 servers.dat 编辑/顺序/状态/临时 Join；XSR-760 选中文件 MRPack 导出 | 世界 datapack/版本/大小/最近游玩/备份/锁；截图更完整元数据/裁剪及其他未接消费者。服务器图标缓存保留不等于全部 UI 图标展示已验收；不能将文件列表或通用移除算成世界管理完成。 |
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

`python eng/acceptance/audit_alpha6.py` 校验并打印本轮九个工作流的保守状态索引；
`--require-accepted` 仅用于发布门禁，在任一项仍开放时返回非零。机器可读清单见
`eng/acceptance/alpha6-status.json`，审计说明见
[XSR-745](migrations/XSR-745-alpha6-acceptance-ledger.md)。清单有效不等于验收通过，
也不能替代原始实机证据。

`python eng/acceptance/verify.py --policy-only` 验证计划本身；`--evidence DIR --commit SHA` 验证指定代码版本的真实 Minecraft 记录及归档 hash，输出每个候选的 pending/verified/not-applicable 状态；加 `--require-all` 才是全矩阵门禁。没有记录时不能返回“已兼容”。测试 fixture 仅证明验证器会拒绝错误输入，不是 launch evidence。

原始证据保存在测试者选择的目录，不提交游戏文件、账户和完整日志。记录中的 artifacts 是相对路径及 SHA-256。审核者应确认视频/截图确为该次运行；hash 只绑定证据内容，不能自动证明人工观察真实。跨提交复用证据必须重新运行或经明确复核，不接受旧 SHA 自动覆盖新版本。

`Nexa.UI.Next.Benchmarks --output FILE [--timing-gate 120hz|60hz]` 输出 kernel 的各场景 percentile。`Nexa.Desktop.Tests --soak idle|navigation --seconds 7200 --output DIR` 运行真实 Desktop composition / UI.Next，使用隔离数据和 fixture 服务；不创建 OS 窗口，不下载/启动游戏，不宣称覆盖 GPU、OS accessibility、网络池或原生 Sidecar/JVM。

## 发布约束

Alpha.6 可明确列出尚待实机验收的候选能力；Beta 的支持列表应只取已审阅的当前版本 evidence。
当前受保护更新器仅准入可信系统安装；便携或不安全安装保持手动可信安装器流程。
旧 ApplyPlan 仍拒绝，不能通过放行用户暂存 helper 替代受保护事务。生产证书、各平台
真机、合法游戏与 Loader 工件、长期测试时间均是独立验收条件。

## 首批收口结果（2026-10-01 历史批次）

- 已落地 126 个候选的证据校验及 CI 契约测试；真实客户端证据仍为 0，不能宣称候选全部支持。
- 已落地两小时 composition soak 入口及三平台手动 workflow；日常 CI 仅运行 10 秒入口 smoke。本机 idle/navigation 各实跑 60 秒通过，scene entities 分别稳定在 285/319，state cells 均为 208；collected managed live bytes 未增长。此结果不替代两小时或完整原生 Desktop 验收。
- 已落地 kernel percentile JSON 和受控 runner 的可选 120Hz/60Hz 门禁。修复 clean entity 重复 dirty acknowledgement；本机 1,600 节点 paint P50 约 2.86 → 1.78ms，layout P50 约 3.24 → 2.31ms。10,000 个全部实例化节点仍超预算；它不是虚拟列表 SLA。时间数据来自当前开发环境，不能用作受控硬件认证。
- 已落地 Recovery 人类可读汇总与类别恢复、删除预览的间接依赖/别名影响、关于页主动诊断 ZIP。诊断包仅保留 typed operation facts，排除 raw message/exception/context；未知完整性与 Java 等事实保持 null。
- 已增加只读 Instance Content Graph，按需复用 inventory、12 项分页与双向关系；迭代循环分析和读取预算不把未知/截断关系判作缺失。更新检查取消、连接池及响应后内容身份复验见 `instance-content-graph.md`；这不关闭实际批量更新工作。
- 首批未新增程序集；该批次时 updater、系统签名、Sidecar execution/Patch compiler、批量内容更新、完整工作区/迁移助手及实机截图仍开放。后续 XSR-754/755 和 XSR-790 已分别补齐受保护更新与 Host API，其余缺口按当前表核对，不能把任一批次称作整份路线图完成。

## 性能优先级修订

运行期性能契约取代启动时间导向：不设置700ms冷启动或300ms热启动门槛。Time To Splash、真实阶段与可响应的最小Shell需单独实施；已有装饰Splash不代表这些能力已完成。fixture soak改为事件驱动idle并增加资源指标，kernel补充尾部数据；新增测量不等于目标已达到。

## 运行期 admission 收口

共享CPU/disk/HTTP调度与Launch Quiet Mode首批adapter见 [work-scheduling.md](work-scheduling.md)。目录主动读取会提升对应预取请求；图标释放HTTP后再等待CPU，整个encoded/decode流水线最多四项。游戏取消/失败立即释放，确认窗口后15秒grace或提前退出释放，多quiet scope独立计数。提示卡片仅在可见活跃状态计时，用户Reduced Motion不被临时策略改写。schema-1在线模型的生产轮询已移除。

本机managed验证：Release零警告/错误；452项Services、105项Desktop、88项UI.Next、9项Avalonia backend及68项目架构检查通过。60秒默认主页fixture记录0frames、0render requests、所有采样quiet/admission计数为0；285entities、210state cells。采样器在本进程内，CPU/allocation数字包含每秒Process/JSON工作和JIT；这不是idle CPU <0.2%、零分配或8h原生验收。

新增静态审查按 [review-505b9f9f.md](review-505b9f9f.md) 与
[static-review-follow-up.md](static-review-follow-up.md) 追踪。下载权威来源/index/旧任务
及归档mode单元通过457项managed与Linux NativeAOT Services、105项Desktop、68项目
架构和trim shell52nodes；`b20dec9e`的XSR CI通过，三平台native模式测试通过。
其后日志空闲唤醒与单流进度单元通过460项managed/Linux NativeAOT Services及68项目架构；全局图片
CPU/GPU预算、collection发布/renderer剩余成本、受保护更新、余下adapter和真实长期证据仍开放。

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

`3ff6d31a` 的Launcher metadata job 110382944803已通过23项Python发布测试，包含3项
真实GnuPG集成；本机agent限制不再阻挡发布脚本回归证据。
同提交XSR CI 36866432112及Launcher Build 36866431789均已通过。

图片单元见 [XSR-731](migrations/XSR-731-image-residency-budgets.md)：图标encoded cache
按32 MiB/256项LRU保留，关闭后不被迟到结果复活；FitToBounds按可见尺寸/DPI解码，
同bucket复用bitmap，encoded数据不再整张复制。469项managed/Linux NativeAOT Services、
9项Avalonia backend及68项目架构通过。全局decoded CPU/GPU预算、pressure adapter与
真实进程RAM/长期曲线仍开放，不能用缩略图尺寸回归宣布内存SLA达标。

`6bdb04b3` 的XSR CI 36868482448与Launcher Build 36868482424均已通过。
collection单元见 [XSR-732](migrations/XSR-732-ordered-collection-deltas.md)：有序唯一基底
仅排序变化项再线性合并，保留原有重复/无序归一化、稳定排序与失败原子性；同revision读取
复用snapshot，10,000次不变读取的线程分配为0。managed/Linux NativeAOT runtime各102项通过，
本机Unix socket EPERM导致1项OS IPC显式跳过，CI保留该项；Services各469项、Desktop105项、
UI.Next88项、backend9项、架构68项通过。60秒composition idle记录1次frame/render request，
285entities/210states稳定；唤醒尚未归因，不宣称零帧或真实原生CPU/RAM/8h验收完成。

`3f671bbd` 的XSR CI 36872175103及Launcher Build 36872175105均已通过，CI保留的OS IPC
及NativeAOT/trim门禁也已通过。本机Unix socket限制没有转化为CI跳过或契约放宽。

恢复调度见 [XSR-733](migrations/XSR-733-recovery-work-admission.md)：初始枚举与来源处理
明确使用Background CPU/disk额度，80 KiB读/hash/压缩之间释放再申请；可选blob回收按32项
Idle try-admission，quiet/争用时留待重试，提交后取消不回退基线。474项managed/Linux
NativeAOT Services、105项Desktop及68项目架构通过。256 KiB实际字节/Brotli回归证实文件中途
quiet时无第二次读取且Critical能取得额度；这不是Minecraft实机争用或长期性能认证。

`4b1c3811` 的Launcher Build 36875918243通过；XSR CI 36875918277在collection热路径分配
测试失败（仅预热2次后测到24,624字节），不是格式或恢复adapter失败。测试补齐既有numeric
lookup同样的200,000次预热，仍严格要求10,000次读的线程分配为0；具体CI分配调用栈尚未采集。

`60fa833c` 的Launcher Build 36878964347六平台通过；XSR CI 36878964126的managed/native
runtime（含OS IPC）、Services、Desktop、UI/backend和架构门禁通过，最后在
`InstanceRecoveryService`的imports排序失败。本批修正排序；不能把末尾跳过的shell/trim
步骤计为通过。

soak唤醒归因见 [XSR-734](migrations/XSR-734-soak-wake-attribution.md)：仅保留测量区间内
最多64项semantic ID/reason计数和时间，分开state/tree请求；不采集payload、不强制flush
或等待。managed/Linux NativeAOT Desktop各106项、68项目架构通过。实跑60秒managed idle
为0frames/0publications，navigation为3,631frames、1,210state/110,117tree请求；另一次
60秒NativeAOT idle为1frame/1state请求，关联测量开始0.0003304秒的`logging.entries`
delta，随后未记录其他publication。历史XSR-732单帧仍未归因；此工具不关闭原生窗口/GPU、
真实Minecraft或8h性能验收。

Function Patch执行首批见 [XSR-735](migrations/XSR-735-bounded-function-patch-execution.md)：
显式Host target grant、同步string ABI、五阶段受限指令、共享预算、单程序失败回滚和会话
激活/卸载已通过真实二进制session测试。managed/Linux NativeAOT runtime各111项通过，
本机既有1项Unix IPC显式跳过，CI保持112项；68项目架构通过。10,000次不变patched调用
预热后线程分配为0。产品target尚未启用，编译前改写器、其他ABI以及UI/Event/Intent adapter
仍开放，不能用该首批执行器宣布Sidecar平台完整。

`6a9f7692` 的XSR CI 36881910562、Launcher Build 36881910337均通过；
`05b303ab` 的XSR CI 36883496173、Launcher Build 36883496270也均通过，包含未跳过的
OS IPC与112项runtime测试、NativeAOT及最后的Desktop shell/trim门禁。

编译前改写与产品point见 [XSR-736](migrations/XSR-736-compile-time-function-patches.md)：
新增一个独立build工具程序集，产物不进入运行依赖/安装包；SDK finalized obj路径、增量
Compile替换和相对debug映射均已核对。managed/Linux NativeAOT Desktop各107项、69项目
架构、编译器执行与18类拒绝回归通过；独立NativeAOT shell52nodes与first-run验证通过。
资源列表/详情的literal标题可实际执行会话程序，项目ID/元数据不变，卸载后后续重建恢复原文。
这是首个受限string point；其他ABI、UI/Event/Intent执行adapter和真实插件burn-in仍开放。
本机带binary/source哈希receipt的两小时NativeAOT idle composition fixture已完成，证据见XSR-742；
构建身份为基础提交加XSR-736工作树，不作为clean-commit版本、OS窗口或Minecraft证据。

`739e8cbb` 的XSR CI 36888656773通过，但Launcher Build 36888656750的Windows两项
因源码路径匹配而失败；独立修复 `af13bc26` 的XSR CI 36890630821与Launcher Build
36890631142均全绿，包含六平台打包与distribution。没有放宽或跳过Windows门禁。

共享动态raster见 [XSR-737](migrations/XSR-737-shared-raster-budget.md)：512项与64 MiB
pixel charge共用预算，按每像素8字节计账；相同内容/尺寸共享lease，只淘汰闲置LRU。
完全不可见和最小化窗口释放驻留，恢复正常显示后获取当前图片；其他窗口的lease不受影响。
managed/Linux NativeAOT backend各9项、69项目架构与whitespace通过；独立NativeAOT
产品的shell52nodes及first-run验证通过，编译器/Roslyn未进入安装输出。实际CPU/native、
GPU、decoder临时分配、OS pressure与真实进程RAM/8h曲线继续保留为验收项。

soak观测工具见 [XSR-738](migrations/XSR-738-soak-window-analysis.md)：读取完整schema-3
fixture后按5分钟报告常规窗口的min/median/max、峰值、采样缺口及CPU/allocation；
强制GC的baseline/final仅作单独端点，不参与趋势。至少3个充分采样的完整窗口才报告
median slope，短run为null；未知handles仍为null。原始run/samples哈希及可选冻结binary
receipt保留构建身份，不能将工作树版本升级为后来clean commit或实机证明。
16项初始Python回归及三份既有60秒实际fixture的分析通过；两小时fixture现已完成并保留原始样本。
工具输出保留原有endpoint gate，并明确runtime KPI/physical acceptance均未认证。

`53364121` 的 XSR CI 36894346775 / Launcher Build 36894346708，及
`896de842` 的 XSR CI 36896613672 / Launcher Build 36896613694 均已通过。

分配归因契约见 [XSR-739](migrations/XSR-739-soak-sampler-allocation.md)：schema-4
精确进程累计分配与fixture线程的capture/JSON写入计数在写入前对齐，当前写入计入下一次
sample；按区间报告总量、采样器、未归因余量。余量包含其他fixture和后台工作，不能
称为产品纯idle分配；CPU采样成本仍未分离。schema-3历史数据保持null归因。

资源页编码图片所有权见 [XSR-740](migrations/XSR-740-resource-page-image-ownership.md)：
列表/详情退到后台时取消旧图标读取、释放raster引用，返回只请求当前页并保留条目和
搜索草稿；销毁条目删除descriptor，取消的迟到completion不再唤醒UI。managed/Linux
NativeAOT Desktop各109项、69项目架构与whitespace通过；独立产品NativeAOT通过
52-node shell/first-run验证，安装输出无编译器/Roslyn。此结果不关闭viewport网络需求、
OS pressure、真实RAM/GPU/8h指标。

私有启动参数边界见 [XSR-741](migrations/XSR-741-private-launch-argument-transport.md)：
共享process service在端口调用前拒绝未配置Host且未保证私有传输的路径，包含默认、
core/production及注入组合；不按token flag名称猜测安全性。Desktop始终配置sibling Host，
缺少组件不能退回Java公开argv。合成凭据的前后复现、实际Host stdin/取消控制及独立
候选审查通过；managed/Linux NativeAOT Services各475项、Desktop各109项、69项目
架构及whitespace通过。独立NativeAOT产品shell52nodes/first-run通过，编译器/Roslyn
未进入输出。此结果不关闭同账户内存、keychain迁移、安装身份或真实Minecraft验收。

实际经过时间的idle fixture证据见 [XSR-742](migrations/XSR-742-retained-idle-soak-evidence.md)
及[原始记录](evidence/2026-10-01-native-idle/README.md)：两小时schema-3与30分钟schema-4
各1帧、常规窗口24/6个，Handle/scene/state/log稳定；初始内存增长、GC nursery和
最终强制GC分别保留。schema-4采样器分配单列，余量不称作产品纯idle；构建身份仍为
基础提交加当时工作树，不能升级为clean commit或Alpha.6实机结果。两份压缩样本精确
重放分析及19项Python回归通过。真实8h原生窗口/Minecraft与未测指标继续开放。

`c1049112` 的 XSR CI 36906662615 与 Launcher Build 36906662691 均已通过；
保留的长期 fixture 记录与六平台打包检查通过仍不等于真实 Minecraft 支持验收。

安装版本列表见 [XSR-743](migrations/XSR-743-version-list-visible-window.md)：只创建可视
窗口附近的行，重叠行复用实体；范围选择和拖放使用完整逻辑顺序。1,000/10,000实例
fixture 的行/实体上限、完整滚动 extent、跨屏多选、运行中 Move 限制、搜索、双向
键盘遍历、轮滚焦点保留及缩放通过。managed/Linux NativeAOT Desktop各110项、
renderer各90项、69项目架构与格式检查通过；独立产品52-node shell/first-run及trim
输出检查通过。此结果不关闭10,000目录扫描、实机frame tail或OS读屏验收。

稳态hover/光标输入见 [XSR-744](migrations/XSR-744-input-scratch-allocation.md)：命中
scratch复用并随场景缩小回收容量，结构输入屏障改为索引扫描，非活动分段手势不创建
闭包。三组预热后查询在managed/Linux NativeAOT均为0线程分配；renderer各92项、
Desktop各110项、backend9项、69项目架构与格式及独立产品AOT/linked trim验证通过。
隐藏pager、叠加modal和退役handle语义保留；实际拖动、动画、OS/GPU帧及8h实机继续开放。

受保护更新的持久防回退单元见 [XSR-746](migrations/XSR-746-update-high-water-store.md)：
helper侧版本高水位仅接受规范公开版本，在独占锁内拒绝同版/降级，以同目录临时文件、
write-through、落盘flush和原子替换提交；孤立临时文件不构成权威状态。既有三平台native
account-security GitHub Actions矩阵通过update-mode入口验证竞争与重启语义。目录保护、安装身份、独立验签、对象绑定替换/回滚及真实断电仍开放，
`UpdateStaging.ApplyPlan`继续拒绝；本单元不启用自动更新，也不将hosted runner称作实机验收。

## 当前实现订正（2026-10-05）

XSR-754/755、758–760、774、790 分别关闭了历史叙述中的受保护 helper/更新事务、
服务器编辑/Join、MRPack 导出、资源包/光影单项更新和 Sidecar Host API 缺口。
安装退出协调、Java/整合包/processor/改名恢复以
[可恢复安装](resumable-installation.md) 为准；快照逐项/全部恢复与启动补偿以
[实例恢复](instance-recovery.md) 为准。历史测试证据未升级为当前 clean commit 的
实机支持声明，未实现的世界管理、批量内容更新、额外 Patch 形状、迁移助手和性能
adapter 继续开放。机器索引仍为 `not-accepted`。

## 后续待办

- [ ] 自写渲染后端（待规划）。
