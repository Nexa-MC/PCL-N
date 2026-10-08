# XSR-818 — 实例操作阶段历史与服务器协议事实

实例操作历史复用 `logging.diagnostics.history.instance`，以规范实例身份摘要精确过滤，
保留每个实际 Operation/Stage/Outcome；不按 SessionId 合并掉过程事件。每页最多 12 项，
只显示结构化已记录事实和会话身份，最多 1024 条保留记录；无实例身份的旧记录不混入。
历史查询随实例或页面退休取消，不执行操作，也不保存路径、日志正文或凭据。

服务器兼容状态只比较网络协议数值：服务器 status 包实际 `version.protocol` 与启动解析
所选本地客户端 JAR 根目录 `version.json` 的实际 `protocol_version`。本地版本继承/jar
引用最多 32 层，版本 JSON 单个 2 MiB/合计 16 MiB，实例元数据 256 KiB；客户端 JAR
最多 512 MiB、16384 项，中央目录最多 4 MiB，在分配 ZIP 条目之前核对实际记录数，
且唯一根目录 version.json 最多 64 KiB。发现最多 1024 版本目录/16384 文件；拒绝
链接、循环、越界、重复 protocol 字段与 metadata 条目、ZIP64、多磁盘及来源变化。
本地 instance/baseId.jar 优先，随后使用启动器的根版本/文件名/manifest fallback 顺序。
版本 JSON 支持本实例的常规同名文件与常规继承/jar alias；无法证明启动解析选择的
非规范 manifest 布局显示未知，避免读取其他 JAR 后误报协议一致。
无本地/服务器数值或不受支持的旧客户端时显示未知，不猜测版本映射表，不使用版本名
相同证明兼容。数值相同只意味着网络协议一致，不能证明 MOD、认证或完整环境兼容。
检测只读，现有明确用户“检查状态”触发网络请求，停止/超时/列表 revision 准入保持原契约。

独立服务 fixture 验证真实 ZIP、继承与 jar alias、实例优先 JAR、缺失/错误摘要、
重复/过大/损坏 JSON、重复 ZIP metadata、超过条目预算、文件链接、循环、取消，
并使用本地 TCP status fixture 验证相同/不同/缺失协议的实际读取。UI fixture 验证未知与
匹配/不匹配文案及同 SessionId 的 15 条 Operation/Stage 历史分为 12/3 两页，
旧无实例身份记录排除，跨实例晚到查询不污染当前页。验证由总集成协调者运行，
本文件描述 fixture 覆盖，不单独声明未运行测试通过或真实平台验收完成。

设置项实际消费者：`instance-settings.advanced.8b445a04eee7` →
`SettingsPageController.InstanceDiagnostics.Operations.BuildInstanceOperationHistory`，
实例诊断页“查看操作阶段”入口；`instance.servers.b485db8ebf9f` →
`InstanceServerStatusService.ReadAsync` / `InstanceOfflineReadinessService.ReadClientProtocolAsync` /
`SettingsPageController.Servers.ProtocolCompatibility`。

启动页导航清理：未识别的 `ui.navigation.*` 页面入口或未绑定的资源/设置/版本设置/
更衣橱入口通过共享 Warn 反馈拒绝，保持当前页面、返回历史和焦点，不创建替代页面。
导航 rail 的展开/选择机制不视为页面入口。版本设置与更衣橱初始是未绑定的 borrowed
page handle，生产 composition 必须显式绑定真实 controller.Page；绑定要求本树存活
的 Page，同一 handle 重复赋值不销毁实体，替换当前绑定时保留返回栈并切换真实页面。
LaunchPageController 不销毁外部 controller 所拥有的页面；旧迁移占位模板及资源引用
删除。接受测试显式绑定真实 Settings/Wardrobe controllers，并验证未绑定/未知拒绝、
重复/无效绑定和真实页面往返；不使用测试替代页面掩盖产品页内容。

选中内容完整性：新增 `minecraft.instance.content.integrity` typed 只读查询，限定
mods/resourcepacks/shaderpacks/screenshots/schematics 下的单个安全文件名；重验选中的
名称、大小、mtime、目录隔离与无链接边界，最多读取 512 MiB，单流实际计算 SHA-256
及用于现有来源关联的 SHA-512。实例元数据沿既有 512 KiB/schema1 契约读取，整个
捕获持只读恢复操作租约并在返回前重验来源；不写内容、基准或首次观察记录。
基准只来自现有应用内容更新事务的同 instance/game/page/file 已完成记录：committed
使用 after，rolled-back 使用 before。最多发现 1000 事务目录、每 JSON 1 MiB、总计
8 MiB；使用记录实际最后写入时间选择最近完成动作，冲突同时间、处理中/无效记录、
预算不足或无可用记录均明确未知。它只表示与已保存受管理更新摘要的比较，不宣称
作者签名、全量下载历史或首次读取得到可信基准。
详情中的来源仅从已完成的在线文件关联结果读取，并严格匹配当前 SHA-512、文件名
与捕获 scope；项目描述或版本名不能代替文件关联。固定未知内容准入说明沿现有
规则：保留未知来源本地文件，更新仍须来源/版本准入、文件摘要与依赖校验；没有
可编辑的安全绕过开关。typed result 与详情 scope 不符、取消或已退休结果不显示。

事实消费者：`instance-settings.security.ae662ab0ef1f`（SHA-256）、
`instance-settings.security.42f5355c7bce`（Modified Status）与
`instance-settings.security.19bb05d719d3`（Source facts）→
`InstanceContentIntegrityService.ReadAsync` / `SettingsPageController.ContentIntegrity`。
Unknown Content 固定准入说明也在此详情页，但 `b585c2e079b2` 的未来可变策略不被
此说明实现。Services fixture 验证实际双摘要、首次读不写基准、真实 update journal
提交/回滚基准、同大小/mtime 不缓存字节、跨实例记录、处理中/无效记录、超过预算、
过期选择、链接、遍历、取消和原件保留；UI fixture 经真实 service 验证摘要、无基准
未知、只有当前 SHA-512 的来源关联以及同名实例跨 scope 的晚到读取退休。

全局隐私页的 `BuildContentTrustStates` 提供只读发现入口：显示当前来源关联、已知
下载强制长度/摘要校验、受管理更新基准比较这三条固定规则，并给出主页版本设置到
内容详情的真实路径。此卡不启动全局扫描、不生成自动警告、不保存或更改策略，
来源关联明确不证明原始下载来源，无基准的修改状态明确未知。
