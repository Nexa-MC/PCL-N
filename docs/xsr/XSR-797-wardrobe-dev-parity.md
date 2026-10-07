# XSR-797 — 更衣橱与 dev 对等重写

行为基准：只读 `origin/dev` 提交
`4755faee0a10686917213afe57cb772ade83d77f`。之前的 XSR-793 是在线上传和披风操作
表单，不是完整的 dev 外观页；本切片按 dev 的页面结构、资料来源和操作规则重写。
架构边界在 [XSR-793](XSR-793-account-wardrobe.md#dev-parity-rewrite-contract) 先行锁定。

## 对照清单

| dev 来源 | 迁移行为 |
|---|---|
| `PageSkinAppearanceRight.axaml`、对应 code-behind | 当前角色个人栏、用户名和账户类型、本地皮肤与皮肤库入口；独立皮肤/披风横向卡片轨道，名称、来源、数量、空状态、活动项与应用按钮；宽度和低高度响应布局 |
| `MinecraftPlayerPreview.cs` | 全身皮肤、经典/纤细模型、旧式 64×32、现代外层、披风，以及视角切换；仅接收不可变 PNG 和绘制配方 |
| `MainWindow.Appearance.cs`、`MinecraftProfileTextureResolver.cs` | 当前 SKIN/CAPE/slim、其他档案快照、LittleSkin 皮肤衣柜、活动披风预览；Microsoft 披风读取中/失败/空列表分别展示，失败保留可用外观和历史 |
| `SkinAppearanceHistoryStore.cs` | `Appearance/history.json` 地址历史兼容，最多 80 项，材质类型和地址去重，按时间排列，原子替换；凭据不入历史，存储失败不阻断已完成的提供方操作 |
| `MainWindow.Appearance.cs` 的本地/历史/公开材质操作 | Microsoft 下载后上传与已获得披风；LittleSkin 独立 OAuth、公开材质入衣柜、应用并回读验证；第三方认证站/材质详情跳转；离线只读 |
| `PageSkinLibraryRight.axaml`、`SkinSiteCatalog.cs` | LittleSkin 站点栏、关键词、皮肤/披风、时间/点赞、分页、版本与数量、载入/失败/空状态、预览、详情、文档、站点与显式刷新 |
| `SkinSiteInteractionPolicy.cs` | Microsoft 和 NCloud 公开披风不提供应用操作；浏览与详情仍可用 |

LittleSkin 认证请求在 401/403 后最多强制刷新一次，并先保存轮换凭据；公开目录与图片
失败不触发 OAuth。页面在同账户操作中接纳已发布的合法更名，仍拒绝账户切换、
删除重加和迟到结果。多来源预览保留成功的图片租约，容量不足时只重试缺失来源，
两个受限控件的空闲帧不会相互触发重复申请。

## 提供方与验收边界

NCloud 的 dev 操作依赖独立账户提供方。PCL-N 提供可注入的上传/站点引用端口，
保留两种语义；外部提供方缺失时明确不可用，不能冒充已完成 NCloud 线上换肤。
本切片不修改独立插件仓库，也不复用第三方游戏令牌作为皮肤站 OAuth。

dev 新版更衣橱的离线档案只读；旧登录菜单的保存皮肤、改名、改密码及旧式离线换肤
不属于这两个页面的对等范围。现有请求预算、PNG 验证、账户/凭证代次 admission、
取消和迟到回复退休继续约束所有新操作。

真实账户上传、站点角色的最终外观和 NCloud 外部能力需要提供方环境验收。自动化
契约测试不会被描述为真实线上账户验收或 Alpha.6 已接受。

## 执行证据

验证环境为 2026-10-07 的 Debian 13、Linux x64、.NET SDK 10.0.100；桌面测试使用
Xvfb。构建版本为 `2.0.0.ci.b1bdd8`，对应本切片开始前的提交标识。

| 检查 | 结果 |
|---|---|
| 完整解决方案 Release 构建 | 0 警告、0 错误 |
| Services | CoreCLR 和 NativeAOT 各 627 项通过 |
| Desktop | CoreCLR 和 NativeAOT 各 196 项通过 |
| UI.Next 渲染器 | 98 项通过 |
| Avalonia 场景后端 | 15 项通过 |
| PXML 编译器 | 40 项通过 |
| XSR 架构约束 | 70 个项目通过 |
| UI.Next 性能门禁 | 通过；521 个实体的干净重绘分配 0 字节 |
| 格式检查 | `IDE0055`、`IDE0005` 的只读检查通过 |
| 实际 Desktop NativeAOT 发布 | 原生编译通过；外壳验证 52 个语义节点，首次设置验证退出码 0 |
| 实际 Desktop 裁剪发布 | `TrimMode=link` 通过；外壳验证 52 个语义节点，首次设置验证退出码 0 |

回归用例覆盖当前材质和全身七视角、经典/纤细/旧式皮肤、披风遮挡、低高度布局、
完整横向库存、公开目录筛选和分页、匿名预览、上传与应用、历史兼容和存储失败、
OAuth 单次重试及轮换持久化、同账户更名、账户切换和迟到结果退休。公开应用完成后
无额外输入即可重新读取账户并恢复操作；两个容量受限的预览控件连续 100 个空闲帧
不新增分配、解码或池修订。设置焦点回归检查异步重建后的同名活动控件、选中状态及
焦点提示，避免将旧实体标识作为固定承诺。

两种发布分别恢复各自的资产；NativeAOT 日志确认 `Generating native code`，产物为
原生 ELF 且不含 `Nexa.Desktop.dll`，裁剪日志确认 `Optimizing assemblies for size`。
发布日志没有 AOT 或裁剪警告。使用 `--validate-shell`、`--validate-setup` 分别验证
两种实际应用产物，而非只验证测试程序。上述结果不扩大本页列出的提供方验收边界。
