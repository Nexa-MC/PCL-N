# XSR-801 — 内容更新、世界与截图工作流

2026-10-08（Asia/Shanghai）。本迁移扩展 PCL-N 内的内容管理，不改变插件执行引擎范围。

世界详情的手动快照闭环遵循 [XSR-804](XSR-804-storage-diagnostics-completion.md)：按实例与世界
路径的不可逆哈希隔离历史，读取、校验、捕获和恢复均经过类型化路由；捕获与恢复复用世界
修订、实例运行状态及 session.lock 租约准入。恢复先验证隐藏暂存世界，再发布到全新名称，
不覆盖原世界；不暗示自动捕获策略已实现，也不在薄备份清单中保存绝对来源路径。

内容更新通过可取消的类型化命令准入模组、资源包和光影包。每个选项必须匹配在线识别所得
项目、Minecraft 和 Loader；所有下载完成并校验后，管理服务取得恢复操作租约，再次核对
本地身份和运行中游戏。批量替换共用一个持久日志与原件备份，失败自动联合撤回；重启后可
从同一记录恢复。撤回前核对摘要，外部编辑冲突保留备份并返回失败，不覆盖未知内容。

世界 NBT 是有界、不执行的展示数据，保留未知标签。世界复制、备份、锁和数据包写入由
管理服务执行，拒绝链接和运行中游戏，并持有 Minecraft session.lock 独占租约。目录扫描和展开共享
明确预算；超限标为不完整，不把部分大小显示为准确总量。数据包启停修改 level.dat 的
DataPacks 列表，先保留原件，再原子发布；Minecraft 自带 vanilla 包不能移除。

截图裁剪只生成新 PNG，不覆盖原始文件；剪贴板和系统分享效果由原生后端提供，UI 只发出
语义意图及受管理目录约束的文件身份。分享将文件交给系统剪贴板，反馈明确说明可粘贴到
聊天应用；不执行上传。列表按实际修改时间倒序，详情显示尺寸、位深、颜色、大小和时间。

文件工作区只枚举所选实例实际游戏目录下的 config、logs、crash-reports。目录浏览支持
最多 16 层和 1000 项，文本读取最多 1 MiB；日志与崩溃记录只读。配置写入限定为常见文本
扩展名，必须先查看修改预览，再显式保存。服务重核文件 SHA-256、大小和修改时间，拒绝
链接及运行中的实例，原子发布前保留原件备份。保持原始 UTF-8/带 BOM UTF-8/UTF-16
编码与换行；其他编码或二进制文件通过系统文件夹入口查看，不能以损失数据的替换字符编辑。
原生输入组件仍是单行输入：配置编辑器选择具体行，只替换该行内容，其它行与原始分隔符
保持原样。每页 25 行，最多 65536 行的展示索引，单行编辑最多 32768 字符；尾部超限内容
仍保留于原始文本，不因展示裁剪而丢失。日志和崩溃文件不能通过伪造 Editable 写入。

## 生产消费者

模组单项更新与所选/全部批量更新进入 `ResourceContentUpdateService.UpdateBatchAsync`，验证
目标仍匹配项目与游戏，下载摘要仍匹配最初选择，再进入同一个持久替换事务。最终启用的
模组集合由实际 JAR 清单核验；缺失必需依赖、重复 ID 或无法完整读取的依赖拒绝发布。
禁用模组更新后仍为 `.jar.disabled`。原有资源包/光影单项回收记录入口保持兼容；批量
更新共用 `InstanceContentUpdateTransaction` 的原件备份、摘要准入与联合撤回。

`ContentManagementRuntime` 注册全部世界、文件工作区、截图读取和更新撤回路由。原生
PNG codec 通过 composition delegate 注入截图裁剪服务，UI 无 concrete service 调用。
世界备份在世界独占租约中调用 ContentBackupService，明确排除 session.lock 与编辑保护
标记；数据包仅 DataVersion >= 1519 准入。启停保留未知 NBT 标签与 level.dat 原件；停用
后的文件移至专属回收目录，可以不覆盖地还原。编辑保护不替代操作系统游戏锁。

