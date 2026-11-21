# 当前 Mod 严格导出准入审计（2026-10-25）

本报告对应 acceptance-20261025-62 的实际拒绝清单。目标是决定每个模块在严格逐帧导出中的最小安全边界，不把“静态扫描没有命中”当成 Certified，也不修改其他 Mod。

## 证据与方法

- 原生拒绝证据：`artifacts/acceptance-20261025-62-capability-rejection-final/native-checks.log`。
- 该次拒绝发生在读取剧情、获取 `RenderControlV1` 所有权和首帧之前；源文件 SHA-256 前后均为 `68699069E01F87BD7AD19F57D4140CAEBE73C9980A898CCA5EFCDF1DF88C5F54`。
- 静态审计原始输出：`artifacts/mod-admission-audit-raw-20261025.txt`，使用现有 `ModClockAudit` 扫描实际 AA 1.0.1 安装中的 11 个程序集。
- IL 结构补充输出：`artifacts/mod-admission-il-20261025/`。该检查只用于发现 P/Invoke、动态调用、反射、文件/进程和 Harmony 风险，不能替代 native 运行时验收。
- 汇总结果：11 个程序集全部可读取；共 294 个已识别调用点，其中 wall 28、Unity 214、audio/video 52。报告所用静态审计本身明确限制为“不能证明运行时调用或 IL2CPP/native 行为”。

严格准入分类：

- `Certified`：已有精确构建指纹、适配器和 native 双遍证据，当前清单中的 More Effects / Portrait 才能使用此级别。
- `Observed`：只代表经过限定的渲染协作边界；Azurite 的 `RenderControlV1.v1` 可以参与导出，但不能授权预检缓存复用。
- `Opaque`：静态或运行时仍存在未证明路径；严格导出拒绝，普通播放不受影响。
- `Rejected`：已知路径与当前严格时间线冲突；必须完成专门适配后才能重新评估。

## 模块结果

