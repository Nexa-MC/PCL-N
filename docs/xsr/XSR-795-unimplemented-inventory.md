# XSR-795 — 未实现标记去重库存

2026-10-05。静态审计基准为 `565e5143`，本轮新增能力另行列出。
此库存区分真实生产占位、已有消费者但仍保留原始目录标记、未来独立能力、外部仓库
所有权以及人工/签名验收。发现标记不等于发现可安全开启的功能；状态订正不构成
全部功能完成、NativeAOT/trim 通过或 Alpha.6/Beta 已验收的声明。

## 计数口径

基准 `Nexa.Services.Settings/Settings/SettingsCatalog.json` 有 540 个 IA 位置，其中
459 个原始 `availability` 字段为 `NotImplemented`：70 Group、26 Choice、292 Setting、
49 State、22 Action。原始标记包含分组、选项、无持久值的事实/操作、重复入口和历史
规划，不能称为 459 项独立未实现产品功能。`SettingsCatalog.Load` 按已有消费者计算
可用性，不能仅统计 JSON 字段推断运行状态。值 schema 有 46 个定义、45 个可用消费者；
`java.compatibility` 的可选策略仍不可用，当前 Java 兼容性检查保持强制。

完整位置与稳定 ID 保留在 [entry map](settings-entry-map.md)，值与生效时机见
[value contracts](settings-value-contracts.md)，消费者证据见
[settings migration status](settings-migration-status.md)。本轮没有通过统一开启目录、
写入无人读取的键或放松校验来消除标记。目录原始计数是基准快照，不能自动用作
后续提交的剩余功能数。

## 六组生产候选及本轮切片

| 基准候选 | 源码事实 | 处置与边界 |
|---|---|---|
| 游戏进程日志 | `LaunchPageController.Process.cs` 的日志按钮显示“尚未实现”且不可执行 | [XSR-791](XSR-791-process-log-view.md) 接入有界进程输出查询与 UI；本轮验证单列，不能把自由文本输出加入遥测/诊断包 |
| 账户更衣橱 | 账户入口可到达未迁移页面 | [XSR-793](XSR-793-account-wardrobe.md) 按真实账户提供方能力接入；没有账户凭据或线上结果时不宣称在线换肤验收 |
| Bedrock 安装 | `BedrockInstallPage.pxml` 显示迁移说明，没有安装命令/消费者 | [XSR-796](XSR-796-bedrock-store-installation.md) 交付官方安装交接：Windows 固定 Minecraft for Windows 产品 `9NBLGGH2JHXJ` 的 Microsoft Store 与官方 HTTPS 页面；其他 OS 只开放官方信息页并明确不支持该 Windows 客户端。Store 负责获取/授权/安装，不伪造安装进度或结果 |
| Linux 显示事实 | `MachineEnvironmentProviders.cs` 的 display count/internal/refresh 使用固定 NotImplemented | [XSR-792](XSR-792-platform-capability-completion.md) 接入受限 XRandR 查询；Wayland/无图形会话及查询失败保留诚实状态 |
| macOS 电源事实 | `MachineHardwareCapabilityProvider.cs` 的五个 power 字段返回 NotImplemented | XSR-792 接入公开 IOPS/系统策略证据；值未知不填 false/0，真实电源设备验收另行记录 |
| Linux/macOS GPU 与 macOS 温度 | GPU budget/current/available 的平台契约及 macOS AppleSMC 温度缺少等价、可靠公开采集通道 | XSR-792 区分 PlatformUnsupported/Unknown 与未接消费者；不以容量替代预算，不以未知可用量或温度填 0；这不是实现了缺少的平台能力 |

基准另有两项现成 advanced IA 位置无消费者：`global.advanced.01c0534c8b37`
XSR State Inspector 与 `global.advanced.8d723d8dcdfb` Renderer Diagnostics。
[XSR-794](XSR-794-developer-diagnostics.md) 的范围是显式进入/刷新的有界只读元数据；
不增加持久化 debug 开关、不读取 State 值、不记录 JVM 参数或引入周期采样。
XSR-791–794 和 XSR-796 已完成当前生产接入，集成执行证据见本文末尾。
两项 diagnostics 目录位置改为只读 State / Available：目录仍有 540 个位置，当前
83 Available / 457 原始 NotImplemented。余项按下面的消费者映射与独立规划保留，
不将原始目录计数当作独立产品缺口数。