以下是稳定 IA ID 与真实消费者的对应关系；SettingsCatalog 已同步对应功能的可用性。
同一行为不因目录中的重复位置计作额外独立功能。

| IA ID | 行为消费者 |
|---|---|
| instance.content.05803a333d18 | 模组目录、实际包元数据、启停/移除/更新；Management、ModCategories、OnlineContent、ContentUpdates |
| instance.content.d0c2287854d6 | 资源包目录、元数据与在线版本更新；Management、OnlineContent |
| instance.content.fc3ff537cf56 | 光影包，按实例 Loader/已启用光影模组准入；InstanceManagementService.Pages、OnlineContent |
| instance.content.619a816efe84 | 世界详情 DataPacks；InstanceWorldService、SettingsPageController.Worlds |
| instance.content.2e3fd2c9385f | 实际已安装内容清单；InstanceContentMetadata、Management |
| instance.content.22b442dcb594 | 已识别可更新筛选、单项/选择/批量更新；ModCategories、ResourceContentUpdateService |
| instance.content.2ee458fdbb24 | 模组禁用筛选及 SetModEnabled；InstanceContentService、ModCategories |
| instance.content.e0991a1031cc | 本地包异常分类与依赖图；InstanceContentMetadata、InstanceContentGraphBuilder |
| instance.worlds.17db261a8c32 | saves 列表及有界世界 NBT 元数据；InstanceManagementService、InstanceWorldService |
| instance.worlds.777fa6b3b8a5 | LastPlayed 真实 NBT 时间；Worlds |
| instance.worlds.3fff1c03d28a | Version.Name、DataVersion；Worlds |
| instance.worlds.ee27261308f3 | 有界文件统计，超限显示未知总量；InstanceWorldService、ContentDetails |
| instance.worlds.0f68adbfffa2 | 显式打开当前世界目录；ContentDetails.OpenContentDirectory |
| instance.worlds.ae94fe88e32c | 独占世界租约中的不可变备份；InstanceWorldService.BackupAsync、ContentBackupService |
| instance.worlds.faad5f3cce5e | 暂存复制、原件再核验、不覆盖发布；InstanceWorldService.CopyAsync |
| instance.worlds.22effc520481 | 世界移至实例回收站及不覆盖还原；InstanceContentTrash |
| instance.worlds.6c5d0a10ab50 | 世界租约内 MCA allocation、压缩校验与有界 NBT 结构扫描；InstanceWorldService.Health、WorldHealth |
| instance.files.29b2e7f92b05 | 总览显式打开版本目录；Management.OpenContentDirectory |
| instance.files.8f0192eaa51c | config 受限浏览、逐行编辑、预览与保存；InstanceFileWorkspaceService、FileWorkspace |
| instance.files.3f21274a1129 | logs 受限只读浏览；InstanceFileWorkspaceService、FileWorkspace |
| instance.files.0133bf26089d | crash-reports 受限只读浏览；InstanceFileWorkspaceService、FileWorkspace |
| instance.files.ff0bb86d9c48 | saves 管理页及目录交接；Management、ContentDetails |
| instance.files.05803a333d18 | mods 管理页及目录交接；Management、ContentDetails |
| instance.files.d0c2287854d6 | resourcepacks 管理页及目录交接；Management、ContentDetails |
| instance.files.83c37904da0b | 所选实例游戏目录只读浏览与有界文本查看；InstanceFileWorkspaceService.Other、FileWorkspace |
| instance.screenshots.3eae7fd3de23 | 有界 PNG 画廊；InstanceContentMetadata、ContentDetails.BuildScreenshotCard |
| instance.screenshots.eacf056aa1f9 | 时间线/画廊切换，按真实修改日期分组，时刻展示与有限分页；ScreenshotTimeline、InstanceManagementService.ReadContent |
| instance.screenshots.998b17a3bbc7 | 当前截图目录交接；Management、ContentDetails |
| instance.screenshots.0a4632048b4e | 文件身份读取 → native bitmap clipboard；InstanceScreenshotService、Screenshots |
| instance.screenshots.b4a843861188 | 文件身份读取 → file clipboard 分享交接；InstanceScreenshotService、Screenshots |
| instance.screenshots.22effc520481 | 截图移至实例回收站及还原；InstanceContentTrash |
| instance.recovery.589eb002b816 | 内容更新联合事务历史；InstanceContentUpdateTransaction、ContentUpdates |
| instance.recovery.0042f30a2a2c | 联合撤回/中断恢复与冲突保留；InstanceContentUpdateTransaction.RollbackAsync |
| instance.diagnostics.d57b2a3b0791 | 有实例身份和 SessionId 的跨重启持久启动记录；InstanceDiagnostics.History、DurableDiagnosticHistorySink schema 2 |
| instance.diagnostics.17e6ddd6fbb6 | 有 FailureCode 或非零 ExitCode 的持久异常记录；InstanceDiagnostics.History、实例生命周期日志 |
| instance.diagnostics.220cd35381a6 | 此实例 SessionId 的真实进程启动耗时、窗口与时间；InstanceDiagnostics.Launch、JvmHostObservation |
| instance.diagnostics.ef6ad908dc24 | nullable measured 字段、准入标志与实际采样窗口；InstanceDiagnostics.Resources |
| instance.diagnostics.164862be0259 | 采集状态及实际有限系统事件，明确不推断因果；InstanceDiagnostics.System |

