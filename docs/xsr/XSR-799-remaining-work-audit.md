# XSR-799 — 剩余内容核查

2026-10-08（Asia/Shanghai），源码基准 `0646d97a` / `refactor/xsr`。
本次为静态审计与文档订正，没有实现或开启保留功能，没有修改发布验收状态。
范围为 PCL-N；外部 Nexa.Plugin SDK/执行引擎不属于本次工作。

## 当前数量

`SettingsCatalog.json` 共 540 个 IA 位置，原始状态为 87 Available、453 NotImplemented。

| 类型 | Available | NotImplemented |
|---|---:|---:|
| Group | 4 | 70 |
| Choice | 0 | 26 |
| Setting | 63 | 286 |
| State | 8 | 49 |
| Action | 12 | 22 |

286 个未实现 Setting 位置中，285 个没有持久值 key。目录包含分组、重复入口、事实、
专用管理页中的已有操作及未来规划，不能把 453 个标记或 286 个 Setting 位置称为
独立未实现功能数。Windows 的有效状态与原始计数相同；Linux/macOS 的三个
`game.title` 位置另为 PlatformUnsupported，即 84 Available / 453 NotImplemented /
3 PlatformUnsupported，见 [目录消费者准入](../../Nexa.Services.Settings/Settings/SettingsCatalog.cs)。

值契约有 50 个定义，其中 49 个已有可用目录消费者。唯一保留但未开放的定义是
`java.compatibility`；现有 Java 兼容性检查保持强制。

## 直接影响当前行为的缺口

1. **普通第三方账户登录后不能启动游戏。**
   [AccountLoginProfiles.FromYggdrasil](../../Nexa.Services.Accounts/Accounts/AccountLoginProfiles.cs)
   对非 LittleSkin 站点保存 `ThirdParty`；
   [AccountLaunchIdentityResolver.ResolveAsync](../../Nexa.Services.Accounts/Accounts/AccountLaunchIdentityResolver.cs)
   只处理 Offline/Microsoft/LittleSkin，其他种类返回 LaunchNotSupported。
   [启动页](../../Nexa.Desktop/Ui/LaunchPageController.cs) 的 SelectedProfileCanLaunch 同样
   排除 ThirdParty。生产 composition 注入该 resolver，属于真实缺口，不能用登录成功
   推断启动支持完成。NCloud 导入兼容种类也缺生产启动提供方，Cloud 产品范围仍排除。

2. **模组与批量内容更新未完成。**
   [ResourceContentUpdateService](../../Nexa.Services.Resources/Resources/ResourceContentUpdateService.cs)
   只准入资源包、光影包。模组实际更新、选择/批量更新、changelog、多项同事务回滚
   仍缺；现有 mod 搜索、启停、回收和内容图不替代更新执行器。

3. **任意启动配置、临时覆盖和 Safe Launch 未完成。**
   [SettingsPolicyService.ValidateMutation](../../Nexa.Services.Settings/Settings/SettingsPolicyService.cs)
   只允许 Global/Instance。Profile/Temporary 仅有内部解析模型，没有可编辑的完整
   生命周期、文件 overlay、撤销及异常恢复。Classpath Head、环境变量和 Post-exit
   命令等独立 Hooks 也仍保留；Wrapper/Pre-launch/等待和取消已经完成。

4. **JVM Host 的部分控制和观测只有能力声明。**
   [JvmHostCapabilities](../../Nexa.Services.Minecraft.Process/Minecraft/Process/JvmHostCapabilities.cs)
   的 CPU Sets、QoS、进程提交量、GPU、进程树聚合和系统事件关联固定返回
   DependencyMissing；标准输入也未开放。Heap/native/commit/GPU 的完整真实采集与
   runtime 聚合尚未接齐，不应把未采集的内部零值当成实际使用量。这些属于监测和
   调度扩展缺口，基础启动、stdout/stderr、CPU/内存等已有消费者不重复计缺口。

5. **早期可交互 Splash 和性能适配仍缺。**
   [原生启动生命周期](../../Nexa.UI.Next.Backend.Avalonia/AvaloniaUiShellLifetime.cs)
   先构造主窗口再显示装饰 Splash；缺真实初始化/错误阶段与首次呈现指标。
   OS 内存压力适配、实测 GPU 图片驻留预算和剩余后台工作让路也尚未完成，见
   [性能契约](runtime-performance.md)。已有像素计账及短时 fixture 不证明原生资源 SLA。

## 其他保留产品工作

