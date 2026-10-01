# Instance Content Graph

Alpha.6 的实例“内容依赖”页是本地 metadata 的只读投影。它复用当前实例管理读取的
LaunchModInventory；不重复扫描 JAR、不请求网络、不引入新的程序集或 Service route。
仅用户打开此页时，通过既有 management query 的 IncludeContentGraph 构建图。

节点是已识别的 Mod identity，不是文件名猜测，也不等价于 JVM 实际 loaded set。
依赖保留声明的版本范围，提供者包含自身 ID 和 provided aliases。状态分别为本地存在、
停用、缺失、未知、多提供者歧义、运行时提供和未验证的内嵌候选。只有完整 inventory
中找不到提供者时才标记缺失；任何依赖/别名声明不完整时，未找到的提供者仍保留为未知。
嵌套候选不能算已加载。运行时提供仅表明 Minecraft、Java 或已选 Loader 的来源，
不能算版本范围验证。未知依赖 metadata 始终明确标注。
当前 inventory 未检查的旧式 .litemod 文件按未知计数，不能作为完整识别结果。

图最多 4096 节点、16384 条声明边、每条边 16 个提供者。超限输出部分图及提示，不得
把截断变成缺失。循环通过迭代强连通分量分析，只在唯一已启用的非内嵌提供者间建立
分析边；循环提示不进入 Preflight Block，也不声称所有 Loader 都拒绝此 metadata。
页面搜索及双向关系均分页，最多显示 12 项；导航、刷新和实例切换重置旧选择。
双向索引由 Service 在后台构建，UI 不在首帧重建整个图的反向关系。

此单元不实现批量更新、版本范围求解或自动修改依赖；删除继续使用现有 hash/stamp
绑定、事务内复验的移除计划。图不改变授权、启动或恢复策略。

## 更新检查生命周期

实例更新检查复用进程拥有的、带 PooledConnectionLifetime 的 HTTP client；请求和响应
仍各自释放，取消仍是每次调用独立的 token。旧结果在新的检查开始前清空。站点响应
返回后，文件大小、mtime、链接状态以及 SHA-512 必须再次符合本次识别的内容。
响应中的项目、游戏、Loader、发布时间和资产 hash 继续独立校验；外部文件变化不能
发布可更新结论。该校验仍不宣称抵御同账户恶意进程反复换链。