Other 提供当前实例游戏目录的只读浏览与有界文本查看，不允许任意绝对路径、脚本执行或
通过伪造 Editable 写入。与配置页共用深度、枚举与文本预算，所有目录链接仍拒绝。
截图时间线是独立可切换视图，按真实修改日期分组、显示修改时刻，每页最多 25 项；不从文件名
推断拍摄日期。Health 与 Snapshot 的消费者见下方补充边界，不能把实例成功基线算作世界快照。

## 世界健康检查与独立快照的补充边界

健康检查为用户显式触发的只读扫描，在停止游戏且持有世界 session.lock 租约时检查
level.dat、region/entity/poi 的 MCA 头、扇区分配、区块长度和压缩 NBT。拒绝重叠、越界、
截断、链接和不支持的编码，不修复或重写存档。最多 256 个 region 文件、4096 个区块、
64 MiB 压缩与 64 MiB 解压总预算，每块最多 8 MiB，每个 region 文件最多 1 GiB；超过任一预算或来源在扫描期间变化，
必须报告检查不完整，不能显示为健康。此检查不承诺验证全部游戏语义或 MOD 专有数据。

独立世界快照复用不可变内容备份，按所选实例/世界的规范绝对路径派生独立命名空间，
提供捕获、历史、对象摘要验证和恢复到新世界名称。创建与恢复持有世界租约并拒绝运行
中的实例；恢复先验证属于当前命名空间，只在 saves 下新建不存在的目标，不覆盖原世界。
session.lock 与编辑保护标记不进入快照。UI 必须调用 typed routes，历史不得混入其它世界
或普通配置备份；哈希验证结果不等同于 region 语义健康检查。

## 选中实例诊断消费者补充

诊断页只读取以规范实例路径匹配的进程 session，以及相同 SessionId 的 JVM 观察和运行
采样。启动性能显示真实进程创建时间、已测启动耗时和采样窗口；资源分析只显示有准入
标志或 nullable measured 字段的工作集、提交量、CPU、堆、native 与 GPU，不将缺失字段
或内部零值当作测量。系统事件关联显示采集支持状态及实际有限事件清单，不推断崩溃因果。
刷新为显式动作，页面退休清除捕获；对其它实例的最后 session 不得显示为当前实例数据。

启动与崩溃历史使用持久的结构化记录、规范实例身份摘要及 SessionId 过滤，分页展示，
原始路径、日志正文、自由错误消息和凭据不进入历史。当前进程中的 Sessions/Failures
不能替代跨重启的历史；底层历史 typed query 与生命周期 producer 由存储/进程边界提供。
旧 schema 无实例身份的记录不得混入当前实例历史。