## 历史缺口的关闭映射

| 旧条目/文档 | 基准已交付消费者 | 仍开放的范围 |
|---|---|---|
| Alpha 5 更新、review-505b9f9f 的未接 helper、Alpha.6 更新事务 | XSR-754/755：预安装受保护 helper、独立验签、对象绑定不可变 slot、持久防回退、恢复/激活/回滚/普通用户重启、差分与完整包回退 | 生产 OS 发布者身份；物理断电和替换竞态。便携/不安全安装仍手动更新；旧 `UpdateStaging.ApplyPlan`/staged helper 必须拒绝 |
| instance-management / instance-content-transactions 的资源实际更新 | XSR-758/774：文件指纹关联、兼容版本查询、资源包/光影单项可恢复替换 | `ResourceContentUpdateService` 只准入 resourcepacks/shaderpacks；模组实际更新、选择/批量更新、changelog、多项同事务回滚仍缺 |
| 同文档的服务器编辑/Quick Join | XSR-759：有界 NBT、未知 tag 保留、revision 保护、增改删/排序、状态探测、一次启动 Join | 图标缓存保留不等于所有展示/平台交互已验收；更广服务器环境/认证策略仍需消费者 |
| 同文档的整合包导出 | XSR-760：选中文件预览、标准 MRPack、预算与任务/取消/冲突保护 | 不承诺任意导出格式、任意 loader、Thin Backup 或同步能力 |
| Alpha 5 / instance-management 的截图预览 | `InstanceContentMetadata` 与 `SettingsPageController.ContentDetails`：有界缩略图、虚拟画廊、详情及大小/时间 | 裁剪、更丰富元数据、复制/分享工作流仍未交付 |
| instance-management 的搜索/启停/删除 | Service 清单、过滤/虚拟行、`SetModEnabledAsync`、持久 trash/restore 和共享活动目录保护 | 不是完整 loader 兼容性或世界数据包管理；不会永久清空未知内容 |
| instance-recovery 的仅准备/内部补偿、尚无命令/UI | 已封存逐条/全部/类别恢复命令、设置文件联合补偿、崩溃入口、启动中断恢复 | 恢复目标仍为最新成功基线；存档/截图不在该恢复范围；物理断电/冲突证据仍需验收 |
| Alpha 5 的 Java/整合包/processor/改名和排队退出恢复缺口 | 当前 `resumable-installation.md`：持久计划/下载身份、统一关闭守卫、恢复队列、Java/整合包、处理器隔离及改名重放/补偿 | 不保证第三方安装器内部逐指令 checkpoint；平台窗口交互及实际中断独立验收 |
| alpha5-desktop / XSR-735 的未接 compiler/point | XSR-736：CoreCompile 前工具改写与资源标题真实 point；运行同步 string ABI 五阶段、有界预算及退役语义 | 泛型/实例/async/ref/迭代器、语义绑定调用及更多 ABI/hook adapter 不支持 |
| Alpha.6 / XSR-404 的仅声明、string-only、未有 stream/health/lifecycle | XSR-747–749 adapters；XSR-790 Host typed payload、stream/credit、health、卸载、监督和独立 Protocol/Transport 消费 | 任意交互 UI、Plugin 引擎/SDK/package/UI IR freeze 属外部仓库；额外 Function ABI 和真实插件长期验收仍独立 |
| Alpha 5 的嵌套候选/JAR 与配置指纹 | `mod-content-calibration.md`：Fabric/Quilt/JarJar 候选、有界 JAR 哈希和配置内容指纹、退出复核 | 声明候选不等于实际 loaded set；完整 Forge/复合依赖语法、工作负载阶段和合格模型样本仍缺 |
| 性能的日志空闲、进度频率、encoded/raster、collection/read、版本实体规模 | XSR-727、729、731–744：事件驱动空闲、有界发布/LRU、pixel charge、虚拟行、稳态指针分配回归和可重放 fixture | 不能从 pixel charge 推断实际 native/GPU 上限；OS pressure、剩余 background admission、early Splash 及物理 KPI 仍开放 |
| migration-map 的 XSR-003 pending | `ArchitectureTests/Program.Semantic.cs` 已有 Roslyn 语义边界/阻塞 API 校验，另有项目与反射限制 | 不是完整未来 Plugin SDK 稳定性分析器；XSR-004 的完整 legacy parity corpus 仍需收口 |

