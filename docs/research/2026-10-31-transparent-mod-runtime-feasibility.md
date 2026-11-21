# AAVE：无需逐个 Mod 适配的透明接管可行性

日期：2026-10-31（Asia/Shanghai）。状态：**架构研究与候选路线，未实现、未安装、未通过真实宿主验证**。

## 用户目标与本次结论

用户要求：其他 Mod 无需修改，AAVE 不按 Mod 名称/版本逐个写适配器，自动统一同步；普通导出不承担开发审计；不能因为操作 AA 抢走前台窗口。

值得推进的更底层路线是 **接管导出期间的运行环境：引擎执行阶段 + 通用时间/等待服务 + 异步结果发布 + 原生媒体推进**。这不同于现有“统一时钟供特定适配器调用”。修正一个通用机制，应覆盖使用该机制的所有 Mod，包括开发时未知的 Mod。

但不能把这条路线宣传成已经解决“任何任意实现的 Mod 全部无条件同步”。共同时间、共同执行调度、可重复输出、符合作者原有语义是四件不同的事。整个程序可重复执行，也可能只是重复同一错误时序。对任意外部进程、网络响应、原生私有线程、未接管设备或语义混用的等待，普通 AAVE 插件没有无限控制能力。这是尚待解决的目标差距，不是把用户目标悄悄改成当前适配范围。

## 重新核对的当前事实

- 主安装为 `F:\AzureArchive_101`。`BepInEx/LogOutput.log:1-7` 和 `doorstop_config.ini` 确认 Unity 2023.2.22f1、BepInEx 6.0.0-be.785、CoreCLR .NET 6.0.7；游戏是 IL2CPP，托管 Mod 在 CoreCLR 中运行。不能按 Unity Mono 的单一执行环境设计。
- `src/VideoExport.Plugin/NativeExportClock.cs:55-69` 根据真实 Unity frame 发布虚拟快照，并明确允许 held 阶段原生更新继续运行。`delta=0` 不是停止执行。
- `src/VideoExport.Plugin/ExportHost.cs:738-751` 仍启动 MoreEffects、Flandre、CharacterVoice 等定向适配；`:826-873` 门控捕获和 `_captured`，不是门控整个引擎。
- 本次检索 `src` 未发现 `PlayerLoop`、`GetCurrentPlayerLoop`、`SetPlayerLoop` 调用。已安装 interop DLL 暴露这些接口及 Update、FixedUpdate、LateUpdate、延迟任务、资源加载和异步读回阶段；仅为静态接口证据。
- `NativeCaptureScope.cs:126-138,330-355` 已设置 `captureFramerate` 并实际推进 Unity 离线 AudioRenderer。这些是应复用的现有能力，不应包装成尚未拥有的新突破。
- `CharacterVoiceDelayClock.cs:9-10,83-120` 已明确区分作者的播放后等待和加载/播放 watchdog。`ExportHost.cs:686-688,714-719` 则证明有资源就绪工作依赖正常 Update。
- 已复核的加载顺序在 `docs/runtime-attribution-20261029.md:115-155`。普通 AAVE 插件 Load 晚于部分 Mod 初始化；早期接管必须放在它们之前。现有开发 RuntimeBootstrap 是观察工具，不是可直接发布的通用执行器。

本次仅读取文件、安装程序集和已有日志，没有操作 AA 窗口、启动/停止 AA、修改安装包或恢复旧工作流程。0.2.12 的交付状态不由本研究重新定义。

## 三条路线的比较

| 路线 | 能解决什么 | 不能单独解决什么 | 判断 |
| --- | --- | --- | --- |
| Unity 执行阶段 + 引擎/运行时通用服务 | 统一更新次数、等待释放、媒体推进；无需识别 Mod 名字 | 混合资源/剧情回调，绕过入口的原生/外部执行，语义模糊等待 | 首选，先做宿主可行性验证 |
| Windows 进程计时函数拦截 | 覆盖部分 Stopwatch/QPC、系统时间读取等共同入口 | 改计时读数不等于调度 Task.Delay、线程唤醒、I/O 或解码；还可能改变内部 watchdog | 只可作明确限定的补充 |
| 整进程/整机确定执行或仿真 | 将更多线程、设备和外部事件纳入统一执行模型 | GPU/驱动/媒体/网络边界和高代价；确定重放不等于单遍正确同步 | 研究备选，不承诺当前性能或可部署性 |

不存在“再往下钩住一个时间函数，一切自然同步”的充分依据。