| 模块（版本） | 指纹（MVID / SHA-256） | 静态观察 | 当前级别 | 最小安全适配与 native 验收 |
| --- | --- | --- | --- | --- |
| AA 界面美化 / `AAMenuPrototype` 0.0.19 | `47739a97-91ed-4450-9928-e6b53bf3619e` / `1610E896FDD738DAED88EF418B5D037118D7B4A2D02B1149C7CFC4F4BFF1A589` | 242 点：Unity 198、audio 39、wall 5；`AudioSource` 播放/停止/游标；`Time.unscaledTime` 91、`frameCount` 91；10 个 `user32.dll` P/Invoke；File/Directory/Process/反射/Harmony。 | **Opaque** | 先证明导出时菜单对象、窗口 P/Invoke、AudioSource 不再运行；若菜单运行时仍会触碰剧情或音频，必须隔离/禁用并在恢复阶段重建。验收：菜单进入/退出、剧情加载后再开关菜单、取消恢复、24/30/60 fps 双遍帧状态和 PCM/PTS。 |
| `AAMultilingual` 0.1.0 | `9e577f42-6683-4079-a7a7-0860caaa5a74` / `B62F46FAD43082F1CD1DE2909B6DB55BB7ACA34C6A989F93D1C0289558B208B3` | 2 个 `Time.realtimeSinceStartup`；另有大量反射/Harmony 元数据调用；原生边界验证了 RenderControlV1 ownership 时暂停。 | **Observed** | 仍不能进入严格放行：需完成语言切换、设置/鉴赏刷新和剧情双遍导出，确认没有文本布局事件、等待或音频命令漂移；当前 Observed 只记录边界证据，不授权缓存复用或严格准入。 |
| `AASkipIntro` 0.1.0 | `9a4693f8-010c-4c58-b66d-fad395393afb` / `A78A9BB90FDD61C4A7D32B03AE35B08A8633BDF68B4CFBFEEE5360EEAE770DC6` | 未命中已知 wall/Unity/audio API；有 10 个 Harmony 结构引用；原生 Catalog 边界确认无活动 CreatorSplash 且 ownership 可恢复。 | **Observed** | 仍不能进入严格放行：需完成冷启动、跳过/不跳过、Catalog→Story、取消和重启，以及剧情双遍状态/PTS/PCM 验收。当前 Observed 只记录启动边界，不授权缓存复用或严格准入。 |
| 芙兰动态背景 / `FlandreScarletBackground` 5.2.0 | `544d35a8-fc4a-434b-9a5f-fb68fcc7564a` / `779484939C616E22E77DD925880DD3EC0E47ED28643CE333D51ACC335C3313B1` | 20 点：VideoPlayer audio 类 14、Unity 6、wall 1；`Prepare/Play/Stop/frame/frameCount/time/isPrepared`；含 `calli`/非托管委托、文件/目录访问。 | **Rejected** | 当前动态 VideoPlayer 解码不能证明与视频帧时间线一致。路线只能是预解码帧/纹理存储后按虚拟帧回放，或完整捕获并重放 VideoPlayer 事件和解码结果。验收：准备延迟、首帧、暂停/停止/切背景、seek、文件缺失、24/30/60 fps、双遍逐帧状态和内容 hash；完成前保持拒绝。 |
| 可更改主菜单音乐 / `MenuBgmSelector` 1.7.3 | `4e7ca964-4de8-4d46-8301-03a0b29cb1cb` / `5C56D01047FC6CABE7DAD3D51D95D733A8620FD03DC5A50041BC61ACC5CB98D4` | 5 个 wall：`Thread.Sleep`、`Environment.TickCount`、`DateTime.Now/Ticks`；`comdlg32.dll`/`user32.dll` P/Invoke；文件/目录操作；只读 CatalogScene ownership 探针观察到 3 个 AudioSource、0 个播放源，线程数 121 前中后不变。 | **Opaque** | 已有只读 ownership 边界证据，但瞬时线程/AudioSource 采样不能证明任意线程归属或 Story capture 行为。仍需证明选择/恢复自定义 BGM、进入故事、取消、重启和双遍音频/PTS 完全静止，或将 BGM 命令转成严格 DSP 事件并记录/回放；完成前不放行。 |
| `Rukari.Lib` 0.4.6（程序集版本 0.2.0.0） | `5b0332b5-b75b-4006-aa10-6eca36b0800f` / `7413B9534A309F7B2498CA709E3BB1769FFEFECB1FBBF5F55953AE5D6C22FD45` | 0 个已识别时钟点，但它是共享库，静态无命中不证明消费者回调、反射或 native 行为。 | **Opaque** | 按消费者建立依赖账本；只有所有使用它的 Mod 都有独立认证且运行时没有未观测回调，才可重新评估。验收：More Effects、Character Voice 及其组合的加载顺序、晚加载、取消、重启、双遍事件/PCM。 |
| `Rukari.Lib.Runtime` 0.4.6 | `18766066-a6b8-40a3-ab67-8c4c4b4e5242` / `BC8453F8E773A27CD9F88E18E82A45668DA2EF50D8C1EDC6C24EF05F201D8A41` | 25 点：wall 18（Stopwatch、Environment、DateTimeOffset）、Unity 8（`frameCount`、`realtimeSinceStartup`、`unscaledTime`）；大量反射/Harmony。 | **Opaque** | 将 UI/主题/Story surface 分区，给真正影响剧情的读取建立 scoped clock 或事件代理；共享库不能继承 More Effects 的 Certified。验收：主题面板、设置页、剧情播放、More Effects、Character Voice、取消/恢复、双遍状态和 PCM。 |
| 音效汉化 / `SfxFolderMod` 1.7.3 | `5da3b0cd-f4f3-421e-bc88-3ee5d6206c87` / `29CA0E83813704E07FE610060B177A85AB6B28EAB7FD9805E796D85B5D8B25D3` | 0 个已识别时钟/audio 点；13 个 Harmony 结构引用，File 3、Directory 1。 | **Opaque** | 静态确认只改编辑器显示和资源查找；仍需 native 验收其补丁是否触碰播放期资源/音频。验收：音效搜索/中文显示、进入剧情、音效触发、文件缺失、取消和双遍 PCM/PTS。 |
| `ExampleCore` 1.0.0 | `c1dd90cf-a65b-4ddd-8f18-8dbe25e0354c` / `4A209AD52EDD4A5BEB1F9EF2801527AEABD2F49A5522710F7F359EDB3D7F25FB` | 0 个已识别时钟/audio 点；无当前 IL 风险命中。 | **Opaque** | 运行时探针确认无 Unity 对象、时间、音频、场景或外部 I/O；依赖关系稳定后才可归入 deterministic-no-clock，仍需至少两次真实导出。 |
| `ExampleDependent` 1.0.0 | `86858117-c7ed-4c49-870b-7bcb1eef4616` / `3E387C762544C3A7315F2DB5972BA106B1AFFBA402BBA42D6C250272579DC8A8` | 0 个已识别时钟/audio 点；有 ExampleCore 依赖关系。 | **Opaque** | 与 ExampleCore 一起做依赖顺序、缺失依赖、晚加载、卸载和双遍事件探针；不能只因“示例 Mod”直接放行。 |
| `ExampleStandalone` 1.0.0 | `3177bafb-71a1-4a63-bf4d-71a4278c0382` / `82620F29C8F75B37DFD67286C549A08A9974CE00D8BE44160268E75BDE596188` | 0 个已识别时钟/audio 点；无当前 IL 风险命中。 | **Opaque** | 做独立动态探针和两遍 native story；通过后才能进入 deterministic-no-clock 候选。 |
| 运行时 `Anonymously Hosted DynamicMethods Assembly` | 无磁盘 MVID/SHA；由 acceptance-20261025-54 的实际加载清单发现 | 无可读静态文件；说明运行时存在动态生成 IL/反射路径。 | **Opaque** | 必须记录生成者、方法边界和调用目标；在生成者无法归属或事件未追踪时继续拒绝。 |

