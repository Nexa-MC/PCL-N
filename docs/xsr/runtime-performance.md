# 运行期性能契约

2026-10-01。取代 Alpha.6 / Beta 计划中以完整启动时间为中心的性能目标。

**Nexa 不追求最短的启动器启动时间，而追求在 Minecraft 整个生命周期中尽可能低的持续资源干扰。** 以下是目标与验收口径，不是已达标声明。共享 CI 的时间/内存数字不能替代受控硬件上的实际 Desktop 证据。

## 优先级与目标

| 层级 | 工作 | 目标与测量边界 |
| --- | --- | --- |
| Tier 1 / P0 | 静止空闲 | 无任务、无动画、无输入时 UI 不调度 frame；进程 CPU < 0.1–0.2%，allocation 接近 0 B/s，不做无必要网络 polling 或磁盘扫描。记录 CPU 时间增量及 logical processor 数，区分单核百分比与整机百分比；采样器开销单列。前台动态提示、动画、下载、游戏观察属于活动阶段，不能混入静止 idle 基线。 |
| Tier 1 / P0 | 内存 | 普通 idle working set 80–120 MiB，复杂页之后 < 150–180 MiB；大规模浏览允许暂升，eviction 后应下降。同步报告 private bytes、平台 commit、GC live/committed heap、native 与 GPU texture；不支持的指标为 null，不能用 working set 差额估算 native memory。 |
| Tier 1 / P0 | 长期稳定性 | 完整原生 Desktop 8h idle 与 8h Minecraft running，观察 working set、private bytes、GC heap、handles、threads、UI entities、Sidecar sessions、图片预算、HTTP 连接、task history、XSR state、日志、watcher 与 recovery blob。预热后按时间窗比较趋势与峰值，不能只用最终 GC 或首尾两个点证明无持续增长。110→118 MiB 可以是平台噪声；持续110→350 MiB必须调查。 |
| Tier 1 / P0 | 常规交互帧 | 受控 120Hz：P50 < 2ms（争取1.5），P95 < 3ms，P99 < 6ms（争取5）；低端60Hz P99 < 16.6ms。另报 P99.9、最慢1%与0.1%的平均耗时、max、样本数和 allocation；包含输入排队→反馈延迟。hover、button、segmented、pager、bubble、scroll、navigation、settings、列表逐项测量。kernel、composition 与完整 OS/GPU frame 分开。 |
| Tier 1 / P0 | Minecraft 启动让路 | Launch Quiet Mode 暂停/推迟可选预取、图标 decode、后台索引、更新查询、snapshot GC、低优先级 hash、非关键 telemetry/Sidecar 工作及动画；文件补全、Java、Jvm.Host 有优先 admission。必须在窗口确认且稳定观察完成后恢复；失败、取消、退出和并行会话也须释放。不得暂停验签、账户初始化、事务恢复、必要观察或安全日志。 |
| Tier 1 / P1 | 大数据 UI | 10k versions、5k mods、20k screenshots、数万搜索结果的实体与工作量主要随当前可见数据变化；列表自身 active entities < 50–100。全 shell entity 数须单独报告；10k全部实例化节点只作压力诊断，不能算虚拟列表 SLA。 |
| Tier 2 / P1 | 图片 | 初始目标：encoded32 MiB、decoded CPU64 MiB、GPU64–128 MiB；LRU + visibility + memory pressure，区分缓存字节与活跃可见对象。evict必须释放实际 bitmap/texture，不能只移除索引。 |
| Tier 2 / P1 | IO / concurrency | Critical、Interactive、Background、Idle 四级共享 admission，分别约束CPU、disk、HTTP并发。任务可取消、有界等待、防饿死；Quiet Mode只限制低优先级。连接复用与取消边界必须保留，不通过统一 Task.Run 制造调度。 |
| Tier 2 / P1 | 热路径 | hover、animation tick、scroll争取零分配；progress极低分配。测线程局部与进程总 allocation，审查LINQ、closure、iterator、插值、临时集合。不得以省分配为由共享可变跨线程状态。 |
| Tier 2 / P1 | Threads / handles | 长期静止 idle进程 <20 threads，争取10–15；child Sidecar/JVM单列。共享scheduler、ThreadPool与async IO；控制watcher/Timer生命周期，避免每个Service一个常驻线程。 |
| Tier 3 | 完整启动 | 取消Cold <700ms / Warm <300ms目标。Splash→可交互只设受控基线的regression上限：<2s很好、2–4s可接受、4–6s调查、>6–8s明显体验问题；这是调查分档，不是共享CI一刀切门槛。 |

## Splash

