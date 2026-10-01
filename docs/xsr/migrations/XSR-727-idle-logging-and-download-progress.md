# XSR-727 空闲日志与下载进度发布

## 契约

保持现有日志顺序、redaction、bounded ring/channel、镜像失败隔离、下载阶段、
failover、resume、取消与最终字节数。此单元只减少没有消息时的唤醒和每次读取
造成的中间 callback / XSR publication，不引入新的程序集或路由。

`LogService` 的 batch timer 初始停用；第一条新日志启动一次性 interval，flush
后停用。observer 重入写入保留下一 batch，手动 flush/clear/dispose 立即处理
已有待发布消息。默认无 batch interval 的构造仍逐条立即发布。

`FileLogSink` 仅等待 channel；最多256条一个 drain batch，队列排空或连续写入
超过200ms flush 窗口时 flush。空队列不创建周期 delay，不制造重复 WaitToRead
任务。DisposeAsync 排空最终消息；IO 失败的剩余 channel 引用释放。

单流每个来源立即发布首个 Downloading，中间每100ms最多一次，EOF 补齐真实
最终字节。状态切换仍立即发布，终态后仍移除 active transfer。分段下载保留
原有每 MiB 及最终反馈策略。clock 仅为内部测试 seam，不改变 public constructor。

## 验证

新回归以 fake clock 检查无消息跨8h不唤醒、batch合并、observer重入、手动flush、
clear和最终shutdown；这不是实际8h产品验收。文件回归检查懒打开、4096条有序
排空、重复dispose和IO失败。下载回归检查高速/持续传输的反馈数量、阶段、
最终文件长度与active state清理。Release构建零警告/错误，managed与Linux
NativeAOT Services均通过460项，68项目架构检查通过。真实进程CPU/内存及原生
8h运行仍需按运行期性能契约独立测量。
