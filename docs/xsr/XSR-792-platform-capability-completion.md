# XSR-792 平台能力检测补充

本轮补齐已有能力定义的检测路径，不新增 Desktop、Avalonia 或 Services 对平台实现的反向依赖。
检测只读，不申请权限、不打开输入设备、不设置显示模式或电源模式。

## 显示

Linux X11 使用 `xrandr --query` 的活动输出、显式 primary、当前模式和当前刷新率。
命令直接启动，固定参数，限制执行时间和标准输出/错误大小；取消终止本次启动的进程。
只有一个活动输出时可视为主屏；多个活动输出未指定 primary 时，主屏属性为 Unknown。
内建屏类型从与主输出匹配的 DRM sysfs 连接器类型判断，不把 framebuffer 虚拟尺寸作为主屏分辨率。
Wayland 无通用非图形客户端的完整主屏查询接口，报告 PlatformUnsupported；
无图形会话或缺少 xrandr 报告 DependencyMissing；失败、截断、权限不足分别保留具体可用性。
macOS 显示 ID 使用 CoreGraphics 的 32 位 CGDirectDisplayID，计数和分辨率范围有界，复制的显示模式释放。

## 电源和温度

macOS 使用公开 IOPowerSources 的电源来源、内部电池存在、容量比例与充电状态。
CF 所有权遵循 Copy/Create 释放，最多枚举固定数量电源，外接 UPS 不当作内部电池。
电源模式只接受 NSProcessInfo 公开的 lowPowerModeEnabled；未启用低电量模式不推断为高性能。
Linux 电池与外接电源读取 `/sys/class/power_supply`，限制条目和文本大小；
多电池不能从任意第一块电池伪造系统电量，按同类 energy 容量加权；
只有一块电池时允许使用内核公开的 capacity 百分比。scope 为 Device 的外围设备电池忽略。
macOS 没有公开免驱动 CPU 温度 API；私有 AppleSMC 协议不属于此闭环，报告 PlatformUnsupported。
Windows 温度继续保留其公开 API 未提供该事实的 Unknown 结果。

## 设备形态

FormFactor 默认检测复用电源、显示和输入 provider 的真实事实，不再假定 BAT0、js 节点、
Windows 专属内建屏接口或 macOS 的默认 false。Linux 显示查询保持异步、可取消和有界。
形态启发式保持既有 Battery + InternalPanel → Laptop、Touch + NoKeyboard + Controller → Handheld
合同，但输入缺失/失败保留 Unknown；只有已观察事实能排除全部便携形态时才产生 Desktop。
无电池、无内部屏等已知否定证据可短路否定结论；未知键盘不当作“没有键盘”。
Windows 电池事实统一使用 kernel32 的 GetSystemPowerStatus，未知 BatteryFlag 不当作有电池。
Windows 主屏 DisplayConfig 查询失败、OTHER 和未识别连接类型保留 Unknown；
INTERNAL、LVDS、嵌入式 DisplayPort/UDI 判断为内建，普通外接连接类型判断为外接。
GetMonitorInfo 失败不将枚举中任意第一块屏的尺寸替代主屏。
DisplayConfig ABI 使用 Windows SDK 的 LUID 四字节对齐：Header 20、Path 72、
Mode 64、SourceName 84、TargetName 420 字节；查询设备信息传完整封套而非只传 Header。
这些布局由公开 [Windows SDK wingdi.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h)
声明和确定性合同测试锁定，实际 Win32 调用仍需 Windows 真机验证。

## GPU 合同

`gpu.memory.dedicated.budget/current_usage/available_budget` 保留既有 DXGI 当前进程预算语义。
AMD sysfs / NVML 的整卡静态容量、全系统占用和 Metal 的推荐工作集/当前 Metal 分配
不能替代该合同。Linux/macOS 在没有等价公开通道时返回完整三项 PlatformUnsupported，
而不是伪造预算或声称未实现的 Metal 适配器已经接入。

## 验证边界

合同测试覆盖 XRandR 多屏、未指定主屏、当前模式刷新率、畸形/截断输出和设备状态投影。
macOS IOPS 数值投影可在任何平台测试；CoreGraphics、IOPS 和 NSProcessInfo 的实际调用
仍需 macOS 真机验证。Linux 无头云环境只证明失败路径与确定性投影，不证明真实显示器验收。
NativeAOT/trim 检查由本轮统一运行，不以本说明代替运行结果。

## Executed integration evidence

2026-10-05: Release build, Services 599 and Desktop 176 under CoreCLR and
NativeAOT, the 70-project architecture gate, and NativeAOT/trimmed Desktop
`--validate-shell` and `--validate-setup` all passed. The complete execution
record and platform/acceptance limits are in
[XSR-795](XSR-795-unimplemented-inventory.md#本轮集成交付与验证).
