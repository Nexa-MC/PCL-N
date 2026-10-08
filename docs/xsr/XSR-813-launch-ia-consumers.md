# XSR-813 — 启动与实例 IA 消费者核对

2026-10-08。此表核对精确语义，不以 catalog 标记或保存值代替运行行为。

| Exact ID | 真实消费者与约束 |
| --- | --- |
| `instance-settings.profiles.d535f1769202` | 命名 Profile 的 `game.jvm`；普通 JVM 表单写 ProfileId，coordinator 读取捕获有效值。 |
| `instance-settings.profiles.2cbc9526055b` | 同一路径的 `game.memory`，包含合法 Auto/Custom 解析。 |
| `instance-settings.profiles.bbe9aad82dcf` | 同一路径的 `game.server`，根据版本生成 quick-play/server 参数。 |
| `instance-settings.profiles.8557766cf81a` | Temporary 的 `game.jvm`；BeginTemporary 更新会话字典，结束撤销，不持久化临时参数。 |
| `instance-settings.hooks.27198ce02cfc` | `game.post-exit`；catalog 已绑定可编辑 key，命令实际在游戏退出和覆盖恢复后执行，2 分钟有界。 |
| `instance.servers.a8a1cc4caac7` | `Servers.cs` / typed servers.dat 列表读取、编辑、排序、删除。 |
| `instance.servers.8c6092b5ce84` | Identity 的默认服务器编辑及 coordinator 的启动消费者。 |
| `instance.servers.de9988a1e55f` | Identity 的声明版本/加载器/必需模组对照，coordinator 启动前收紧准入。 |
| `instance.servers.73ea41d2f3c0` | Servers 加入操作捕获实例与地址，root 的 JoinManagementServer 路由启动。 |
| `instance-settings.servers.36e4dd9de50c` | 已锁定认证配置为只读，服务验证禁止变更；没有供用户解除强制锁的开关。 |
| `instance-settings.basic.4c5dfb0bcc3d` | 概览显示真实目录，修改版本页允许改名，通过原有同卷 journal 与 Settings/Profile 移动事务。 |
| `global.game.eecd18a1df09`, `instance-settings.window.f7c129eae162` | 已捕获 `game.launcher-visibility` 的 hide 恢复 / hide-and-close 退出，由 DesktopGameWindowSession 消费；不是独立第二套退出 preference。 |
| `instance-settings.window.b28c9d125ee6`, `instance-settings.window.44effed05745`, `instance-settings.window.0322e116710e` | 同一 `game.launcher-visibility` 的 keep/minimize/hide-and-close 选项，不是三个布尔设置。 |
| `instance-settings.basic.09d985efd446` | Identity 的实例隔离编辑，保存受 revision 约束；管理页 GameDirectory 刷新，coordinator 与内容服务使用同一 metadata.InstanceIsolation。 |
| `instance-settings.basic.b7c88a7cee30`, `instance-settings.basic.99c5e92def4c`, `instance.overview.d1e245c8ad1c` | 整合包项目与版本 typed bounded 保存/读取；不是联网更新版本查询。 |
| `instance.overview.fbfe8d779951`, `instance.overview.78c89aeac0d1`, `instance.overview.70d026453c0f` | Identity 名称/PNG 图标与管理页 Minecraft；加载器取当前 manifest 的正式 detector。 |
| `instance.overview.8228a17a1eac` | 实例记录 Java 偏好；继承时明确启动阶段选择，不将偏好路径声称为已验证的运行时。 |
| `instance.overview.a3f8bc20aaf9`, `instance.overview.a0a265bd720b` | 本次启动器最近 256 条真实进程记录中的最近启动/已观察进程用时；UI 明示统计范围，不是此前全部游戏历史。 |
| `instance.overview.988b4b01b3eb`, `instance.overview.732a45f9e791` | 实际离线依赖检查结果及有效 memory 设置；缺少检查或硬件测量时不伪造结果。 |
| `instance.overview.56e33e480cdf`, `instance.overview.17a004456ff2` | 当前 library 身份验证后调用既有 LaunchFlow/InstallEditor。 |
| `instance.overview.90172f2592b8`, `instance.overview.7fb60a50d168` | 实例目录打开；版本行 context menu 真实绑定该行的修改/设置/删除三操作。 |
| `instance-settings.profiles.6a86c1d65288`, `instance-settings.profiles.aa2b636c351e`, `instance-settings.profiles.fe088946fb87`, `instance-settings.profiles.cb1c4275b7f6` | LaunchProfiles 卡显示 Safe Launch 四个固定只读效果，下游 SafeLaunchPolicy 强制消费。 |
| `instance-settings.advanced.d5129739b1ae` | SettingsPolicy 的 active Profile/Temporary 有效值与来源，普通表单及 Profile 卡实际展示。 |
| `instance-settings.advanced.bdc6b2186762`, `instance-settings.advanced.9c7f83375a0b` | AdvancedLaunchDiagnostics 的实际执行器捕获计划及逐项脱敏命令；未捕获为未知。 |
| `instance-settings.advanced.b84f343da972`, `instance-settings.advanced.504c5f4447d2` | 同一捕获快照中 Classpath 条目和 Native 归档，仅实际解析结果。 |
| `instance-settings.advanced.6f200d616dd1`, `instance-settings.advanced.e89dd4f9c2a4` | InstanceLocalDocuments typed query 的有界脱敏本地 Manifest/metadata；损坏原文保留并显示失败。 |
| `instance-settings.advanced.25c2ac9fa4ed` | 已捕获计划时间与本次启动器进程开始/结束观察，明确没有补造未观察阶段。 |

