# XSR-803 — 系统偏好与网络来源策略

2026-10-08（Asia/Shanghai）。PCL-N 内的系统集成继续采用 Desktop intent → 原生 adapter；
Services 不引用 Avalonia，renderer 不读取剪贴板或注册操作系统资源。

## 本次关闭的边界

- 开机启动只写当前用户入口；关闭仅删除由 NexaCL 创建的入口，不申请管理员权限。
  关闭时入口或父目录不存在均为幂等成功，不创建目录；访问拒绝仍报告失败。
  Windows Jump List 使用当前 NexaCL AppID 的四项固定导航任务，关闭删除本应用的列表，
  不接管用户对其他应用的选择；标签在 UI dispatcher 拍取不可变的当前语言快照。
- 文件关联只声明 NexaCL 可处理的 `.mrpack` 与 `.nexapack`，不接管普通 ZIP/JAR；
  Shell 收到本地文件后仍由现有导入命令校验，不将路径执行为命令。
- 原生通知是现有反馈事件的可选投影，失败不影响内部通知，不创建重复反馈环。
  Linux `notify-send --action` 的有界响应经 UI dispatcher 回到明确导航 intent；
  macOS `osascript` 只用参数传正文，Windows 使用源生成 `Shell_NotifyIconW`。
- 剪贴板检测只在窗口获得焦点时读取一次、仅准入 `nexacl://` 导航链接，
  要求显式确认，不执行剪贴板中的 shell/文件路径。禁用后不读取剪贴板。
- 网络来源启停在 HTTP 传输的请求准入处生效，并保留用户手动开启的能力；
  有界 trace 只记录主机、状态、时延与错误类型，不记录查询、正文或代理凭据。
- 原生窗口透明度与模糊仅在平台声明支持时应用；图片背景与 Logo 采用有界本地
  图片输入，不将未经消费者实现的媒体设置标记为可用。
- 用户主动的“打开启动器配置文件”通过 `OpenLocalFile(string path)` 使用真实 OS
  文件关联：Windows ShellExecute 直接传文件名，macOS `/usr/bin/open`、Linux `xdg-open`
  只收一个独立 argv；不传 shell 命令文本，不自动启动编辑器。必须已有 native owner，
  且输入是无控制符的绝对路径、存在的 regular file；拒绝目录、链接和 device/FIFO。
  Unix 类型检查使用有限 `statx`（Linux）/`lstat`（macOS）ABI，测试 capture ProcessStartInfo
  而不真正打开云端编辑器。composition 只将当前 launcher-owned settings.json 交给它。

## 媒体与主题

- 自定义主题只包含 `background`、`foreground`、`accent` 三个 `#RRGGBB` 颜色；
  输入最多 4096 字符，renderer 只读取不可变调色板。
- Logo/静态背景只读取本地 PNG 的绝对路径，16 MiB、4096×4096 上限，异步读取，
  native raster pool 负责解码预算。背景自身透明度独立于子页面的可读性。
- 背景适配使用 `appearance.background-fit` 的 `cover`（默认）、`contain`、`stretch`；
  cover 居中裁切并严格限制在背景载体内，contain 保持比例并显露底色，stretch 填满载体。
  renderer 的新增 immutable `XsrUiImageFitMode` 契约只在完整图片模式生效，既有图片
  recipe 默认 Contain，保留账号/皮肤/截图的既有适配。解码预算仍有原来的上限。
- 背景底色使用 `appearance.background-color`，仅 `auto`（默认、现有主题底色）或
  `#RRGGBB`；不接收颜色名称、alpha、脚本或任意样式。静态 PNG 与视频帧共同读取
  `appearance.background-opacity`（0–100，默认 100），只改变图片自身透明度，
  底色和子页面保持独立。策略变更可更新正在播放的帧，不以重启解码器实现外观修改。
- `appearance.reduced-motion` 是独立的已提交布尔偏好（默认 false），不复用旧设置 port；
  Desktop 通过 typed effective query 读取，与原 `appearance.animations-disabled` 做 OR，
  在 UI frame 边界设置真正的 `Renderer.ReducedMotion` 并使缓存呈现失效。该偏好或旧动画
  禁用开关开启时，视频背景暂停/停止出帧，关闭后从原定位恢复；不因此停止背景音乐。
- 本地音频通过自有 `ffplay` 进程播放，具有真实退出码、暂停/继续（按时间定位重启）、
  静音和音量；目录播放列表最多 128 首，支持顺序/随机和自然结束自动播放。
- 视频背景通过自有 `ffmpeg` 进程产生 PNG 帧，6 fps、960×540、每帧 4 MiB 上限，
  只排队一个最新 UI 帧。失焦与游戏 quiet 可暂停，恢复时继续定位，结束或销毁杀死
  自有进程。缺少引擎明确反馈 DependencyMissing，不展示假播放状态。
