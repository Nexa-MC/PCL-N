# XSR-815 — 通用偏好与网络诊断入口

本切片延续 [XSR-803](XSR-803-system-preferences-completion.md) 的 Desktop/native 与
Services typed contract 边界。仅 PCL-N，不含独立插件执行器与 SDK。

`general.startup-page` 是八项导航偏好，默认 launch；只在首次有效已提交读取应用。
Main 必须让显式 CLI、URI、文件及已经收到的另一个实例激活优先。`general.launch-hints`
默认 true，通过 UI callback 改变 LaunchingHintBox 的真实可见性并标记 layout/paint dirt。
操作确认保留现有关键操作流程并显示状态说明，不加入绕过开关。`nexacl://` 状态来自
真实注册 adapter 的 Attempt/Pending/Success、时间与失败信息，不接受旧 `nexa://`。

`NetworkManualProbeQuery` 没有目标参数。Host 创建共享策略池上的非重定向 HttpClient，
`NetworkManualProbeService` 只向固定五个 HTTPS authority 发 HEAD，两个并发，每项 3 秒、
整轮 10 秒、单轮互斥、所有子任务被等待。请求带 diagnostic 标记以避免递归自动 probe，
不发送鉴权/正文/路径/查询，也不将 HTTP 401/403/404 当作接口健康。UI 显示观察时间，
导航与销毁取消；过期结果只属于那次观察，不表示当前状态。最近 Endpoint 表由有界
trace 的最后主机记录产生，并明确说明不是健康状态。

Download Diagnostics 读取现有 `download.transfers` 活动传输 collection，由下载协调器
真实 progress 发布、终止移除。contract-owned `DownloadStateContract.TransfersKey` 是
Services/UI 共用的静态键，旧 `DownloadService.TransfersKey` 保留兼容 alias。UI 最多
展示 16 个活动事实，展示本次观察时间、阶段、字节与速率，仅展示来源 authority host；
不展示本地路径或 URL query，空集合明确没有活动传输，不虚构下载历史或终止结果。

`network.resource-source` 默认 follow-request，保留资源页面选择；official-first 或
mirrors-first 覆盖资源 API 与实际文件源顺序，页面明确显示全局覆盖并禁用局部选项。
地域与来源禁用门控不变。`ResourceSourceResolution` 只记录 provider、时间、顺序、
候选主机及真正成功的主机，不记录 URL 路径、query、API Key 或正文。
资源来源 selector 的动态 track 与两个实际 option 在创建时进入同一命名表；全局覆盖
同时更新 option selection 和 track.Selected，恢复时显示保留的页面来源偏好。

`network.auto-install-dependencies` 默认 true。关闭时仍完整遍历并验证 required 图；
发现未安装的 required 依赖即拒绝整个计划，要求先手动安装，不省略验证、不下载主模组。
显式选择的 optional 仍是用户请求，其 required 依赖同样接受规则。preview 与实际安装
各自在开始捕获一次已提交策略，原有循环、版本冲突与文件完整性验证仍生效。

交付测试覆盖固定匿名请求、实际状态码、最大并发、单轮互斥与取消归还；required 关闭
时的嵌套验证、已禁用依赖拒绝与显式 optional；实际来源顺序和 host-only 解析事实。
Desktop 用例通过 typed mutation 与实际 HintBox 可见性验证启动提示，验证 manual probe
导航取消与迟到结果淘汰、HTTP 403/404 及活动下载观察、全局来源覆盖禁止局部合成 intent
并恢复页面偏好，以及媒体菜单只去重/清理自己四个命令。

所有新增测试由根整合任务注册并运行；本切片不执行构建，不将未执行用例写成通过。
