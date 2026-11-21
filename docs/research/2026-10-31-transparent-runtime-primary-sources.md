# 透明导出时间运行时：官方资料与能力边界

核查日期：2026-10-31（Asia/Shanghai）

状态：**研究记录；不是已批准实施设计，不是已实现能力或 AA 原生验收报告。** 本次只读取本地源码、安装文件和日志，并新增本文；没有启动/操作 AA、构建、安装或修改现有发布包。

## 结论

不改 Mod 二进制、不写逐 Mod 适配器，是值得推进的工程目标：可以把接管层下移到 Unity 公共时间接口、协程/任务调度、原生音视频和进程运行时边界。但没有找到一个官方接口能把任意 Mod 的全部时间、异步完成、外部 I/O 和副作用自动转换为同一条作者时间线，同时保证所有原行为不变。[S1–S17]

必须区分两种承诺：

1. **按公共机制覆盖一批 Mod**：不识别 Mod 名称或业务类型，而是对已验证的时间、等待和媒体机制提供统一行为。这能形成可验证的兼容范围。
2. **任意 Mod 的无条件语义透明性**：包括自行 P/Invoke、原生插件、自建线程、真实日期、网络服务、设备输入、随机性和自定义调度。这超出目前资料和本地证据支持的范围；不能因补齐若干 API 钩子或重复导出一致就宣称成立。

确定性回答的是“相同状态与输入能否重现相同执行”，不自动回答“该执行是否保留作者要求的声画时序”。整机记录/回放也会忠实重放原本就错误的延迟。后一句是由记录/回放定义推导出的工程判断，不是 QEMU 对 AAVE 的支持声明。[S16]

## 已核实的本地基线

- 本次目标安装为 `F:\AzureArchive_101`；其 `BepInEx\LogOutput.log` 第 1–7 行显示 BepInEx `6.0.0-be.785`、Unity `2023.2.22f1`、.NET `6.0.7`。`UnityPlayer.dll` 的产品版本同为 `2023.2.22f1 (6b19bf4f8115)`。目录包含 `GameAssembly.dll`、Il2CppInterop 以及 IL2CPP chainloader 日志。这是现存文件/日志证据，未在本次操作中重新启动或验收运行时。
- `src/VideoExport.Plugin/NativeCaptureScope.cs` 已设置 `Time.captureFramerate`，并调用 `AudioRenderer.Start()`、`GetSampleCountForCaptureFrame()` 与 `Internal_AudioRenderer_Render(...)`，同时读取 DSP 时间与维护 PCM 样本位置。**这些不是待新增功能。**
- `CaptureHooks.cs` 已有 `unscaledTime`、`unscaledDeltaTime`、NGUI `RealTime`、`WaitForSecondsRealtime.keepWaiting/Reset` 等钩子；`NativeExportClock.cs` 已有统一帧快照和等待状态。需要研究的是覆盖深度、调用边界与语义完整性，不是再加一个同名时钟。
- `docs/unified-export-timeline-20261031.md` 记录现有统一时间线工作的范围。其安装和验收结论属于该文的既有证据；本次研究没有重新执行那些验收。

## 官方事实与对 AAVE 的含义