- Linux MPRIS 通过源生成 libdbus-1 ABI 导出真实媒体服务，支持 Get/GetAll、音量 Set、
  Play/Pause/PlayPause/Next/Stop 与 Raise；PlaybackStatus 读取实际 player 状态。
  不支持 Seek/Previous/OpenUri 的 capability 明确为 false/空数组。
  Windows 使用 WinRT `ISystemMediaTransportControlsInterop.GetForWindow` 的明确 ABI，
  注册 typed event handler 并管理 IUnknown 引用计数，不要求 Store 身份、不使用 RCW。
  macOS 使用 libobjc 的 MPRemoteCommandCenter target/selector 和 MPNowPlayingInfoCenter。
  状态/按钮 enabled 来自实际播放器与已提交策略；依赖缺失给出明确反馈。
  Windows 通知点击使用自有 Window subclass 回调，macOS 使用自有通知 delegate lease，
  native UI cleanup 在 Closed 同步解绑，避免等待已退出的 dispatcher。三平台物理播放、
  系统面板与通知服务尚须分别验收，Linux 总线契约不替代其他系统实机证据。

## 网络策略

来源门控仅匹配官方游戏内容、Modrinth、CurseForge 与已知镜像的精确域名，不禁用
Microsoft 登录、Minecraft services 或启动器更新。策略在每个新 HTTP 请求生效。
Trace 最多 256 条，只存主机、请求/诊断 probe 种类、状态、耗时和错误类型。
自动故障 probe 每 30 秒最多一个、3 秒预算、无鉴权/查询/正文，不递归触发。
后台下载关闭时拒绝新的 Background/Idle transfer，用户 Interactive 下载保持可用。

## 最终目录入口闭环

- 启动页偏好只有 launch/install/resources/settings/java/storage/about/tasks 八个导航目标，
  首次已提交读取时应用一次；composition 对显式 CLI、URI、文件激活优先，不重写外部请求。
  启动提示独立布尔偏好只控制游戏启动页的提示区，不隐藏真实进度、错误或确认。
- 操作确认是现有关键操作的强制流程状态，不提供绕过开关。协议目录使用正确的
  `nexacl://` 名称，显示真实注册尝试、结果和观察时间，不把目录文字当注册结果。
- 音乐菜单安装先去重自己的四个 command，销毁只移除自己拥有的项，保留导航菜单。
- 手动网络 probe 使用固定五个 HTTPS authority、HEAD、无请求正文/鉴权/查询，
  最多两个并发、每项 3 秒、整轮 10 秒；导航取消，不接受用户指定 endpoint。
  Endpoint 状态读取最近已观察的请求结果并标注时间，不将旧请求称为实时健康状态。
- 资源站来源优先级只接受 follow-request（默认、保留页面选择）、official-first、
  mirrors-first；实际 provider/下载适配器读取已提交策略。地域与 provider 禁用限制仍生效。
  自动安装依赖默认开启；关闭时缺失的必需依赖会明确拒绝整个计划，要求先手动安装，
  不删除必需依赖检查、不下载不完整主模组。用户显式选择的可选依赖仍属于用户请求。

## 验证

集成契约、原生 adapter、架构及 AOT/trim 证据见 [XSR-820](XSR-820-completion-closure.md)。不同操作系统的
真实开机、通知服务及文件管理器交接仍须分别取得物理平台证据。
新增验证覆盖参数保持、可逆自有 autostart、主题/图片输入限额、provider 禁用与敏感
信息排除、trace 环上限、真实 ffmpeg 帧和暂停退出。MPRIS 的 Get/GetAll/PlayPause/
Volume 原生验证须在 `dbus-run-session` 中运行，普通无总线环境不能冒充通过。
ffmpeg/MPRIS fixture 已通过；整合时发现的默认原生透明提示覆盖回归也已修复。
最终完整后端套件 25 项通过，包含窗口透明提示和真实媒体验证。
系统策略的独立有界队列用 typed query 读最新已提交快照，不受等待通知点击的队列阻塞；
新增隔离 XDG 的真实设置变更→状态 revision→query route→注册/取消测试。

背景后续契约验证覆盖 typed effective query 的已提交适配/颜色/透明度、已缓存 scene 的
纯底色更新失效、视频优先与恢复最新静态图。原生 raster 像素验证覆盖三种适配、底色
留边、图片 opacity 与有界 decode lease 复用；这些原生验证已纳入通过的完整后端套件。

## 外观目录逐行核对（本切片后的边界）

主题、背景、窗口、动画、音乐五个 Group 行是信息架构容器，不代表待实现功能。
本地主题、Logo、PNG/视频/音乐、音乐控制、窗口透明度/模糊布尔开关有实际消费者。
“减少动态效果”已独立连接到 renderer 及视频消费者；四项 renderer/layout/animation/
performance 诊断行现有独立 `SettingsPageController.RuntimeDiagnostics` 捕获动作与目录
consumer 映射，读取真实 renderer/native 计数。物理呈现帧率与缺失 GPU 驻留计数仍明确
不可用，现有 Advanced 的 renderer/state 诊断卡不是这四项的消费者。
“模糊强度”“模糊采样”没有对应 Avalonia 便携原生窗口参数；本实现只承诺系统 compositor
支持的模糊模式提示，不将这两项结构占位或渲染器未消费的 BlurRadius 虚报为完成。