Safe Launch 已保证禁用所有 shaders/resourcepacks/custom hooks/custom JVM 参数。
`instance-settings.profiles.6a86c1d65288`, `.aa2b636c351e`, `.fe088946fb87`,
`.cb1c4275b7f6` 是此固定策略的四个效果，可展示为只读策略事实；不能宣称有四个
可反向放宽的独立开关。`instance-settings.profiles.412a989699be` 目前全量移走 mods，
不等同于保留旧 mods、只移走“最近新增”子集，不能按后者标记。

System GLFW 的 `game.system-glfw` 由共享 schema 声明为 false、InstanceOverride、
NextLaunch。只有非 Builtin 来源覆盖已捕获 request.UseSystemGlfw；显式 false 也能
关闭旧 metadata/legacy 偏好，Builtin 保留旧输入兼容。有效 true 继续使用实际 GLFW
native 归档过滤器，不虚构新的本地库安装或系统兼容保证。Safe Launch 始终归一为
false，恢复随版本提供的 native 归档。对应 exact IDs 为 `global.game.37621820582f`
和 `instance-settings.hooks.d32f82a77b56`，普通表单通过同一 typed policy 写入。
验证走完整 Coordinator.StartAsync → Planner → Executor → JVM Host 的捕获计划，
检查 native 归档与实际解压文件，以及全局/实例显式设置、默认兼容和安全启动。

JLW/RW/Debug Log4j/LWJGL Unsafe Agent、Force X11、
Renderer Backend 的 catalog 设置不能仅凭 metadata 字段存在标为可用。
安全/完整性开关同理：强制检查是实际策略，关闭能力不因此成立。

本轮进一步把实例隔离、modpack 项目/版本与概览的加载器、Java、启动计数/本次
会话时间接入同一个 bounded typed identity 快照与修订检查；不从缺失历史伪造
累计数据。准确消费者已列在表中，构建、AOT 与测试由根集成任务统一验证。

概览启动/修改入口只接受绝对实例路径。LaunchPageController 必须核对当前 library
完整快照中的 selected identity、root/versions/id 路径及 descriptor 路径，拒绝已变化或
正在扫描的选择，再复用既有账户捕获、LaunchFlow 与 InstallEditor。入口不通过伪造
UI intent source 或更改当前选择来启动另一个实例。