终审另确认已有消费者未被目录白名单收录的条目：content DataPacks/Disabled/Problems
（619a816efe84、2ee458fdbb24、e0991a1031cc）与 files Root/Saves/Mods/ResourcePacks
（29b2e7f92b05、ff0bb86d9c48、05803a333d18、d0c2287854d6），具体行为见上方 ID 表。
诊断的 current state、preflight、最近变化、依赖、repair、bundle 六项已有分散消费者，
分别为 LaunchPage.Process、LaunchFlow/LaunchPreflightGate、Recovery、ContentGraph、
显式基线回滚方案与 About.ExportDiagnostics；不用新诊断页声称全部修复所有问题。

## 验证记录

服务契约测试涵盖真实下载更新、禁用状态、混合批量撤回、失败补偿和外部冲突，独立 NBT
fixture 的未知标签保留、数据包启停/回收/还原、世界复制/锁/备份租约，以及编码、换行、
保存预览、运行游戏禁止写入、路径与二进制准入。UI 契约测试覆盖单项与所选/全部更新、
配置显式预览再保存及实例退休。原生图片 codec 的真实 PNG fixture 由后端测试验证。
MCA fixture 独立构造实际扇区头与 raw/gzip/zlib NBT，检查重叠、越界、区块/文件/压缩尾部
截断、外部 .mcc、编码拒绝、region 数量预算和运行实例准入，检查不写回原件。UI 验证
健康扫描需显式触发、检查不完整不宣称健康、跨实例退休，以及日期来自文件 ticks 的时间线。
实例诊断 UI fixture 覆盖同名但不同路径的 SessionId 范围、缺失 measured 数据不使用
legacy 数值、持久历史 query 的实例范围、无身份旧记录排除以及晚到查询退休。
最终构建、执行套件及 AOT/裁剪证据由集中集成验证回填，不能以本文替代物理平台验收。

## macOS Java world-session lock port

The backend supplies `AvaloniaUiWorldEditLease.Acquire` as an explicit
`Func<string, IDisposable>` composition port. Services retain path admission and own the entire
mutation/copy/backup lifetime. The port uses Darwin nonblocking `fcntl(F_SETLK)` exclusive write
locking for bytes `[0,1)`, overlapping Java's whole-file `FileLock`; it holds the writable descriptor
until disposal and preserves existing `session.lock` bytes. `openat` walks already-open directory
handles with `O_NOFOLLOW`, including ancestors and the final lock file. Missing locks fail closed
at this port, and the service decides when an existing session lock requires admission.

The launcher prevents a second in-process owner of the same admitted pathname, because POSIX
record locks belong to the process. Release attempts unlock and always closes the descriptor;
failed acquisitions close every owned descriptor. The API rejects non-macOS calls before loading
Darwin libraries. Linux fixtures verify this guard only: they do not certify native macOS/Java
interoperability, APFS volumes, power loss or external processes bypassing advisory locking.

Darwin x64 calls `fcntl` with its flock structure directly. Apple ARM64's variadic ABI cannot be
represented by a fixed P/Invoke signature for fcntl's third argument, so ARM64 uses the fixed-signature
`lockf(F_TLOCK, 1)` wrapper at descriptor offset zero. It acquires the same POSIX record-lock region
and remains nonblocking; `F_ULOCK` releases it before closing the descriptor.

资源页面与详情页保留独立的操作来源。列表内 Sidecar 模块退休时，只清除模块绑定的
待执行操作；同一帧已经从详情页发出的下载、外链或可选依赖选择按原顺序执行。
整个资源站页面退休时仍丢弃其待执行操作，不将上一次页面生命周期的点击带回。
内容详情的在线结果和完整性结果分别更新自己的子树，保持本地详情实体与当前滚动；
验收读取具体事实的可见滚动位置，不把后来追加的卡片顺序当作固定布局契约。