## 首选候选设计：ExportRuntimeSession

这是候选架构，不是已批准实施规范或已存在接口。

### 1. 引擎阶段门控

以当前 PlayerLoop 树为基础，保存并保留其他插件加入的节点与顺序，在明确的阶段边界控制模拟能否向前推进；不可用默认 loop 覆盖掉第三方节点。

一次模拟步的目标顺序为：发布逻辑时间快照 → 发布本步允许的外部完成事件 → 执行适用的原生/托管模拟与协程阶段 → 完成布局/动画/媒体对应状态 → 提交渲染及匹配音频窗口。

等待素材、解码或队列容量时，不能继续多执行 `Update(){counter++;}`、`yield return null`、LateUpdate 或剧情 continuation。仍需执行不推进剧情的资源、GPU、编码、取消和进度工作。主机 phase 的选择和原生函数调用约定必须实测，不能把任意 updateFunction 指针当普通委托调用。

**关键未解点：**剧情逻辑、加载结果消费和 UI 可能在同一个脚本 Update 或 ScriptRunDelayedTasks 中。仅暂停整个阶段会阻塞就绪和取消；继续整个阶段又会推进剧情。需要证明通用异步边界或启动前准备能解决指定场景，不能用每个 Mod 的私有字段知识冒充通用接管。

### 2. 通用时间与等待服务

统一的是受控执行域中的时间、时差、帧号和等待释放规则，而不只是 `Now`。保留时间原点、scaled/unscaled 区别、局部暂停/倍速和已经存在的等待剩余量；在取消/结束时按确定顺序恢复。

Unity 原生入口与 CoreCLR 入口需要分别处理。安装中的部分 Time getter 有原生 MethodInfo，另一些是 ResolveICall 包装；只改托管包装不证明覆盖引擎原生内部读取。直接拦截原生时间 getter 仍不等于推进 Animator、Physics、ParticleSystem 等内部状态。

通用托管补充可在正常 Mod 加载之前，对内存中的调用做按 API 家族的转换，原始 DLL 不改，也不要求作者配合。需要验证模块初始化、加载上下文、依赖解析、程序集身份/Location、反射、晚加载、动态生成代码、其他插件后续打补丁等路径。当前 BepInEx preloader 默认处理 interop 目录，不会自动覆盖普通 Mod DLL；不得把“有 preloader”当作“已经全面接管”。

不能按 Mod 名、程序集版本、固定私有字段或某条剧情单独放行。新 Mod 使用同一受支持机制时，必须自动获得同一执行规则。

### 3. 两种时间域与完成事件发布

剧情动画使用模拟时间；AAVE 的取消、真实资源超时、编码和界面控制继续用真实时间。后台工作可以并行，但受控完成事件应在明确模拟阶段发布，带有会话代次和因果依赖，避免旧导出的迟到回调污染新导出。

不能简单按“哪个先完成”确定剧情结果，也不能简单按创建顺序阻塞全部任务：有些任务互相依赖。线程池与 SynchronizationContext 不是全部异步代码的唯一入口，`ConfigureAwait(false)`、自建线程、共享变量写入等另有覆盖问题。

语义反例：同一个 `Task.Delay(5000)` 可能代表剧情停顿五秒，也可能代表资源加载五秒超时。两者需要不同时间域。只看到函数名和参数，无法保证无误推断作者意图。整体运行域虚拟化可制定一致语义，但可能改变原行为；必须将这种变化明确作为产品选择，不能宣称完全透明。

### 4. 媒体采用后端级接管

针对 Unity AudioSource/AudioRenderer、VideoPlayer、Animator、粒子/物理等公共引擎机制集中实现，而不是针对某个 Mod 名称。复用现有离线真实 PCM 和有界渲染流水线。

视频需要在采样时间对应的帧实际可用后才提交；音频需要实际混音并遵守连续样本窗口，不能用修改 DSP getter 或声明长度代替真实媒体推进。各对象保留局部播放起点、seek、暂停、循环、倍速与切换语义；不能把全部视频都设成相同绝对播放位置。自带媒体/音频 native 后端不能假定使用 Unity 这些公共服务。

### 5. 模拟步与输出帧率明确分开

本次未发现独立固定模拟步策略；现有 captureFramerate 与用户输出 FPS 关联。按更新次数而非秒数运动的 Mod，在每帧一步时会随输出 FPS 改变速度。必须明确模拟频率与采样频率之间的契约，不可未经依据硬设 60 或 120 Hz，也不能承诺任意错误的帧计数逻辑在所有帧率下自动保持作者意图。测试应分别验证同一模拟配置下的不同输出帧率，以及原始行为的保留程度。