| 机制 | 第一方资料确认的能力 | 不能据此承诺的事情 |
|---|---|---|
| `Time.captureDeltaTime` / `captureFramerate` | 捕获时以固定间隔推进游戏时间；`captureDeltaTime` 明确不影响 `Time.unscaledTime`。[S1] | 不能单凭该设置推导 unscaled、真实时间、.NET 计时器和原生计时器同步。 |
| `PlayerLoop.SetPlayerLoop` | 可调整 Unity 系统顺序并插入回调；遗漏的系统不会运行；新顺序在下一次完整循环生效。[S2] | 它不是线程、I/O、原生媒体所有权的通用开关。不能盲目重建默认循环，更不能把取得原生回调指针等同于可安全手动调用。 |
| PlayerLoop 的版本风险 | Unity 官方 UUM-57619 记录 standalone Player 忽略 `SetPlayerLoop`，列出 `2023.2.2f1` 可复现，`2023.2.X` 分支状态为 Won't Fix。[S18] | 这不是 AA `2023.2.22f1` 已复现该故障的证据；但仅凭 API 存在、调用成功或 `GetCurrentPlayerLoop` 读回正确，不能证明阶段接管生效。 |
| `WaitForSecondsRealtime` | API 文档定义它使用 unscaled 时间；Unity 2023.2 参考源码的实际截止时间由 `Time.realtimeSinceStartup` 建立，并在结束时重置。[S3,S4] | 只替换 `unscaledTime` getter 不等于覆盖该等待。构造、首次轮询、跨导出边界的既有等待、重用和 Reset 都有状态。 |
| 自定义协程等待 | `CustomYieldInstruction.keepWaiting` 在每帧 Update 之后、LateUpdate 之前轮询。[S5] | 时间值相同仍不意味着回调执行阶段相同；改变轮询/恢复阶段可能改变可见帧与副作用顺序。 |
| Unity 音频录制 | `AudioRenderer.Start` 进入录音模式，正常扬声器输出静音直至 Stop；`Render` 录制主输出及注册的 mixer group；可查询自上次 Render 后可用样本数。[S6–S8] | 文档不是“所有外部音频设备、原生插件及 .NET 回调均受此控制”的证明。现有 AAVE 原生混音路径仍须按样本、音源完成事件和恢复行为验证。 |
| `VideoPlayer` 捕获同步 | Unity 2023.2 手册说明：游戏时间模式可随捕获帧率同步，并阻塞准备/seek 以满足帧时刻；DSP 或 unscaled 模式仍异步，可能跳帧/重复帧。[S9] | 不同 Mod 选择不同时间模式时，不能假定全部视频自然同步。强制改模式也可能改变原作者暂停/倍速/音频主时钟语义。 |
| 视频外部参考时间 | `externalReferenceTime` 在 `VideoTimeReference.ExternalTime` 下给出视频纠偏所追随的外部时钟值。[S10] | 这是时钟参考接口，不是一个“指定帧已解码、音频已正确混合、全部回调已完成”的通用屏障。外部时钟模式需要独立原生验证。 |
| .NET 延迟与系统时间 | .NET `Task.Delay` 文档明确依赖系统时钟；Windows 官方资料区分 QPC/Stopwatch 的间隔计时与 UTC 时间 API。[S11,S12] | 替换一个 Unity getter 不会重排已登记的任务/计时器；替换所有时间读数也不自动控制线程唤醒、I/O 完成和任务恢复顺序。后者是调度机制的工程推论。 |
| Harmony / HarmonyX | 原版 Harmony 记录内联和无 IL 的原生方法限制；HarmonyX 则明确扩展了 extern 方法的 prefix/postfix/transpiler/finalizer 支持及自定义后端。[S13,S14] | **不能用原版 Harmony 的限制直接断言 BepInEx 无法接管原生方法。** 反之，可 patch 一个 managed extern 也不证明覆盖所有 native-to-native 调用、内联副本或缓存函数指针。 |
| IL2CPP 互操作 | Il2CppInterop 提供 Unity IL2CPP 与 CoreCLR 间的桥接、包装和集成能力；HarmonyX 支持为 IL2CPP 实现自定义 patch 后端。[S14,S15] | “有互操作库”不等于“任意引擎内部状态/函数都能安全调用”。具体 ABI、调用线程、版本和生命周期必须在目标运行时证明。 |
| 整机确定性回放 | QEMU 用 `icount` 和非确定事件记录/重放控制执行，包括输入、网络、时钟和中断等；磁盘、网络有专用 replay 配置。[S16] | 不是一个给现有 Unity 进程加上固定 dt 的小改动；本次没有 AA、D3D、GPU、媒体设备、性能或部署兼容性证据。 |

Unity 的视频手册在异步段落中有一处措辞不顺的尾句。本文依据其明确区分 game time 与 DSP/unscaled 的主体说明，不扩大解读为所有平台或所有视频模式都保证逐帧同步。[S9]

## 可讨论的下沉层次（待设计，不是实施指令）

### 第 0 层：保留现有帧时间线、混音与恢复骨架

现有 `NativeCaptureScope` 的固定帧率和 AudioRenderer 以及 `NativeExportClock` 的快照可以作为继续调查的基线。首先定义一个已提交输出帧对应的作者时间和音频样本窗口，确认游戏更新、协程恢复、混音、视频就绪与截图之间的顺序。不能为了新方案跳过 RenderControlV1 的获得/恢复生命周期，也不能把故障恢复时钟混入作者时间。

### 第 1 层：Unity 引擎公共接口及阶段

以公共机制为边界研究 scaled/unscaled/realtime 的各类读数、固定更新、协程等待和媒体时钟。通过已加载引擎的真实调用路径确定接管点；保留当前 PlayerLoop 的所有已存在系统，只在验证后考虑插入阶段或屏障。[S1–S5]

**前置门槛：** 对本安装的 PlayerLoop 先做独立开发探针：安装可撤销的 marker 回调，观察它在预期阶段实际执行，并验证导出结束/取消后原循环恢复。不能只检查 `GetCurrentPlayerLoop` 读回。官方 UUM-57619 在 2023.2 分支的处理状态使此门槛尤其必要；若公用 API 在本机失败，再研究引擎原生阶段钩子，不能静默降为未知顺序继续导出。[S18]

