# 受控运行时续接清单 — 2026-10-30

**Goal 仍 active；本轮没有生成一部全 Default Mods 电影，不能宣称整体完成。**
目标不变：不关闭或遗漏当前 Mods 的效果，统一控制帧、音频及事件；真实重复电影通过解码内容/PCM/PTS/事件比对、取消恢复、源资源不变和 Azurite RenderControlV1 v1 交接。

## 当前工作区与保护边界
- 主源码：`E:\aamod\AAVideoExport-0.2.4-source`；来源分支仅参考，不把旧分支实测当当前候选验收。
- 主安装：`F:\AzureArchive_101`，本轮未向其 staging。当前主 AAVE/Core 仍为原 0.2.8；不要使用旧记忆中的 100_fix 路径。
- 实验副本：`artifacts/native-lab-20261029-bootstrap`，ActiveProfile=Default；16 个配置项、15 个实际 Mod，缺失的“芙兰的AA自定义入场动画/1.0.0”是原状态。
- lab 与主安装 modconfig 字节相同，SHA `549F74941E247B545747B331E1C713F795C1F26AA96BFFEC77343F76AB005C54`；未减少 Mod 集合。
- `aave-test-lab.json` 的 Provider/Core/Fixture hash 是 **2026-10-29 创建副本时的历史快照**，未重写；当前身份以下表及 `artifacts/continuation-20261030/current-state.json` 为准。
- 仅 lab：`rukari.charactervoice.cfg` 的 PostPlaybackDelaySeconds=0.1；MoreEffects WorkspaceRoot 已改为 lab 自己的 `profiles/Default/configs/AzureArchive.VideoTools/workspaces`。
- **原生运行必用 `tools/Run-GuardedNativeAcceptance.ps1`**。AA 自身仍共享 LocalLow 用户设置，不能只靠 lab 路径隔离；禁止并发 AA。
- 最终复核：AA 进程=0；UserSettings、三个备份、原临时设置和 Sfx config 六文件均匹配还原 SHA；typed PlayerPrefs 与最后快照相同；`voiceVolume=0`，UserSettings SHA `750578DAF16694EB9750BFF62A01C1CD657F5EC2C0AB6C4377577A81B2B781C7`。

## 当前已 stage 候选身份
| 文件 | SHA-256 |
| --- | --- |
| lab `mods/AAVideoExport/0.2.8/AAVideoExport.dll` | `E5F135CCC88CAE36EBB7ED78A1EC966AAACD1564CB33C7FF94DA12A8160C1B26` |
| 同目录 `AAVideoExport.Core.dll` | `3039565917BB263F7930DD8BFDEB455B0220B0B811A6F7D176727FC3852E6AAB` |
| lab `BepInEx/plugins/AAVideoExport.NativePath.Tests.dll` | `8531DB18011B3B094CDC5F84DA16F67732560A12DC3B5D92FA210A4D50C1B358` |
上述三项与当前 build 输出逐字节 hash 相同，不把当前 hash 回填到早期旧候选运行。
主安装 AAVE=`27AE30AE26877C7A6330FF1C46990C6578BC29B28DCA420F5C901F08E330C452`，Core=`4747D708BAD25128681D7E7CC2EDE7097C57F58B5FD6F73A306D40A486578266`。
主 Azurite 仍为开轮时 `FEE95B5E34863DE3F858939B88A1E08820FC42C2772D8A7534557EA6DCA551C7`；1.0.39 目录内实为 1.0.38 candidate，原清单差异未擅改。

## 本轮已落地的代码
- 配音：Plugin/Host 接上 authored-delay-only 生命周期；不改 loading/start/playback wall watchdog。EndExport 的“恢复仍 pending 却返回”缺口已修；真实 Gate/Harmony **20 tests**。见 `artifacts/voice-clock-20261029/lifecycle-wiring-verification.md`、`artifacts/continuation-20261030/voice-end-recovery-{red,green}.log`。
- 背景：PTS/frame store、原层过渡/纹理、native scaled-wait、背景命令身份双遍检查已迁入；补齐所有 Harmony 类别/owner 检查和可重试 cleanup。见 `docs/background-migration-20261030.md`。
- 翻译/Rukari/AASkip：原翻译/字体/布局按帧 pump，保留同帧新队列；共享库只控制已验证 idle surfaces，native resource queue 继续；AASkip 用 retained live proof。旧 Voice 全 Runtime.Now 改写与不在当前集合的 TitlePage 分支未迁入。见 `docs/scoped-runtime-migration-20261030.md`。
- 三 Example：实际 exact DLL 的空 Load 元数据证明接入 **Observed retained scope**；跨双遍保持同 receipt、帧内查 patch/依赖、提交重哈希；失败不能换 receipt 掩盖。见 `artifacts/static-inert-integration-20261030/verification.md`。
- 电影终验：每帧原生像素格式 SHA/有理 PTS TSV + 全 PCM16 SHA；单像素/单采样改变会失败，同内容 remux 可通过。真实媒体 **9/9**，尚未用于本轮全 Mod 电影。见 `docs/decoded-media-repeat-acceptance-20261030.md`。
- Guard：原字节设置/Sfx/typed PlayerPrefs 先恢复，采证失败不阻断核心恢复；仅清理自己启动的进程句柄，新增设置移入诊断；临时异常回归 **5/5**。见 `artifacts/guard-20261030/verification.md`。