## 全部目录族的实现与未来映射

原始 NI 数包含本行的结构/选项；下表覆盖全部 26 个存在原始标记的 scope/page 族。
“已有”表示能力在工作消费者入口已经存在，不将同名 keyless 目录项自动启用。
未定义值或执行语义的 IA 位置仍保留，不为缩小数字而捏造契约。

| Scope/page | 原始 NI 数 | 已有消费者/展示 | 仍未来或不可等同的能力 |
|---|---:|---|---|
| global/general | 19 | 界面语言、区域格式、developer 可见性 | autostart、single-instance 激活、tray/后台生命周期、关闭/启动行为策略、通知、剪贴板自动探测、URI/文件关联/Jump List |
| global/appearance | 27 | 系统/浅/深主题、有限强调色、动画/帧率/窗口锁、低功耗、现有 Reduced Motion 机制 | 任意主题/Logo、多媒体背景/音乐、窗口透明/模糊；独立减少动态效果设置及此页四项 developer 诊断未接 |
| global/game | 23 | 内存/Java/窗口/参数、启动器呈现、隔离、server、priority、强制 preflight/修复与 quiet 基础 | GPU 选择、任意默认 renderer/native 兼容开关、资产验证绕过、额外资源 handoff/提示策略和启动模板；强制检查不是可关闭选项 |
| global/java | 19 | 库存/扫描/选择、runtime 事实、注册/启停、外部解除注册、受所有权保护的托管删除、兼容优先级/获取 | 独立全量 probe/module/raw 页；可选兼容策略不可用，扫描时校验不等于每个同名独立操作入口 |
| global/network | 21 | 来源/并发/重试、proxy 原子表单、DoH/system fallback、IP-family 顺序、共有传输正文预算 | provider 启停、后台下载、额外来源策略、依赖自动安装设置、自动诊断、Endpoint/probe/retry trace 页面；不承诺 OS/helper 全流量限速 |
| global/storage | 41 | 设置导入/预览/原子应用/reset、数据位置迁移、保守临时/成功卡片清理、实例快照统计基础 | 全部 roots 重定位、引用感知 CAS/去重/hardlink/reflink/CoW、全局占用/自动 pruning、各启动器迁移助手/Portable 检查 |
| global/privacy | 35 | 日志等级/保留、磁盘轮转/有界事实导出、主动脱敏诊断包、telemetry、crash/preflight 基础 | 持久 richer histories、系统事件/TDR/WHEA/native correlation、AI provider/model/budget/data-share 契约、通用安全复制/实时 State/Launch trace 页 |
| global/advanced | 37 | 通道与自动发现、受保护更新、开发者可见性；本轮两个只读 diagnostics 切片 | 背景更新策略、Safe Mode、任意兼容 workaround、CLI/command palette/deep link、实验首页/后端/AI 修复、任意 debug delay/skip-copy/raw settings/feature flags |
| instance/overview | 13 | 元数据概览、组件/目录事实、启动/修改/目录入口 | 最近启动/游戏时长、完整 health/resource summary、全部自定义 identity/tag/note 操作 |
| instance/content | 8 | mods/resourcepacks/shaders 列表、关联/分类、启停/回收、图和 pack/shader 单项更新 | data packs、完整兼容冲突判定与模组/批量实际更新 |
| instance/worlds | 10 | 有界 saves 目录项、打开及通用回收/还原 | level 元数据/版本/最近游玩/大小/health、世界专属备份/snapshot/锁/duplicate、datapacks |
| instance/servers | 5 | NBT server list 编辑/排序、状态、临时 Join、共享 server 选择策略 | 更广 Environment Match/Compatibility 页面；Quick Join 不等于全部服务器匹配策略 |
| instance/files | 8 | 实际目录定位和打开、受控内容操作 | 任意文件浏览器/编辑器及全部 logs/crash reports/config 管理工作流 |
| instance/screenshots | 6 | 虚拟画廊/详情、目录、通用回收/还原、大小/修改时间 | timeline、复制/分享、裁剪及更丰富元数据 |
| instance/recovery | 8 | 最新成功基线、history 列表、diff、逐条/全部/类别 restore 和崩溃/启动补偿 | 统一 change/update timeline、任意历史目标恢复及不同语义 Undo；不回滚未备份存档 |
| instance/diagnostics | 11 | 平台/preflight、crash 分析、变化摘要、修复和主动诊断包的既有入口 | 持久 launch/crash histories、完整 instance integrity、系统相关性及独立全量资源/性能工作区 |
| instance-settings/basic | 14 | 真实改名、目录/description/modpack 事实、既有隔离策略 | 任意图标/描述编辑、tags/group/notes/custom info 与完整收藏流程 |
| instance-settings/java | 9 | compatible runtime 选择、inventory 事实/扫描、获取/registration 基础 | 独立 raw module/probe 页面及可关闭兼容策略 |
| instance-settings/resources | 22 | 内存、估算/provenance/同实例历史、captured priority 和 quiet 基础 | 指定 GPU/renderer、细粒度显存/共享内存估算、shader/resource/render-distance 独立建议及额外 handoff |
| instance-settings/window | 10 | 窗口尺寸/模式、Windows title、启动器 visibility/退出恢复已有消费者 | 非 Windows title、额外独立呈现策略；重复 Choice 不形成另一份存储 |
| instance-settings/hooks | 14 | JVM/game 参数、wrapper、prelaunch/wait 和 owned-process 取消 | classpath head/env/postexit，以及 JLW/RW/System GLFW/X11/unsafe/log4j 专用设置 |
| instance-settings/profiles | 26 | 优先级模型已有 Builtin/Global/Instance，Profile/Temporary 仅解析语义 | 任意 Profile Override、临时文件 overlay、Safe Launch；公共 mutation 尚拒绝，需生命周期/恢复契约 |
| instance-settings/servers | 10 | 同一 game.server 值和一次 Join，现有账户 authlib 选择 | 新认证 Requirement/Auth Server/Register URL/Display Name/Lock 设置不能由现有账户认证推出 |
| instance-settings/security | 30 | 强制 preflight、文件补全/校验、typed remediation、内容 hash/图线索、pack/shader rollback 基础 | 自动内容更新/source lock、完整 impact/risk、模组同事务更新、World Guardian、ignore policy/unknown-content 策略；不开放绕过真实性/兼容校验 |
| instance-settings/backup | 18 | 最新成功基线及可选保留 history，现有本地恢复对象寻址 | 独立世界/配置/截图备份、Thin Backup manifest/lockfile、完整 Offline Readiness/Prepare Offline；内容寻址快照不是全产品 CAS |
| instance-settings/advanced | 15 | 已封存有效启动计划/State 与运行日志基础；本轮有界 metadata inspector | 全量 effective command/classpath/env/native/manifest/lockfile raw 页面、跨历史 trace、兼容/assets 验证绕过；私密 JVM 参数不应复制到通用诊断 |