重要问题是：接管点是否覆盖原生引擎消费方、已编译 IL2CPP 的调用和 CoreCLR Mod 的调用，而不是仅证明某个托管包装方法能被测试调用。对于 PlayerLoop 原生指针或 ABI 不明的入口，先做独立、可回滚的开发探针，不在研究阶段调用生产实例。

### 第 2 层：公共运行时的“时钟 + 调度”

如果目标是支持不使用 Unity 时间 API 的 Mod，范围会扩展到 .NET 延迟、计时器、Stopwatch/系统时间、等待句柄、线程池和恢复阶段，以及它们向 OS 的边界。接管必须考虑从注册到取消、完成、续体恢复的整个生命周期；不能只改 getter。[S11,S12,S14]

这种方式可以摆脱逐 Mod 名称映射，但不能自动推知每次延迟是剧情停顿、资源加载超时、重试退避还是外部服务时限。怎样保持真实故障处理又推进作者时间，是需要明确政策和反例检验的设计决策；不能未经批准就全局替换。

### 第 3 层：媒体与资源的完成条件

音频已经有真实离线混音；视频存在不同同步模式。需要研究一套按媒体对象/公共接口工作的完成条件，并保留每个对象原有的起始偏移、暂停、倍速、结束事件和生命周期，而不是把所有播放器强行设置成同一个播放头。[S6–S10]

一个加载等待在真实世界完成之前，能否只暂停作者时间且不阻断资源线程，要靠实际引擎顺序证明。不能预设“所有等待都冻结”或“所有等待都按 dt 跳过”任一策略普遍正确。

### 第 4 层：进程/OS/虚拟机确定性环境

只有在前三层不能覆盖的范围明确值得付出代价时，再把整个执行环境作为独立研究路线。QEMU 说明此路线需要同时处理输入和设备事件；它验证的是记录/重放机制存在，并未验证 AA 在该环境中的兼容性或性能。[S16]

它也不自动保证作者语义：要么回放已有参考执行，要么定义新的虚拟时钟与环境政策。前者可以重现参考错误，后者可能改变 Mod 对真实世界的观察。

## 必须能解释的语义反例

以下是研究用反例，不是检测到具体已安装 Mod 出错的报告。

1. **同一 `Task.Delay(1000)` 的两种含义。** A 用于角色出场后停顿一秒，B 用于 HTTP 请求超时一秒。统一映射到加速作者时间，B 可能在真实请求完成前超时；全部保留真实时间，A 又跟不上加速导出。仅看 API 名称和参数无法区分其含义。[S11]
2. **相同计时 API 同时承担内容与诊断。** Mod 用 Stopwatch 控制特效，又用它判断解码器卡死。全局改 QPC/Stopwatch 会同时改变 watchdog；只改 Unity 时间覆盖不了前者。完整环境虚拟化可以定义另外一套政策，但不能宣称保留了两种原语义。[S12]
3. **异步胜者决定内容。** Mod 比较两个资源任务哪个先完成，并选择对应过渡。固定时间值仍不能固定网络/磁盘/线程的完成顺序；强制某个顺序可能改变原本观察到的行为。QEMU 的非确定事件日志说明需要控制的范围远大于一个 getter。[S16]
4. **有意使用真实日历。** Mod 根据 UTC 日期生成文本，或外部服务器按有效期回答请求。冻结或加速日期与保留外部世界语义可能冲突。Windows 官方文档明确区分日期时钟和间隔时钟。[S12]
5. **原生计时绕过包装层。** Mod 的原生 DLL 可以自己读取 QPC；成功 patch 一个 C# 包装调用不能证明这一条独立路径已受控。HarmonyX 的原生能力让研究成为可能，却不是进程内所有计时路径的自动覆盖证明。[S12,S14]
6. **全局播放头破坏局部语义。** 两个视频在不同剧情时刻启动，其中一个暂停或变速。即使都接受外部时钟，也需要保持各自局部 epoch 和播放状态，不能简单设置成导出绝对秒数。[S9,S10]
7. **延迟首次轮询与对象重用。** `WaitForSecondsRealtime` 首次 `keepWaiting` 轮询才建立截止时间，完成后重置。把开始点一律移到构造时刻会改变已有 Unity 语义；同样，新导出中的旧等待也不能未经定义重新从零计时。[S4]

这些反例支持“需要声明与验证边界”，不构成对任何更深层实现的数学不可能性证明。

## 后续研究应交付的证据