## 必须先证伪的宿主风险

Unity 官方问题 **UUM-57619** 记录独立 Player 中 SetPlayerLoop 被忽略，包含 2023.2.2f1，2023.2.X 分支显示 Won't Fix。它不证明 AA 的 2023.2.22f1 必然受影响，但足以否定“接口存在就能用”的推断。

因此第一个原型必须在隔离 AA 宿主里观察真实 callback 执行次数与顺序，包括加载场景后的行为，而不是只读回 GetCurrentPlayerLoop 看节点是否存在。若公开入口在实际宿主不生效，再评估已识别的原生 PlayerLoop 调度边界。此类原生补丁按 Unity/宿主构建验证，不转化为按 Mod 编写补丁。

## 建议的实施顺序与验收条件

1. **宿主阶段原型**：独立实验安装验证 loop 回调、脚本/原生阶段可控性和恢复；不改变主安装，不抢前台。新增未知测试 Mod 只使用标准 Update/yield/timer 等机制，无 AAVE 专用调用。
2. **运行时通用接管**：按机制实现时间、等待、continuation 和媒体服务，覆盖启动前等待、晚加载、取消和重导。先证明混合资源与剧情回调的方案，再扩大范围。
3. **去除定向依赖的验收**：在实验构建中禁用对应专用适配器，未知/重命名 Mod 仍通过；仅在保留专用适配器时通过不能证明目标实现。此步骤不在生产中贸然移除已经工作的代码。
4. **性能与产品验收**：同片段、同输出/编码配置、同输入/焦点条件做完整耗时对照；保留 GPU readback、编码并行和有界容量。没有实测前不承诺追平 0.2.4 或零成本。

开发期必测：逻辑时间保持期间插入 0/1/20/200 次基础设施轮询，计数器/yield/动画不得多走；I/O 乱序、CPU 竞争、队列满、暂停、取消、同进程连续导出、媒体 seek/循环/转场和真实 Azurite 交接恢复。测试必须比较真实效果、事件、PTS 和 PCM，不能只测时间 getter。

普通导出只做执行必需的调度、资源就绪和错误处理，不运行开发审计、重复导出比对、逐帧哈希、全 Mod 反射扫描或调用栈追踪。自动绑定/必要代码转换集中在加载或会话建立阶段；避免热路径反射扫描。等待真正必需的资源是执行依赖，不是开发审计，不可为省时间跳过。

## 依据

本地依据已列到相应段落。外部依据仅使用一手资料，访问日期 2026-10-31：

- Unity 2023.2 PlayerLoop.SetPlayerLoop：允许替换更新顺序；省略的原生系统不再更新。https://docs.unity3d.com/2023.2/Documentation/ScriptReference/LowLevel.PlayerLoop.SetPlayerLoop.html
- Unity 2023.2 Time.captureDeltaTime：只控制捕获的游戏时间步，不影响 unscaledTime。https://docs.unity3d.com/2023.2/Documentation/ScriptReference/Time-captureDeltaTime.html
- Unity 2023.2 Video clock management：GameTime 与 DSP/unscaled 的捕获行为不同。https://docs.unity3d.com/2023.2/Documentation/Manual/video-clock.html
- Unity UUM-57619（具体宿主是否受影响未验证）：https://issuetracker.unity3d.com/issues/playerloop-dot-setplayerloop-function-is-ignored-when-playing-game-in-player
- Unity 2023.2 公开绑定源码（不是完整 native 引擎实现）：https://github.com/Unity-Technologies/UnityCsReference/blob/2023.2/Runtime/Export/PlayerLoop/PlayerLoop.bindings.cs
- Microsoft Task.Delay：系统时钟相关行为，不代表已进入 Unity 模拟时钟。https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-6.0
- QEMU record/replay 与 instruction counting：完整设备/事件接管的参照，不是已验证的 AA/Windows/GPU 解决方案。https://www.qemu.org/docs/master/system/replay.html
- 另见同目录 `2026-10-31-transparent-runtime-primary-sources.md` 的逐项原始资料核对。

本次研究没有实现上述运行时，也没有证明任意 Mod 的透明接管。它提供了可验证的底层路线、最先要排除的宿主风险，以及不能用“时钟已统一”掩盖的剩余问题。