## 本轮 native 证据（直接复读 run/result/restoration）
下列目录均在 `artifacts/`；全部 exitCode=0、timedOut=false、Restored=true、RegistryRestored=true，restore/evidence errors 为空。
| 目录 | 结果 | 实际范围 |
| --- | --- | --- |
| `acceptance-20261030-05-integrated-handoff` | 15/15 | settled Catalog ownership；支持/绑定成功，FPS=-1、VSync=1、tier=1 还原 |
| `acceptance-20261030-06-background-native` | 1/1 | 原背景 callback 首帧、loop/rebind/retry/restore |
| `acceptance-20261030-12-multilingual-native` | 1/1 | 原 AUTO→“自动”效果，在接管、重试及恢复前后保留 |
| `acceptance-20261030-13-voice-auto-policy` | 51/51 | 旧候选的 30fps 配音 probe，保留为阶段证据 |
| `acceptance-20261030-16-voice-60fps-zero-render` | 52/52 | 60fps；28 个零 sample render ticks 正确处理 |
| `acceptance-20261030-17-voice-24fps-restoration` | 52/52 | 24fps；原 gate/cancel/Azurite 恢复 |
| `acceptance-20261030-18-voice-muted-native` | 58/58 | 原静音前置，两个 attempt peak=0，预期 silent-user-voice-level-zero |
| `acceptance-20261030-19-voice-30fps-final` | 52/52 | 最终 30fps 有声 probe，两个 attempt peak≈0.71366316 |
配音证据 JSON 均 Passed/CleanupOk=true；13/16/17/18/19 的 PCM 文件 SHA 都与 JSON 相符，cleanup 后 renderingOwned=false、providerReady=true。
配音仍是实际 Runtime/Gate/SetVoice/preload/callback/AudioRenderer 的独立 probe，**不进入 ExportHost，不认证 cold-load 帧身份，也不生产或授权 strict movie**。
静音根因是用户真实 `voiceVolume=0`，不是导出器吞音；有声与原静音条件分别验证，最终继续保留原值 0。
AUTO-only 阶段曾错误地在 AUTO 暂停时断言等待；改为原生 AUTO 条件并验证原 policy，非改生产门控。60fps zero-render 与 Azurite 恢复条件轮询也属于夹具纠正，旧 red 记录保留。
真实资源为 `aavt/voice/SE_BatSwing_01`，解析到 `E:\AAdata\data\overrides\sounds\SE_BatSwing_01.wav`；native owner 使用现有 `日奈的work all day-0.aas`，不是已编排全效果的新电影样本。

## 最终回归
- `artifacts/portable-20261030-integrated-final/portable-results.json`：**23 组、360 PASS，全部 exit 0**，PerformanceTests=false。
- `artifacts/continuation-20261030/targeted-final/results.json`：**9 组全部 exit 0**；依次 Voice20、Video8、Background15、ScopedRuntime14、StaticInert23、PluginLifecycle8、Startup28、StrictCapability22、DecodedMedia9。
- 首轮补了 StoryPath source-link 的 StaticInertScope 缺项及共享 fake EndFailure 初始化，再完整复跑到绿；不是放宽断言。Host 本轮 69/69 红绿与身份记录保存在 static-inert-integration 目录。
- 精确 run/PCM/hash/还原比对及当前身份已汇总 `artifacts/continuation-20261030/current-state.json`；它是续接入口，不取代各原始日志。

## 仍未完成与最短下一阶段
1. CharacterVoice 的 authored delay probe 已过，但整条加载/音频事件/剧情两遍尚未形成生产 scoped proof；现有整链准入仍拒绝，不能只因 probe 成功改成 Certified。
2. MenuBgmSelector、AAMenuPrototype、SfxFolderMod 的实际播放/资源/线程边界还需具体接管或持续 idle proof；不能禁用它们来缩小目标。
3. Anonymous holder 不可整体允许：当前 94 个 Interop factory 实例需实际 target/redirect/ctor 递归证据；无 T 约束、可写 redirect 等缺口见 `docs/anonymous-factory-scope-20261030.md`。
4. 先补上述有限 controller/retained proof 和稳定 audio/event 身份；不要反复跑已知拒绝，也不要以开 tracing 布尔值代替控制。当前 cache reuse、audio/story tracing 三个生产开关仍 false。
5. 在独立素材/剧情副本中编排真实语音、动态背景、More Effects、Portrait 和翻译等当前效果，保留 Default 15 Mods；guard 下完成真实双遍、第二次电影、取消重试，逐帧事件/解码内容/PCM/PTS、源资源和 Azurite 交还共同通过后再评估交付。

保留事项：自动审批曾拒绝清理 `C:\Users\21294\AppData\Local\Temp\aave-static-inert-1385bd8ee09e43f8816ae320f02a0ed1`（7 个测试变异 DLL）；未绕过、未触用户 Mod。完整目标仍未达到，不 mark complete。