## 结论与放行规则

本轮没有模块升级为 Certified。AAMultilingual 和 AASkipIntro 仅升级到带原生边界证据的 Observed，仍不满足严格准入；Flandre 动态背景因动态 VideoPlayer/非托管调用被标为 Rejected；其余模块即使静态时钟点为零，也保持 Opaque。严格导出只有在当前加载集合中不存在 Opaque/Rejected/未授权 Observed，且 More Effects/Portrait 的精确适配器、Azurite 的 RenderControlV1 交接和双遍 native 证据全部通过时才可开始。

MenuBgmSelector 1.7.3 的当前二进制静态审计复核见
`artifacts/menu-bgm-audit-20261025.log`：MVID
`4e7ca964-4de8-4d46-8301-03a0b29cb1cb`、SHA-256
`5C56D01047FC6CABE7DAD3D51D95D733A8620FD03DC5A50041BC61ACC5CB98D4`，发现
`Thread.Sleep`、`Environment.TickCount` 和 `DateTime.Now/Ticks`，但静态扫描仍不能证明运行时线程、文件对话框或 AudioSource 行为。因此继续保持 `Opaque`，下一步仍需 native ownership 期间的线程/音频边界探针。

新增的只读 native ownership 边界探针见
`docs/menu-bgm-boundary-evidence-20261025.md`：隔离 CatalogScene 中实际加载的
`MenuBgmSelector` 1.1.4.0 程序集在 ownership 前/中/后均为 3 个 AudioSource、
0 个播放源，进程线程数均为 121，ownership 正常恢复（1/1）。这是瞬时边界
观察，不能证明任意线程归属或 Story capture 中未来不会有文件对话框、BGM
切换、播放和文件操作，因此不改变 `Opaque` 结论。

只读 native ownership 探针已通过：
`artifacts/acceptance-20261025-64-menubgm-boundary/native-checks.log`。在
CatalogScene 中，ownership 前/中/后均观测到 103 个进程线程、3 个
AudioSource、0 个播放源；探针没有打开文件对话框、切换 BGM、开始播放或
写入文件。该证据只能说明采样窗口的边界状态，不能证明任意线程归属或
后台线程未来不会唤醒，因此 MenuBgmSelector 继续保持 `Opaque`。