服务器兼容事实对照实际 status.version.protocol 与本地客户端 JAR 的 version.json
protocol_version。客户端采用至多 32 层、16 MiB manifest 读取预算，至多 512 MiB
客户端摘要校验；只接受声明 SHA-1 或已记录核心补丁 SHA-256 匹配的客户端。
ZIP 至多 16384 项，version.json 至多 64 KiB，未知/缺失/损坏/正在变化均返回未知。
协议一致只表示该观测时刻的协议号相同，不承诺模组、认证或代理兼容，也不代替
启动前的独立检查。检查不会修复、下载或启动游戏。

高级只读检查由两个独立 typed queries 提供。执行器在图形环境策略应用成功后、
启动前捕获实际计划；最多保留 16 个实例、每个 128 KiB 字符预算及每类 2048 项。
保存前移除身份参数、凭证和全部环境值、所有 hook 正文。UI 标注捕获时间、截断及
阶段，缺少捕获时显示未知；不为预览调用 PrepareAsync、刷新账户或下载依赖。
本地文档读取最多 32 层 manifest、每文档 2 MiB、总读取 16 MiB，保留读取失败状态，
最多显示 128 KiB 脱敏 JSON。原文件无写入；当前架构没有独立实例 lockfile 格式，
视图明确显示未生成，不发明一个有效锁文件。实例诊断页面分页，每页最多 32 行。

`instance-settings.advanced.8acf76e45c8e` 只有未生成独立 Lockfile 的事实视图，不能
将锁文件编辑、导入或复现能力标为可用。`instance-settings.advanced.5f06019abe3e`、
`instance-settings.advanced.787b274645ff`、`instance-settings.advanced.e8914ed0d7e2`
在 XSR 没有对应准入绕过 API，强制兼容与文件真实性保持独立。这些历史破坏开关
属于退役行为，不能把“拒绝关闭”伪装成可写设置。

`instance-settings.hooks.d675482eefee`、`instance-settings.hooks.d5047c9dd72e`、
`instance-settings.hooks.09ebeb623281`、`instance-settings.hooks.3a071fa4c0d7`、
`instance-settings.hooks.50de4c103e2a` 与对应 global 原生兼容条目没有 XSR 执行 API。
JLW/RW 是旧启动包装器内部选项；本架构的 JVM Host/Wrapper/typed环境端口不暴露
这些旧内部开关。Debug Log4j 与 LWJGL agent 不能靠存在 metadata 字段声称实现；
新的日志/图形契约应单独呈现真实能力，历史行必须标明退役或无对应 API。

验证代码：Services 的 `LaunchDiagnosticsCaptureRedactsAndBoundsWithoutMutatingPlan`
和 `LocalLaunchDocumentsReadBoundedInheritanceRedactAndRetainDamage`；Desktop 的
`AdvancedLaunchFactsPageUsesReadOnlyTypedQueriesAndPagedSnapshots` 与原有 Identity
editor 测试新增隔离、整合包字段和真实启动/修改入口拒绝错实例断言。

World Guardian 的迁移边界：只读核对 `origin/dev` 的 `SaveManager.cs:159-205`、
`DataVersionBoundaries.cs`、`PageInstanceSavesInfoRight.axaml.cs:114` 与全部
Launching 路径。dev 的 DataVersion 用于选择存档解析器/编辑器和数据包 UI 准入；
没有读取客户端 `world_version` 与世界 DataVersion 的启动前比较，也没有升降级
阻断或启动自动快照 producer。原子编辑的 level.dat_old 备份不等于启动保护快照。
XSR 锁文档 `settings-ia-source.md:691` 明确写“未来 World Guardian 继续填入这里”，
`settings-next-development-plan.md:135` 亦列为依赖边界完成后开放的独立能力包。
因此 `instance-settings.security.88ba1be4830d`、
`instance-settings.security.0f7c5e74b592`、
`instance-settings.security.89e2cc2879c7` 是规划能力，不能冒充已迁移 dev 行为；
本轮不创建没有既有行为契约的自动世界保护服务。已有 World Health 与用户显式
CAS snapshot 保持它们自己的真实消费者与准入边界。
