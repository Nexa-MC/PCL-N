# XSR-802 — 启动配置、临时覆盖和退出钩子

2026-10-08（Asia/Shanghai）。范围为 PCL-N。

## 契约锁

设置解析顺序为 Builtin → Global → Instance → 已选择的命名 Profile → Temporary。
Profile 仅允许实例可覆盖的值，以完整实例目录绑定；名称长度与数量有界。
命名配置与选择持久化到已有设置事务，Temporary 只在本次启动器生命周期有效，
结束后立即撤销。修改配置不改变已捕获的启动计划。Java 兼容检查保持强制；
尚无消费者的 `java.compatibility` 值写入（包括导入）明确拒绝。

环境变量使用有界 `KEY=VALUE` 行，直接传给进程环境，不作 shell/token 展开。
Classpath Head 使用完整路径行，保留声明顺序。Post-exit 命令仅在游戏实际退出后
运行，失败记录诊断，不逆转游戏退出结果；命令生命周期仍由已有 Hook port 管理。

Safe Launch 清除用户 JVM/game 参数、环境变量、classpath head、wrapper 和启动前/
退出后 Hooks，保留必要默认参数及当前账户的必要 authlib 注入。它暂时清空 mods、
resourcepacks、shaderpacks 和 config，不重新注入 Profile/Temporary 自定义来源。
CLI Safe Mode 的 typed session command 只影响本次启动器，不保存开关。

临时 mods/resourcepacks/shaderpacks/config 在游戏目录内使用独占租约及
拥有的恢复工作区。先记录 journal，再移动原目录，复制临时内容；失败、取消及
游戏退出后恢复原目录。只处理固定四个受控目录；不递归跟随符号链接，不覆盖
冲突恢复内容。进程仍存活时不得恢复其目录。启动器异常退出后，下一次同实例
启动在取得独占租约后执行 journal 恢复。用户源目录只读。
每个来源最多 4096 个条目、实际复制 2 GiB。正常退出必须等待拥有的游戏目录
恢复及 Post-exit（最多 2 分钟）完成；无临时文件或退出命令的游戏不改变原退出策略。
实例物理改名时配置与实例设置在同一事务移动，回滚恢复；活动临时覆盖必须先撤销。

所有普通游戏也持有共享目录租约；临时覆盖与恢复必须取得独占租约。Unix 使用
原生 advisory flock，Windows 使用文件共享约束。普通启动在进程创建前先保存有界
准备凭据，创建后原子保存实际 PID 与启动时间；启动器关闭不删除仍存活游戏的
凭据。独占启动必须检查这些持久凭据，避免启动器异常退出释放 FD 后误搬动普通
游戏正在使用的目录。仅确认同一 PID/启动时间已退出的运行凭据可自动清理；
中断于进程创建和凭据绑定之间的未知准备记录保留并拒绝独占变更，要求检查后
处理。普通游戏租约不列入启动器退出阻止条件。最多 64 条凭据，每条 4 KiB，
串行凭据锁等待最多 2 秒。
进程身份与存活检查走既有 `IPlatformProcessIdentity` 的 Running/Exited/Unknown 端口，
服务不查询系统进程；Unknown 或缺失启动时间不授予恢复权限。生产 composition
注入真实口，契约测试显式替换 Unknown/live/PID-reuse 观察。

恢复 baseline 保存捕获时的有效值；当前选中 Profile 时恢复写入该 Profile，未
选择时写入 Instance，从而保证有效值确实恢复。恢复 journal v2 逐项保存 layer
及 ProfileId，兼容已有仅 Instance 的 v1，并能只回滚原计划的层。活动 Temporary
必须先撤销才可创建或恢复 durable baseline，避免将短暂会话状态误写为持久配置。

UI 使用已封闭的语义 query/command，服务不引用 Desktop 或 renderer。设置页
展示配置名称、选择、撤销和临时配置操作；普通表单沿用同一值验证器。
配置操作仅在当前实例的有效值及配置快照都达到已提交修订、没有写入或配置读取
正在进行时可用。先处理当前帧已排队的按钮意图，再消费配置读取并重建按钮，
避免异步刷新清空已排队意图的绑定。刷新期间禁用配置操作；旧修订不得写入层。
退休按钮的意图无效。同实例、同配置与临时层的未保存目录和名称草稿以及焦点
在刷新后保留；切换层沿用清除旧层草稿的规则。

## 验证

验证涵盖持久配置重开、层级来源、临时撤销、强制兼容性写入拒绝、环境变量与
classpath 入参、正常退出/失败/取消的覆盖恢复及崩溃 journal 恢复。最终构建、
架构、NativeAOT 与裁剪证据见 [XSR-820](XSR-820-completion-closure.md)。
