# XSR-800 — 普通第三方账户启动

本切片把已登录的普通 Yggdrasil 账户接入现有 Authlib Injector 启动链。
第三方服务器使用已保存、正规化的账户地址；启动前 validate，失效时 refresh，
持久化轮换的游戏凭据。刷新不能切换角色或服务器，迟到结果不能覆盖已修改账户。
取消、刷新失败、读取/写入错误保持已有失败语义，不以离线身份代替认证失败。
Desktop 只按受支持的账户种类开放入口，认证与 injector 下载仍由 Services 负责。
LittleSkin 独立 OAuth/game token 路径继续保留。NCloud 外部提供方不属于此切片。

验证覆盖有效会话、刷新与持久化、不同角色响应、账户代际变化及取消。