## 独立能力及外部依赖

- Profiles/Temporary/Safe Launch 需定义隔离写入、撤销、异常中断恢复、运行期间所有权
  和取消；优先级模型存在不等于可修改任意 overlay。
- CAS/dedup/hardlink/reflink/CoW、引用回收、snapshot pruning 和自动删除需先定义
  文件身份、引用和平台一致性。恢复对象按摘要寻址不等于全部内容已迁入 CAS。
- AI 诊断/修复需真实 provider、授权和数据范围、有界请求、取消以及可审查的修复
  执行契约。schema-1 资源模型已经退役；后继模型需要 loaded-set/世界阶段、合格
  数据、保留集验证和准入，不用没有有效样本的权重宣称训练完成。
- 完整 1.x/其他启动器迁移助手需逐字段转换预览、兼容性、撤销及账户安全语义；
  当前目录/实例/账户导入和 settings JSON transfer 不能替代整份助手。
- Bedrock 独立包下载/安装引擎没有本仓执行契约、来源授权和生命周期，保留为真实
  未来规划。XSR-796 只完成官方 Store 交接，不自动获得 entitlement、购买或安装成功。
- Cloud/cloud sync 已从当前产品范围排除。历史 IA 保留提及不是本轮授权新增云服务。
- Plugin SDK、执行引擎、package/UI IR freeze 与任意插件 UI 由外部仓库承担；本仓
  Host API 和 fixtures 不能替代真实 SDK 或其完整执行验收。