SfxFolderMod 1.7.3 的当前二进制 ModClockAudit 复核见
`artifacts/sfxfolder-audit-20261025.log`：MVID
`5da3b0cd-f4f3-421e-bc88-3ee5d6206c87`、SHA-256
`29CA0E83813704E07FE610060B177A85AB6B28EAB7FD9805E796D85B5D8B25D3`，0 个
已识别时钟/audio 调用点。静态零命中不能证明 Harmony 目标、资源查找和
播放期音效路径，因此继续保持 `Opaque`。

IL 复核补充：`Patch_GetSoundNames.OnNames` 只过滤返回的字符串列表，
`Patch_SoundItemInit` 只修改标签/布局；`SfxData` 的配置目录位于
`Application.persistentDataPath/SfxFolderMod`（必要时回退 Temp），可能创建或
读取 `config.txt`。当前 IL 未发现 clip resolution 或 AudioSource 播放调用，
但仍需真实故事音效触发和双遍 PCM/PTS 验收后才能考虑放行。

新增只读 native ownership 边界证据：
`artifacts/acceptance-20261025-68-sfxfolder-boundary/native-checks.log`。
SfxFolderMod 1.7.3 在 ownership 前/中/后均保持 `data_loaded=true`、774 个
资源项、Harmony installed、SoundExplorer 未打开、3 个 AudioSource 且 0 个
播放源；线程数保持 122。探针没有搜索、打开 SoundExplorer、切换音效、发送
音频命令或写文件。该采样不能证明资源查找或播放期 Harmony 路径的完整行为，
因此模块继续保持 `Opaque`。

Rukari.CharacterVoice 1.6.8 的当前二进制 ModClockAudit 复核见
`artifacts/charvoice-audit-20261025.log`：MVID
`3c455a03-837f-40d5-a719-281df650be68`、SHA-256
`06C4FE4F87282258FE45A8436FCDDBDCA079C58ED6356C31391F5BC3D855C054`，共
39 个直接调用点，其中 38 个 wall-clock、1 个 AudioSource.Stop；关键路径
`VoicePlaybackRuntime.Now` 使用 Stopwatch，且存在独立播放门控。该 Mod 还包含
作者工具、文件夹选择、导入/目录扫描和编译保护路径，不能继承 More Effects
认证。Character Voice 继续保持 `Opaque/Rejected`，需要独立播放门控适配和
原生双遍音频/PTS 证据后才能重新评估。

任何新版本、MVID、SHA-256、依赖集合、动态程序集生成者或 Harmony 目标变化都会使审计结论失效并回到 Opaque。普通 AA 播放不受这份严格准入报告限制；未认证模块只能阻止严格导出，不能被静默改写或修改其源文件。

AAMenuPrototype 0.0.19 的只读 native ownership 边界探针已通过：
`artifacts/acceptance-20261025-aamenu-boundary-01/native-checks.log`。在
CatalogScene 中反射确认 `Plugin`、`MenuBehaviour`、`MenuEntryHooks` 三类，
前/中/后均为 1 个活动且启用的 `MenuBehaviour`、0 个 `MenuEntryHooks`，
3 个 AudioSource（活动 3、播放 0），进程线程数均为 121，ownership 正常恢复。
探针没有打开菜单、调用窗口 API、启动音频或写入文件。该瞬时边界不能证明
故事期间菜单、user32 P/Invoke、语音/特效和双遍帧/PTS/PCM 行为，因此模块继续
保持 `Opaque`。

AAMenuPrototype probe re-run after adding process-thread equality to the state gate:
`artifacts/acceptance-20261025-aamenu-boundary-02/native-checks.log` (1/1,
SHA-256 `8D844C0AE6DC5515D3AB19D531CDEEC6A8A88CA1BDAAA8500869C7CE205EFB07`).
This run observed 122 threads consistently before/during/after, one active enabled
MenuBehaviour, zero MenuEntryHooks, and three active AudioSources with zero playing;
ownership was restored.
