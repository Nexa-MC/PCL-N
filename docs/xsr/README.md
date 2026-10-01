# XSR architecture lock

This directory is the normative architecture baseline for the XSR migration. The three original migration guides remain design inputs; the documents here record the decisions that are enforced in this repository.

The user-requested constraints take precedence:

- migration work happens on `refactor/xsr` in a dedicated worktree outside the `dev` checkout;
- the XSR product line starts at `2.0.0` and uses the dotted version forms in [versioning.md](versioning.md);
- the branch contains no legacy source or project graph; the new XSR graph is built independently while `dev` is consulted read-only;
- Wave 0 locks architecture and migration policy only; it does not migrate product behavior.

## Documents

- [work-scheduling.md](work-scheduling.md) — 共享资源 admission、Launch Quiet Mode 与可见提示生命周期
- [download-trust.md](download-trust.md) — Minecraft 权威元数据、摘要绑定、旧任务与更新权限约束
- [update-signature-policy.md](update-signature-policy.md) — 固定发布密钥、签名算法、认证过期策略与封套预算
- [release-admission.md](release-admission.md) — 签名发布身份、完整包集合及未来更新准入
- [migrations/XSR-730-signed-release-manifest.md](migrations/XSR-730-signed-release-manifest.md) — 发布清单生成、独立验签与运行期准入
- [migrations/XSR-731-image-residency-budgets.md](migrations/XSR-731-image-residency-budgets.md) — 图标字节LRU与按可见尺寸/DPI解码
- [migrations/XSR-732-ordered-collection-deltas.md](migrations/XSR-732-ordered-collection-deltas.md) — 稀疏改动排序归并与未变快照复用
- [migrations/XSR-733-recovery-work-admission.md](migrations/XSR-733-recovery-work-admission.md) — 恢复采集共享额度与可延后分块回收
- [migrations/XSR-734-soak-wake-attribution.md](migrations/XSR-734-soak-wake-attribution.md) — 有界状态发布记录与idle唤醒归因
- [function-patches.md](function-patches.md) — Host授权的受限Function Patch执行、五阶段ABI与生命周期
- [migrations/XSR-735-bounded-function-patch-execution.md](migrations/XSR-735-bounded-function-patch-execution.md) — Function Patch会话注册到Host执行的首个闭环
- [migrations/XSR-736-compile-time-function-patches.md](migrations/XSR-736-compile-time-function-patches.md) — 编译前源码改写、增量构建隔离和实际资源标题point
- [migrations/XSR-737-shared-raster-budget.md](migrations/XSR-737-shared-raster-budget.md) — 共享动态bitmap预算、lease与可见性回收
- [migrations/XSR-738-soak-window-analysis.md](migrations/XSR-738-soak-window-analysis.md) — 独立常规观测窗口、峰值/趋势与原始证据哈希
- [migrations/XSR-739-soak-sampler-allocation.md](migrations/XSR-739-soak-sampler-allocation.md) — 对齐进程与采样器分配计数；旧记录保持未测量
- [migrations/XSR-740-resource-page-image-ownership.md](migrations/XSR-740-resource-page-image-ownership.md) — 资源页隐藏时释放编码图片引用，恢复原有条目且取消旧图标请求
- [migrations/XSR-741-private-launch-argument-transport.md](migrations/XSR-741-private-launch-argument-transport.md) — 共享启动边界拒绝公开参数传输，缺少Host时不回退到Java命令行
- [migrations/XSR-742-retained-idle-soak-evidence.md](migrations/XSR-742-retained-idle-soak-evidence.md) — 完整保留两小时/30分钟实际fixture样本、构建receipt与可重放分析
- [migrations/XSR-743-version-list-visible-window.md](migrations/XSR-743-version-list-visible-window.md) — 安装版本列表按可见窗口创建行，保留逻辑多选与双向键盘遍历
- [migrations/XSR-728-detached-signature-admission.md](migrations/XSR-728-detached-signature-admission.md) — detached GPG 策略收口
- [migrations/XSR-729-verification-receipt-lru.md](migrations/XSR-729-verification-receipt-lru.md) — 有界文件校验缓存与显式校验失败撤销
- [review-505b9f9f.md](review-505b9f9f.md) — 新审查的事实核对、修复范围和剩余证据
- [static-review-follow-up.md](static-review-follow-up.md) — 补充性能与架构审查的当前代码核对
- [migrations/XSR-727-idle-logging-and-download-progress.md](migrations/XSR-727-idle-logging-and-download-progress.md) — 空闲日志与有界下载进度
- [runtime-performance.md](runtime-performance.md) — 运行期性能优先级、资源预算、Splash 与验收边界
- [alpha6-beta-acceptance.md](alpha6-beta-acceptance.md) — 非商业路线图、真实 launch / soak 证据与 Beta 完成标准
- [diagnostic-export.md](diagnostic-export.md) — 用户主动诊断包、固定字段和日志隐私边界
- [instance-content-graph.md](instance-content-graph.md) — 实例依赖图、未知关系与更新检查生命周期
- [architecture.md](architecture.md) — system direction and project boundaries
- [dependency-rules.md](dependency-rules.md) — allowed dependency graph and CI enforcement
- [state-model.md](state-model.md) — state ownership, snapshots, deltas, and derived state
- [service-model.md](service-model.md) — service responsibilities and communication primitives
- [renderer-model.md](renderer-model.md) — UI.Next and backend boundaries
- [capability-fabric.md](capability-fabric.md) — provider discovery, dependencies and permissions
- [sidecar-protocol.md](sidecar-protocol.md) — Sidecar Fabric control/data planes
- [versioning.md](versioning.md) — XSR product-version grammar and compatibility surfaces
- [migration-map.md](migration-map.md) — waves, closed work units, and cutover gates
- [source-reference.md](source-reference.md) — clean-slate rules for consulting legacy code
- [migrations/XSR-002-project-graph.md](migrations/XSR-002-project-graph.md) — initial solution graph and architecture gate
- [migrations/XSR-101-identifiers-and-registry.md](migrations/XSR-101-identifiers-and-registry.md) — Wave 1 identity and sealed registry contract
- [migrations/XSR-102-command-query-routing.md](migrations/XSR-102-command-query-routing.md) — asynchronous command/query routing and stable errors
- [migrations/XSR-103-revisioned-state.md](migrations/XSR-103-revisioned-state.md) — revisioned cells, collections, snapshots, deltas, and derived state
- [migrations/XSR-104-ordered-events.md](migrations/XSR-104-ordered-events.md) — ordered event scopes and bounded delivery with backpressure
- [migrations/XSR-105-scheduling-lifecycle-diagnostics.md](migrations/XSR-105-scheduling-lifecycle-diagnostics.md) — scheduling, lifecycle state machines, and session traces
- [migrations/XSR-106-lifetime-scopes.md](migrations/XSR-106-lifetime-scopes.md) — runtime lifetime scopes for atomic resource cleanup
- [migrations/XSR-201-ui-entity-kernel.md](migrations/XSR-201-ui-entity-kernel.md) — renderer entity tree, components, dirty tracking, state bridge
- [migrations/XSR-202-layout-and-scene.md](migrations/XSR-202-layout-and-scene.md) — deterministic layout and the immutable render scene
- [migrations/XSR-203-input-navigation-overlay.md](migrations/XSR-203-input-navigation-overlay.md) — pointer and keyboard input, navigation, overlays, accessibility
- [migrations/XSR-204-renderer-gates.md](migrations/XSR-204-renderer-gates.md) — deterministic benchmark gates and renderer CI
- [migrations/XSR-205-animation-and-review-fixes.md](migrations/XSR-205-animation-and-review-fixes.md) — animation kernel, generational handles, thread-safe state bridge
- [migrations/XSR-206-renderer-completion.md](migrations/XSR-206-renderer-completion.md) — easing, keyframes, scroll, and the media slot
- [migrations/XSR-207-pxml-parser.md](migrations/XSR-207-pxml-parser.md) — PXML grammar and the structural parser
- [migrations/XSR-208-pxml-ir-compiler.md](migrations/XSR-208-pxml-ir-compiler.md) — PXML compilation to the typed UI.Next IR
- [migrations/XSR-209-pxml-loader.md](migrations/XSR-209-pxml-loader.md) — runtime loader with hand-built parity
- [migrations/XSR-210-pxml-gates.md](migrations/XSR-210-pxml-gates.md) — PXML NativeAOT, generated-catalog, and Wave 3 acceptance gates
- [migrations/XSR-211-pxml-review-hardening.md](migrations/XSR-211-pxml-review-hardening.md) — parser boundary and transactional loader review fixes
- [migrations/XSR-212-generated-control-catalog.md](migrations/XSR-212-generated-control-catalog.md) — required UI.Next control directory and early generated compiler catalog
- [migrations/XSR-401-sidecar-protocol.md](migrations/XSR-401-sidecar-protocol.md) — Sidecar frames, message numbers, and the TLV payload codec
- [migrations/XSR-402-sidecar-transport.md](migrations/XSR-402-sidecar-transport.md) — frame transport and the connection lifecycle
- [migrations/XSR-403-sidecar-session.md](migrations/XSR-403-sidecar-session.md) — host session lifecycle, registration, and the state mirror
- [migrations/XSR-404-data-plane-and-reconnect.md](migrations/XSR-404-data-plane-and-reconnect.md) — data plane, bounded exchanges, crash recovery, and reconnect
- [migrations/XSR-405-execute-by-id.md](migrations/XSR-405-execute-by-id.md) — session-local contract IDs, snapshot lifecycle, capability boundary, protocol draft
- [migrations/XSR-406-transactional-snapshot-typed-codecs.md](migrations/XSR-406-transactional-snapshot-typed-codecs.md) — transactional snapshots, typed codec registry, content-addressed host cache
- [migrations/XSR-501-settings-capability.md](migrations/XSR-501-settings-capability.md) — Wave 5 settings capability: schema, durable-first writes, stable errors, legacy file compatibility
- [migrations/XSR-502-logging-capability.md](migrations/XSR-502-logging-capability.md) — Wave 5 logging capability: bounded redacted ring as ordered state, level gate, no static sink
- [migrations/XSR-503-launcher-settings-compatibility.md](migrations/XSR-503-launcher-settings-compatibility.md) — launcher settings JSON compatibility: full legacy key universe, quarantine recovery, atomic saves
- [migrations/XSR-504-download-capability.md](migrations/XSR-504-download-capability.md) — download capability: failover with resume, per-destination coalescing, active transfers as state
- [migrations/XSR-505-segmented-download.md](migrations/XSR-505-segmented-download.md) — segmented parallel download: range planning, part-file assembly, fallback for non-segmented sources
- [migrations/XSR-506-account-capability.md](migrations/XSR-506-account-capability.md) — account capability: legacy launch profile file compatibility with credential-free state views
- [migrations/XSR-507-update-block-contracts.md](migrations/XSR-507-update-block-contracts.md) — update block data contracts: FastCDC chunking, gzip/zstd block codecs, local block index
- [migrations/XSR-508-update-eligibility.md](migrations/XSR-508-update-eligibility.md) — one-way upgrade gate: legacy 1.4.x crosses into 2.0.0, downgrades never offered
- [migrations/XSR-509-update-package-planning.md](migrations/XSR-509-update-package-planning.md) — update package planning: variant selection, cheapest patch path, patch-versus-full by size
- [migrations/XSR-510-update-discovery-transport.md](migrations/XSR-510-update-discovery-transport.md) — update discovery and transport: index fetch, multi-tag walk, HEAD probe, eligibility gate
- [migrations/XSR-511-update-signing-delta-codecs.md](migrations/XSR-511-update-signing-delta-codecs.md) — update signature and delta codecs: pinned-key GPG verification, RFC 3284 VCDIFF decoder
- [migrations/XSR-512-staged-install-core.md](migrations/XSR-512-staged-install-core.md) — staged install core: verify/flatten/plan/apply with safe paths, re-verification, and managed deletes
- [migrations/XSR-513-online-account-flows.md](migrations/XSR-513-online-account-flows.md) — online account flows: Microsoft device-code chain, Yggdrasil validate/refresh, roster bridge
- [migrations/XSR-514-littleskin-oauth-appearance.md](migrations/XSR-514-littleskin-oauth-appearance.md) — LittleSkin OAuth (device flow, closet, texture upload) and Microsoft skin/cape services
- [migrations/XSR-515-file-capability.md](migrations/XSR-515-file-capability.md) — File capability: canonical data folders and the safe atomic file port
- [migrations/XSR-516-payload-extraction-patch-orchestration.md](migrations/XSR-516-payload-extraction-patch-orchestration.md) — payload extraction (zip/tar) and HDiffPatch orchestration with binary chains and scatter ops
- [migrations/XSR-517-network-telemetry.md](migrations/XSR-517-network-telemetry.md) — Network probing with latency and opt-in Telemetry buffering/flush with a pending state cell
- [migrations/XSR-518-helper-handoff-restart.md](migrations/XSR-518-helper-handoff-restart.md) — helper hand-off and restart scheduling: artifact validation, replacement process contract, launch port
- [migrations/XSR-519-wave5-acceptance-integration.md](migrations/XSR-519-wave5-acceptance-integration.md) — Wave 5 acceptance: unified host state composition, foundation command routing, cross-capability PXML integration
- [migrations/XSR-520-foundation-correctness-closure.md](migrations/XSR-520-foundation-correctness-closure.md) — Wave 5 review closure: raw typed settings, formal Foundation runtime composition, unified download logging
- [migrations/XSR-701-product-shell.md](migrations/XSR-701-product-shell.md) — Wave 7 product shell foundation: shared UI.Next chrome with the Experimental presentation
- [migrations/XSR-702-pxml-scene-backend.md](migrations/XSR-702-pxml-scene-backend.md) — Wave 7 PXML-to-scene Avalonia backend closure
- [migrations/XSR-703-product-base-plate.md](migrations/XSR-703-product-base-plate.md) — Wave 7 legacy-experimental base plate: title bar, icon rail, splash, frameless chrome, embedded icons, and fluid-interface motion
- [migrations/XSR-704-base-plate-runtime-hardening.md](migrations/XSR-704-base-plate-runtime-hardening.md) — Wave 7 base plate runtime hardening: desktop lifetime contract, UI.Next-owned rail geometry animation, live reduced-motion policy
- [migrations/XSR-705-launch-page.md](migrations/XSR-705-launch-page.md) — historical Wave 7 launch-page layout/navigation slice; its temporary direct launch flow is superseded by XSR-706
- [migrations/XSR-706-launch-orchestration-and-ui-state.md](migrations/XSR-706-launch-orchestration-and-ui-state.md) — product-level launch orchestration, Desktop-owned projection state, generation-safe discovery, and PXML key/accessibility separation
- [migrations/XSR-712-defer-liquid-glass.md](migrations/XSR-712-defer-liquid-glass.md) — Experimental-only product style; defer the alternate visual redesign while preserving motion and capsule controls
- [migrations/XSR-713-diagnostic-breadcrumbs.md](migrations/XSR-713-diagnostic-breadcrumbs.md) — English operation/stage logs, early startup capture, complete production router observation and credential-safe diagnostics
- [migrations/XSR-714-in-window-feedback.md](migrations/XSR-714-in-window-feedback.md) — window-internal lower-left notification service, modal PXML dialogs, accessibility/motion contract, and Java acquisition confirmation migration
- [migrations/XSR-721-install-entry-and-java-catalog.md](migrations/XSR-721-install-entry-and-java-catalog.md) — title-free Java/Bedrock two-card installation entry, embedded twelve-slice Java catalog with conditional Fabric API/QSL slices, wheel-inert horizontal pager, and truthful unavailable-installer feedback
- [migrations/XSR-722-install-catalog.md](migrations/XSR-722-install-catalog.md) — background catalogs, merged addon sources, compatibility-driven draggable selector, virtualized lists and search/name input
- [migrations/XSR-723-task-center.md](migrations/XSR-723-task-center.md) — foundation task tracking with stage-monotonic plans, the bottom-right rising-fill bubble, and the task center page over typed cancel/dismiss routes
- [migrations/XSR-724-install-execution.md](migrations/XSR-724-install-execution.md) — real installs: version documents first, shared download planners with bmclapi failover, file-accurate task progress, Fabric-family support and explicit processor-loader deferral
- [migrations/XSR-725-capability-planning.md](migrations/XSR-725-capability-planning.md) — explicit instance-scoped capabilities, estimator provenance, and preflight/remediation layers
- [migrations/XSR-726-jvm-host-observations.md](migrations/XSR-726-jvm-host-observations.md) — typed JVM host boundary, per-launch capabilities, and bounded runtime observations

## Decision process

Any change to a locked boundary requires:

1. a concrete motivating use case;
2. an update to the affected document;
3. an architecture-test or analyzer update;
4. compatibility and migration impact notes;
5. review before implementation depends on the new boundary.

Public API baselines describe the accepted surface; changing a baseline never turns a breaking change into a compatible one.
- [migrations/XSR-606-minecraft-launch-hardening.md](migrations/XSR-606-minecraft-launch-hardening.md) — Minecraft launch hardening: Java conflicts, token coverage, natives extraction, process state
- [migrations/XSR-608-minecraft-java-policy.md](migrations/XSR-608-minecraft-java-policy.md) — Minecraft Java policy closure: version schemes, manifest-first selection, and the historical compatibility matrix
- [migrations/XSR-609-minecraft-library-artifact-native-pair.md](migrations/XSR-609-minecraft-library-artifact-native-pair.md) — Minecraft library artifact/native pairing: preserve ordinary classpath JARs beside native classifiers
- [capability-registry-gap.md](capability-registry-gap.md) — Registry 1.1 与当前实现的全量差距矩阵（逐 namespace 探测缺口 + 六层架构缺口 + 依赖驱动的切片顺序）
