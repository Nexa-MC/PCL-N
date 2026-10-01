# 用户主动诊断导出

Alpha.6。诊断 ZIP 由 Common 模块写入，Desktop composition 只收集已发布事实并调用明确的导出动作；UI controller 只接收异步效果委托。无需新增程序集，不上传、不执行附件、不扫描任意个人文件。

固定条目：`summary.json`、`mods.json`、`logs.json`。版本、OS/arch、Java major、MC/loader、preflight issue code、crash classification、完整性/失败任务统计都是 allowlist。未知值明确为 null。没有账户 DTO、settings 全表、process command line、env 全表、文件绝对路径和 raw crash 内容。

日志只导出时间、等级、模块和受控操作结构：operation/stage/outcome；原始 message、exception 和 context 不进入 ZIP。这样无论 token/用户名/个人路径采用何种格式，都不能借未识别的自由文本漏出。此版本牺牲详细堆栈换取严格隐私边界；开发者可从稳定错误分类与操作序列继续定位。Mod ID/version 来自 inventory；不输出文件名或来源路径。压缩前内容总量和最终 ZIP 均限制为 2 MiB。

集合有界，字段限制字符/长度，ZIP 先在目标目录随机临时文件完成，取消/失败删除临时文件；发布使用拒绝覆盖的 rename。不覆盖已有文件，不跟随既有临时文件。原生平台目录选择器仅由用户点击触发；退出时取消未完成操作。测试必须包含无标签凭据/账户/路径、恶意字段、取消、同名保护和包体预算。NativeAOT 使用 Utf8JsonWriter，不依赖反射序列化。

这完成的是安全基础诊断包；更细的实例完整性验证与持久 preflight/crash 历史仍需后续收口，不能把未知统计显示为“已验证完整”。