- 共享客户端 mTLS PFX 的安装身份改造需要 API 服务端策略及部署证据；不得直接删除
  认证策略来消除该外部缺口。

## 验收库存

`alpha6-status.json` 继续是 `not-accepted`。126 个 Minecraft 候选的实机世界进入/正常
退出，8h 原生 idle/游戏运行，DPI/字体/读屏/高对比/触摸/笔/手柄、文件管理器拖放、
窗口动效、实际 CPU/RAM/GPU/frame tails 及断电/冲突语料属于受控或人工证据工作。
生产 Authenticode、Developer ID/notarization 与 SmartScreen/Gatekeeper 是外部发布身份
和平台信任条件。保留的 2h/30min composition fixture、NativeAOT/trim smoke 和 hosted
CI 不得改称这些证据。缺少可测事实时保持 unknown/null，不把等待 8h 实测当成代码 stub。

性能仍有可实现的独立缺口：早期可交互 Splash、真实初始化阶段/错误与首次呈现
instrumentation；OS memory-pressure adapter、实际 GPU 图像驻留预算和剩余后台
admission 接线。64 MiB pixel charge 是明确计账单位，不是实测 native/GPU/RAM SLA。
完整目标见 [runtime performance](runtime-performance.md)。本轮文档订正不关闭这些工作。

## 本轮集成交付与验证

2026-10-05，在 Debian 13 x64 云环境，构建版本种子为 `2.0.0.ci.565e51`。
XSR-791/793/794/796 已由 Desktop composition root 接入；文件选择、剪贴板与官方
Store/HTTPS 操作使用原生效果适配器。XSR-792 提供真实平台检测与明确未知/不支持
结果。原先为测试或尚未注入控制器保留的通用 VersionSubpage 不构成生产页面入口；
生产版本设置、更衣橱和 Bedrock 控制器均已替换对应描述符。

- Release 完整重建及最终集成构建：0 warning / 0 error。
- Services 599、Desktop 176：CoreCLR 与 NativeAOT 均通过。
- Sidecar Protocol/Transport 35、XSR Runtime/Host 164、UI.Next 97、Avalonia backend 15、
  PXML 40：CoreCLR 通过；架构门禁覆盖 70 个项目，UI.Next benchmark gate 通过。
- Desktop NativeAOT 与 `PublishTrimmed` / `TrimMode=link` 发布均通过，分别执行
  `--validate-shell` 和 `--validate-setup`，退出码均为 0；发布没有 AOT/trim warning。
- 验收脚本 22 例、policy-only 校验、Alpha.6 状态审计通过；验收状态仍为
  `not-accepted (0/9 accepted)`，不以结构检查替代真实客户端或长时运行。

新增回归覆盖实际进程 stdout/stderr、会话过期与取消、PNG 完整性和解压预算、
账号选择/凭据代际与迟到请求、换肤后刷新及失败重试、读取失败不循环请求、
响应和披风库存上限、空闲帧复用、开发者元数据不格式化 payload，以及官方
Bedrock 按钮的来源/页面/平台限制。语言目录补齐固定文案与模板的 en/zh-Hant；
游戏输出、提供方披风名和 State 元数据保持原文。

Windows/macOS 原生设备与 Microsoft Store 的最终安装结果、真实在线账户操作、
发布者签名和此前列出的实机验收仍需各自的环境与证据。本轮不声称未来规划、
外部 Plugin SDK/引擎或这些验收已经完成。