完整稳定 ID 保留在 [设置入口表](settings-entry-map.md)，行为关闭映射见
[XSR-795](XSR-795-unimplemented-inventory.md)。以下为去重后的工作类别，不能从
同名 keyless 行推断整类功能不存在。

| 类别 | 剩余内容 |
|---|---|
| 系统集成 | 开机启动、文件关联、原生通知、剪贴板自动检测；`nexacl://` 已完成，旧 `nexa://` IA 字样不是另一个协议任务 |
| 外观与媒体 | 任意自定义主题/Logo、背景视频、透明/模糊、多媒体和媒体控制；现有主题、强调色、动画、低功耗已接通 |
| 网络 | provider 启停、后台下载策略、自动诊断及独立 endpoint/probe/retry trace；现有代理/DoH/IP 顺序和正文预算已接通 |
| 世界、数据包、截图 | 世界 level 元数据、datapack、世界专属备份/锁/复制、截图裁剪/分享和更多元数据；目录列表及画廊已存在 |
| 实例与诊断 | 完整 identity/tag/note/图标/描述编辑、服务器认证/环境匹配、World Guardian、持久启动/崩溃历史、完整系统事件关联和 raw 工作区 |
| 存储与迁移 | 其他 roots 重定位、引用感知 CAS/去重/hardlink/reflink/CoW、自动 pruning、世界/配置/截图独立备份、Thin Backup、完整 Offline Readiness、1.x/其他启动器迁移助手 |
| AI 与高级工作区 | provider、模型、预算、授权/数据范围及修复执行契约；Safe Mode、CLI/command palette 等仍保留 |
| Sidecar 后续扩展 | 更多 Function ABI、语义绑定及 hook/visual adapter、任意交互式页面/图像/live State；当前 Host API、二进制协议、生命周期和有限 UI adapter 已完成 |

Cloud/cloud sync 已排除；独立 Bedrock 包下载/安装引擎仍为未来契约，当前完成的是
官方 Microsoft Store/HTTPS 交接。外部 Plugin SDK/引擎不能计作 PCL-N Host API stub。

## 两项设置契约/展示待收口

- [SettingsPageController](../../Nexa.Desktop/Ui/SettingsPageController.cs) 仅生成
  Available 且具有 Definition 的普通表单。未实现/平台不支持位置被过滤，未实现
  “保持最终位置、禁用并标注尚未可用”的历史 IA 展示方案；需选择是否继续该方案，
  不能靠开启无人读取的值解决。
- `java.compatibility` 虽未开放，mutation 校验仍准许保存该值，API/导入的 `false`
  不会关闭强制兼容性检查。无消费者定义的写入准入和反馈仍需契约收口。

## 实机、线上和发布身份验收

[alpha6-status.json](../../eng/acceptance/alpha6-status.json) 保持 `not-accepted`：
9 个工作流中 5 partial、3 blocked-external、1 pending，0 accepted。

- 126 个真实 Minecraft 候选组合进入世界与正常退出。
- 8h 原生窗口 idle 与 8h 游戏运行、受控资源/帧尾和交互数据。
- 三平台更新事务的物理断电、替换竞争、真实故障和冲突恢复语料。
- 生产 Authenticode、Developer ID/notarization、SmartScreen/Gatekeeper。
- Windows/macOS 托盘与协议回调、真实在线账户换肤、DPI/字体/读屏/高对比/
  触摸/笔/手柄、文件管理器拖放，以及真实插件长期生命周期。

这些是待取得的验收证据，与可在当前仓库直接实现的功能缺口分开记录。

## 本次订正和核查

修正 settings-migration-status 与 settings-next-development-plan 中称为“当前”的
旧 46/45、83/457 计数；XSR-795 的明示历史基准及已执行测试数字保留。
生产 C#/PXML/Python 范围未检出 TODO、FIXME 或 NotImplementedException。
通用 VersionSubpage 及旧本地化文案仍存在，但更衣橱/版本设置的生产描述符由完整
控制器替换；不能单凭残留字符串判定已交付页面未迁移。
MachineCapabilityBroker 的默认 NotImplemented 是尚未由 provider、派生规则或
投影提供事实的兜底，不代表所有机器检测均未接入。退役的不安全更新入口明确拒绝，
生产受保护更新不属 stub。

重新读取 JSON 计算数量并核对消费者白名单；执行
`python eng/acceptance/audit_alpha6.py` 通过索引结构检查，输出 `0/9 accepted`。
未运行新的运行时测试或实机验收，本次提交仅含审计文档及计数订正。