Time To Splash从用户启动进程到首次实际呈现测量，争取100–200ms以内；不能用窗口构造/Show调用完成代替首次显示。最小Shell必须能重绘、处理DPI、显示错误并安全退出，慢初始化不得阻塞消息循环。真实阶段可以是设置、事务恢复、账户、实例、界面；只有可计量的真实工作量才给百分比，其余使用indeterminate。

当前 `AvaloniaSplashWindow` 是装饰图标，`AvaloniaUiShellLifetime.Compose` 接收已构造的shell且先构造主窗口再Show splash；不等同于上述早期可用Shell。现有2秒fallback是装饰关闭兜底，不是启动SLA。早期Splash、阶段/错误状态与首次呈现instrumentation待交付，不能因已有Splash类而宣称Time To Splash已达标。

## 日志与下载发布契约

资源图标 encoded cache 使用32 MiB实际字节预算与256条目上限，命中更新LRU，
入缓存只淘汰满足预算所需的最旧条目。正在显示的图片由页面持有，不计作可被cache
强制释放的对象；缓存预算不等于整个进程图片预算。取消须覆盖缓存命中，dispose后
迟到的HTTP/decode结果不能重新填充cache；四条pipeline和Quiet Mode admission保留。

FitToBounds图片按可见DIP尺寸和当前RenderScaling选择向上取整的二次幂解码宽度，
最多1024像素、不得超过原图；同一bucket复用bitmap，DPI改变在绘制时重新核对。
皮肤layer保持原始像素坐标和nearest-neighbor语义。backend读取已有encoded数组，
不复制整张图片；更换/退休control必须释放其bitmap lease。

backend的动态raster由进程共享pool持有，按encoded身份、fit模式和解码宽度去重。
所有活跃lease与闲置条目共用64 MiB的pixel charge预算、512条目上限；charge为
每像素8字节，在decode前按目标尺寸保守预留，decode后按实际尺寸核对并计账。
该charge是明确的预算单位，不是allocator/native/GPU测量。闲置条目按LRU淘汰并
dispose；活跃lease不可被淘汰，额度全部被占用时新图片使用原有占位而不突破预算。
完全在clip/viewport之外、零尺寸或opacity为0的节点不持有lease；活跃与outgoing
control共享同一pool，更换、退休及surface关闭释放lease，关闭还清理所有闲置项。
原生窗口最小化时暂停该surface的raster驻留、释放lease并trim闲置条目；其他可见
窗口的lease仍受保护。最小化期间的scene更新或迟到draw不得重新decode，恢复显示
后由正常绘制重新获取当前图片，不暂停必要的state/launch/recovery观察。
预算拒绝不产生timer/frame；后续正常绘制或场景提交在容量变化后可重试。畸形图片
不会逐帧重试。固定内嵌avatar/version bitmap、临时decoder内存、compositor持有的
引用与GPU texture不在该动态pool计账内；OS memory-pressure adapter和实机曲线
仍须单独验收，不能把64 MiB pixel charge宣布为实际CPU/GPU或进程RAM上限。

日志 batch publication 采用一次性唤醒：无待发布条目时 timer 必须停用，第一条
新消息才启动一个 publication interval。连续写入合并为同一 batch；显式 flush、
clear 和 dispose 保留立即可见/最终排空语义，observer 重入写入不得丢失下一 batch。
文件 sink 在 channel 空闲时等待新数据，禁止空队列的周期 flush；队列排空或连续
写入跨过 flush 窗口才 flush，异步 shutdown 必须排空并保留顺序与 dropped summary。

单流下载在每个来源尝试中立即发布首个 Downloading 反馈，中间进度最多每100ms
一次，EOF 时补齐最后真实字节数。Connecting、Reading、Retrying、Committing、
Completed、Failed 不能被节流合并；不得丢弃终态、改变实际 IO/取消、取消 hash 或
改写 failover 的来源/offset 语义。节流只减少 callback 和 XSR state publication。
静止日志的 fake-clock 回归和下载 fake-clock/实际字节回归不替代进程 idle SLA。

## 当前工具及证据局限

有序唯一collection base只排序改动项并归并，非有序/重复键base保持完整规范化；
键身份默认equality与排序comparer分离，稳定顺序、删除优先、失败原子性保留。
未变化的同revision read复用immutable snapshot/wrapper，发布后释放node旧缓存；
availability改变也产生新view。该路径减少排序和重复read分配，publication仍有O(N)
复制/校验，不等于O(visible) UI或全进程零分配，见 [XSR-732](migrations/XSR-732-ordered-collection-deltas.md)。