- 建立小型独立测试 Mod，分别只使用 Unity scaled/unscaled/realtime、等待重用、Task.Delay、原生计时、视频不同模式、音频完成事件和异步资源。它们测试公共机制；不要求生产中的逐 Mod 专用适配。
- 先证明下沉接管点和原生调用覆盖，再验证时序及资源恢复；随后用真实 AA 剧情做单次正确输出、重复一致性和取消后再次导出。上述阶段彼此不能互相替代。
- 使用外部帧/PTS/PCM、可见副作用与恢复结果判断作者语义；开发探针/重复验证属于开发验收，不应因此强加生产审计、证明清单或输出 sidecar。
- 不把“拒绝未知路径”“没有崩溃”“测试程序集通过”或“错误输出重复一致”改写成任意 Mod 透明支持成功。
- 本文没有测得新路线的速度，也没有给出性能改进或交付时间承诺。

## 第一方来源（本次实际读取）

以下网页/源码均于 2026-10-31 读取。Unity 优先使用与目标主次版本一致的 2023.2 文档；GitHub 的 `master`/wiki 和 QEMU 文档不是目标已安装包的固定版本证明。S1–S16 读取了正文；S18 的网页需要脚本，本次读取到官方记录的搜索索引正文，明确不把它当成本机复现证据。链接是来源，不是授权运行其中代码。

- **S1** Unity 2023.2 `Time.captureDeltaTime`: https://docs.unity3d.com/2023.2/Documentation/ScriptReference/Time-captureDeltaTime.html
- **S2** Unity 2023.2 `PlayerLoop.SetPlayerLoop`: https://docs.unity3d.com/2023.2/Documentation/ScriptReference/LowLevel.PlayerLoop.SetPlayerLoop.html
- **S3** Unity 2023.2 `WaitForSecondsRealtime`: https://docs.unity3d.com/2023.2/Documentation/ScriptReference/WaitForSecondsRealtime.html
- **S4** Unity 官方 2023.2 参考源码 `WaitForSecondsRealtime.cs`: https://raw.githubusercontent.com/Unity-Technologies/UnityCsReference/2023.2/Runtime/Export/Scripting/WaitForSecondsRealtime.cs
- **S5** Unity 2023.2 `CustomYieldInstruction`: https://docs.unity3d.com/2023.2/Documentation/ScriptReference/CustomYieldInstruction.html
- **S6** Unity 2023.2 `AudioRenderer.Start`: https://docs.unity3d.com/2023.2/Documentation/ScriptReference/AudioRenderer.Start.html
- **S7** Unity 2023.2 `AudioRenderer.Render`: https://docs.unity3d.com/2023.2/Documentation/ScriptReference/AudioRenderer.Render.html
- **S8** Unity 2023.2 `AudioRenderer.GetSampleCountForCaptureFrame`: https://docs.unity3d.com/2023.2/Documentation/ScriptReference/AudioRenderer.GetSampleCountForCaptureFrame.html
- **S9** Unity 2023.2 视频时钟管理手册: https://docs.unity3d.com/2023.2/Documentation/Manual/video-clock.html
- **S10** Unity 2023.2 `VideoPlayer.externalReferenceTime`: https://docs.unity3d.com/2023.2/Documentation/ScriptReference/Video.VideoPlayer-externalReferenceTime.html
- **S11** Microsoft `.NET 6 Task.Delay`: https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-6.0
- **S12** Microsoft Windows 高精度时间戳、QPC、Stopwatch 与 UTC 时钟: https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps
- **S13** Harmony 边界（特别是内联与 native/extern 方法）: https://harmony.pardeike.net/articles/patching-edgecases.html
- **S14** HarmonyX 官方差异与 patch 后端说明: https://github.com/BepInEx/HarmonyX/wiki/Difference-between-Harmony-and-HarmonyX ; https://github.com/BepInEx/HarmonyX/wiki/Valid-patch-targets ; https://github.com/BepInEx/HarmonyX/wiki/Custom-MethodPatcher
- **S15** BepInEx Il2CppInterop 官方 README: https://raw.githubusercontent.com/BepInEx/Il2CppInterop/master/README.md
- **S16** QEMU 官方记录/回放文档源码（网页访问超时后读取官方仓库正文）: https://raw.githubusercontent.com/qemu/qemu/master/docs/system/replay.rst ; 对应网页 https://www.qemu.org/docs/master/system/replay.html
- **S17** 本地源文件和现存日志：本节前的“已核实的本地基线”所列路径。本次没有把其历史验收标记当成重新取得的原生证据。
- **S18** Unity Issue Tracker UUM-57619（`PlayerLoop.SetPlayerLoop` standalone Player 问题；官方记录列出 2023.2.2f1，2023.2.X Won't Fix）：https://issuetracker.unity.com/issues/10968/playerloopsetplayerloop-function-is-ignored-when-playing-game-in-player