文件校验 receipt 只属于单个 service 生命周期，容量8192；成功命中更新最近使用顺序，
超预算只淘汰最旧条目，不能整表清空造成下一次启动集中重读。路径身份遵循平台规则，
大小/mtime/期望 hash 任一改变使条目失效；显式 forceHash 始终重读，失败须撤销旧 receipt。
receipt 不持久化，不是同账户攻击隔离，也不能替代发布验签或显式完整性验证。

- `Nexa.UI.Next.Benchmarks --output FILE [--timing-gate 120hz|60hz]` 只测renderer kernel。常规1600节点场景使用上述门槛，10k materialized场景只报压力数据。JSON包含tail sample counts；1000样本的最慢0.1%只有1个，不能当作稳健的正式认证。受控最终验收应使用长窗口与独立重复运行。
- `Nexa.Desktop.Tests --soak idle|navigation --seconds 1..28800 --output NEW-DIR` 是composition fixture。idle按tree/state invalidation合并触发render，不再主动60Hz轮询；navigation主动产生负载。schema-3区分tree/state请求，并保留测量窗口内最多64对semantic ID/reason的发布次数与首末时间，无payload；warmup/最终GC后的事件不混入计数，不为取得零帧结果插入flush/wait。每秒采样记录CPU、private bytes、GC committed、collection counts等；CPU同时给单核占用和按Environment.ProcessorCount归一化的数字，后者受quota/affinity/DOTNET_PROCESSOR_COUNT影响，不能直接等同于整机任务管理器百分比；采样、JSON与Process查询的allocation属于harness，不能把进程总allocation称为产品静止路径allocation。定期hint等真实composition事件仍会计入帧。
- 8h可在本地运行；现有手动workflow设置150分钟timeout并保持2h fixture选项，不能直接承担8h任务。不把fixture导航或8h本地composition等同于原生Desktop/Minecraft运行验收。
- OS commit、native/GPU、Sidecar/HTTP/cache/IO等未测指标明确列出；不存在指标时不得填0。
- Foundation host新增共享CPU/disk/HTTP admission与Launch Quiet Mode；首批接入下载连接/分段、目录预取、图标获取与验证、更新/rollout查询和遥测上传，详见 [work-scheduling.md](work-scheduling.md)。失败/取消释放，确认窗口后15秒 grace 或提前终止释放；不支持窗口检测的fallback不称作稳定确认。XSR-733接入自动快照初始枚举、80 KiB来源处理与32项Idle blob回收；后台索引、Sidecar非关键工作及其余恢复IO尚待adapter，全局图片CPU/GPU预算、真实8h与OS/GPU测量仍待验收。签名、初始化、事务恢复与取消契约保持。
- 主页提示改为仅当前提示卡片、活跃且未最小化窗口、非启动/quiet时唤醒；其他页面和卡片不再三秒更新。temporary motion suspension独立于用户Reduced Motion。生产组合不再启动已禁用schema-1在线模型的每小时HTTP refresh session。

## 本轮验证

Release构建零警告/错误，104项Desktop测试及68项目架构检查通过；managed与Linux NativeAOT benchmark的确定性门禁、schema-2尾部字段检查通过。idle/navigation各运行60s，仅证明fixture入口及有界保留检查；1s无事件窗口记录0frames与null帧percentile，60s主页仍记录40frames（包括提示定时器触发）。这不是产品已实现静止idle的证明。

当前非受控、并行开发环境的1600节点kernel paint/layout P95分别约3.6/4.5ms（managed）、3.7/4.9ms（NativeAOT），部分场景高于新目标；未启用受控时间门禁，不能将确定性PASS解释为性能达标。OS/GPU完整帧、真实8h、静止进程外采样、Quiet Mode与缓存/IO预算均未验收。

后续admission批次：452 Services /105 Desktop通过；默认主页60秒fixture记录0frames与0render requests，所有采样admission/quiet计数为0（285entities、210cells）。这关闭提示定时唤醒回归，仍不证明原生idle CPU/RAM SLA。

日志/进度批次：managed与Linux NativeAOT Services460项及68项目架构检查通过。一次性日志publication、
空channel等待和100ms单流进度回归已通过；后续图片/collection单元缩小了保留与排序成本，
全局图片CPU/GPU预算、collection发布剩余成本及真实原生idle CPU/RAM与8h趋势继续验收。
fake clock跨8h只验证调度逻辑，不是8h运行数据。

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
16项Python回归及三份既有60秒实际fixture的分析通过；两小时fixture仍待跑满。
工具输出保留原有endpoint gate，并明确runtime KPI/physical acceptance均未认证。
