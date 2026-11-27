# 版本变更记录

## 1.0.0 音频修复与确定性 G6 — 2026-11-27

- `ReloadProjectAudioOverrides()` 接回工程目录的 `SoundOverrides`；无工程目录时才使用 sidecar fallback，避免空 sidecar 覆盖已烘焙音频表。修复后 `直通线-0` 全片产品自动发布不再近静音。
- `RuntimeScenePlane.SetStoryFontText()` 从 `.aas` 提取全部可打印文本；`PrimeFontAtlases()` 在首帧前按 story 全部字体、全部字号（含 42）和 Normal/Bold/Italic/BoldAndItalic 四种 style 预热动态字体 atlas，并在 story glyph 前调用 `Canvas.ForceUpdateCanvases()`。同进程两次导出的字体 atlas 布局一致，G6 `double` 逐帧相同。
- `ExportHost` 在 owner CLI 的 `Application.Quit()` 后补 `Environment.Exit(0)`，修复 finished 后进程不退出、矩阵 runner 卡住的问题。
- 最终构建 Plugin `B2766ACEAF5F639D67CEEFCB10DC181CFA9A2D1D1408170C9D0FA130776F5B1F`（571,392 B）/ Core `98C575EB21CA12057D6285888B1734CEEFD92F49BC9F01A498AEC6CF554E0089`（386,560 B）。G6 24/24；`normal`、`reexport`、`passive`、`clock`、`double` 两份均为 `8FA18158776240F9A6D2D4BB88AB210A66905DAC61D5DE49E19C7F60CF8A1F74`、257,285,420 B、644 帧；音频 MD5 `02e4992c1d78922834d3c7a8b44d67ae`；取消 300 帧；owner 第 300 帧全屏洋红 921,600；clock 45 条事件。
- 全片 `直通线-0` 自动发布成片 `AD38A849CA21728956691313C625A92543E3B8A1F6C13F92D9B3C8FA481AE906`（16,809,543,166 B）来自音频修复构建 `44A1F8…`；最终字体构建下的全片自动发布证据尚未重新生成。

## 1.0.0 1080p 性能更新 — 2026-11-26

- 固定稿件 CLI 新增 `--aa-export-width` / `--aa-export-height` / `--aa-export-fps`，以及 encoder / codec / bitrate / software preset 控制；默认仍为 1280×720 / 30 fps / qtrle / 软件编码，旧命令行为不变。
- libx264 默认 preset 为 `superfast`；新增 `--aa-export-software-preset ultrafast` 作为显式速度档，不自动选择。两档均保持 `-threads:v 1` + `lookahead-threads=1` 的严格单线程确定性契约，分辨率、帧数、时长和音频采样不变。
- 1080p 热态同稿件结果：h264_qsv `7.269 s / 88.6 fps`；libx264 superfast `10.334 s / 62.3 fps`；ultrafast `6.799–7.067 s / 91–95 fps`（约快 34%）。ultrafast 对 superfast 的 SSIM 为 `0.9953`、PSNR 平均 `48.22 dB`、最低 `43.60 dB`；文件大小几乎一致，默认质量档不变。
- QSV VPP 的 RGBA→NV12/transpose 链在验证的 Intel 驱动上会挂起（1080p 强制实验与 2K 实测均超时）；最终改为所有 QSV 尺寸使用已验证的 CPU 预处理 + QSV 编码，不再启用 VPP。
- 其他分辨率补充：1440p 软件 superfast 全流程 `63.4 s`（26,618,618 B）、ultrafast `50 s`（26,453,433 B）、QSV `64.8 s`（10,553,451 B）；2160p 软件 superfast 全流程 `97.0 s`（52,008,122 B、644 帧）。2160p QSV 本轮在 506 帧后编码/合成失败，未通过；4K 交付以软件路径为准。
- 最终 B5 构建 Plugin `200924A296D4F0D9CDDF9B135D67B9AF739B8BCE5C67D379EEDBA8B63682615D`（568,320 B），Core `4283B2FD13DB7C027EE868E4BF2FECB10CF34F27125FB9D9C668CE6FBE81000E`（386,560 B）。
- 最终 G6 纯产品验收 8/8 场景 PASS（24/24 项检查）；G1 115 文件 / 19 Mod / 0 违规；Core 117 passed / 0 failed。详见本轮验收证据。
- 最终 Core 1080p 复验：libx264 superfast `69599A49BDF6E5FFA81B36A9E07CF2983CE888677E366D1E150343052916E11B`（13,504,583 B）、ultrafast `054420E50EDFCD1B026CC00909AD23B60AB8917F098CC6F92EBA80D2394D396D`（13,509,334 B）、h264_qsv `7515D38425C5FAD64E0044F17720213112FA77DB724BD927A943A4A09D3C042B`（7,416,909 B），三档输出 SHA 与字节均与 B5 候选一致；性能绝对值以旧 B5 热态样本为准，本轮复验仅用于最终二进制等价性。

## 1.0.0 正式 — 2026-11-25

- 最终构建 Plugin `D59D7F21721B3655E055B95FE6F997CF71E33DB25491CF8742605F9880E51142`（562,688 B），Core `C3EA604E46D13F5918DAC02C29B4F1EC83C4AF87EB4A7D5A2DE11E307CF77215`（386,048 B）。
- 动态背景改为预解码帧缓存直接绑定 `BackgroundPresenter.Layer.Image.mainTexture`；不再经过 Mod RenderTexture，不再依赖 Mod 的最后一帧写入。缓存只按 URL、帧数、尺寸和资源 basename 选择，不读取 Mod 身份。
- 背景 Mod 开启正式成片：三次独立进程、同进程连续两次、取消后全新进程重导均为 `CCCA68DC92909AD62D357D9B95315BCAB8F109542EC63232EB8A54187610FA30`，644 帧、`256,855,078 B`、`1,031,168` 音频采样；背景逐帧动态。
- 背景 Mod 关闭回归为 `BCF14DAAC69C471B048D2597ECF7443255FFF470172804C77B3857BF977A876E`，`184,811,076 B`，未泄漏缓存绑定。
- G1 PASS：115 文件 / 19 Mod / 0 violations。G2 PASS：同进程两次文件 SHA 与解码视频/音频 SHA 均一致。G3 PASS：UnknownClockMod 644 帧、45 条 clock 事件、`clock_capture_reads=3869`。G4 PASS：UnknownRenderOwnerMod 两次同 SHA、第 300 帧全屏洋红 100%。G5 PASS：91 项 before/after 0 added / 0 removed / 0 changed。G6 PASS：8/8。
- 取消项按“取消 + 全新进程重导”签署：取消 300 帧、`cancelled=true`、0 成片、0 临时文件；重导 644 帧同 SHA。
- 已知限制：`--aa-export-count 2 --aa-export-cancel-frame` 的 CLI 同进程自动重导不受支持；AA 原生 `Test` 取消后没有回到脚本开头，也没有公开 Rewind。该限制不属于产品取消/重导路径。

## 1.0.0-rc.1 — 运行期虚拟化（RVL），2026-11-22

- 四个通用运行期平面落地：`RuntimeVirtualizationHost` 协调 Clock / Audio / Render / Scene；`RuntimeCompatibilitySeams.cs` 仅保留旧类型名作为转发接缝，startup / menu / voice commit scope 均转发到实际平面，不再返回空作用域。
- 语音与 BGM 由 `RuntimeAudioPlane` 直接观察最终 mixer；循环源在导出起点回绕，一次性语音在首次可听帧把 clip 原点对齐到帧边界，解决两次导出语音相位漂移与 PCM 偶发错位。G2 两次 decoded video 与 PCM16 完全一致。
- 动态背景由 `RuntimeScenePlane` 对每个 active `VideoPlayer` 逐帧 seek，MoreEffects / 立绘 / 翻译类 canvas 保持原渲染路径；不再按 Mod 身份选择适配器。
- 标题收尾的 1–5 LSB 像素差已消除：对所有（含未激活模板）`UILabel` 的字体/字号预请求完整可打印 ASCII 字形，使克隆标题标签在首帧前已拥有确定图集；G2 明确不接受 font-atlas 例外。
- 未知时钟、未知被动、未知 render-owner 均通过通用类型契约与 Clock/Render 平面接入，不读取 Mod 身份。G3 4/0、G4 4/0、G6 8/8。
- 产品 CLI 的无值 `--aa-export-story` 仅在下一参数为 `.aas` 路径时消费；`--aa-export-output` 拒绝 `-` 开头的下一参数，修复测试插件不加载与误把 `-logFile` 当剧情路径的缺陷。
- 最终构建 `A47B563B6E22E6D97156F1CBB942406398B7F312517E751F7E76E2A09B39AA3D`；G1 111 files / 19 Mod / 0 violations；G5 91 files / 12,531,834 B / 0 diff；G6 纯产品 8/8。

## 0.3.0 — 零 per-Mod 适配重构，2026-11-22

生产代码不再针对具体其他 Mod 做身份特判，准入与拒绝按通用 API 族（时钟、媒体、渲染）观测结果与通用能力探测产生；0.2.12 时代按 Mod 身份编写的适配器退出生产源码，留存于 `legacy/0.2.12-per-mod/` 供对照。通用 Stopwatch 桥已接入生产 capture；保留单遍音画执行路径、有界 GPU 读回与编码队列、取消清理与原生场景恢复、公开 `RenderControlV1` v1。普通生产构建不运行开发审计、不携带审计 bootstrap。

- **已验收（对应门禁证据，详见 `docs/zero-per-mod/EVIDENCE-MATRIX.md`）**：
  - G1 生产源码静态扫描：106 文件 / 19 Mod / 81 身份索引，违规 0，退出码 0。
  - G2 重复导出等价性：最终批次 2 次完整导出，450 帧 / 720,000 采样帧，6 项检查通过、0 失败；音频、事件、PTS 与 authored-render 契约范围内等价；存在显式 font-atlas 例外——2 个像素、3 个通道、最大通道差 1 LSB（UI 字形），**不是逐像素完全一致，也不代表成片字节级相同**。
  - G3 未知时钟 Mod 真机导出：未知 live clock Mod 触发 `Test.Update` 事件 465 次，`Stopwatch` 读取被通用桥接为虚拟时间，导出期 32 次读取，wall 与 unity 时钟同步递增；4 项检查通过、0 失败；动态准入与桥接不依赖 Mod 名字。
- **未验收 / 部分闭合（不得对外表述为整体通过）**：
  - G4 未知 render-owner 真机拒绝未验证（NOT-RUN）：仅有合成策略拒绝与准入单测证据，无首帧前真机拒绝时点与“无错位成片”记录。
  - G5 第三方 Mod DLL / 配置前后哈希 PARTIAL：基线快照、工具自比与重扫零差异已演示，缺真实导出前 / 后的成对快照零差异比对。
  - G6 纯产品闭环尚未完成：现有真机批次由测试 harness 驱动，运行记录未同时登记 Plugin + Core 哈希；无产品-only（无 harness、无审计 bootstrap、无测试程序集）运行记录。物料层验包通过不等于纯产品运行闭环。
  - 可选 Mod（MoreEffects、Multilingual、CharacterVoice 等）在本版无真机验收记录；通用时钟桥目前只覆盖 `Stopwatch.GetTimestamp`，`DateTime` / `TickCount` / Unity `Time` 时钟族未桥接。
- **生产载荷**：固定 9 个运行时程序集（`AAVideoExport.dll`、`AAVideoExport.Core.dll` 及 Vortice / SharpGen 图形依赖），不含测试 harness、验收夹具、`RuntimeBootstrap` 审计 bootstrap、`*.pdb` 或 `*.user`。最终生产构建 Plugin `B2FF1F61…AF279`（498,688 B）、Core `E5658076…6ABCF6`（385,024 B）。
- 0.2.12 为 legacy 版本，其验收结论、成片数据与 DLL 哈希不作为本版已验证证据。

## 0.2.12 — 统一逻辑时间轴与单遍音画导出，2026-10-31

以下记录冻结的 0.2.12 R2 运行时与独立实验验收；本机安装/验收状态以安装回执和独立报告为准。

- 普通 `Release` / `Debug` 默认单遍导出：剧情、动画、Mod 特效、语音、BGM 和视频使用同一逻辑帧时间；一次原生剧情播放同时提交视频帧和连续离线 PCM，按实际结束点封存音频并合成，不再为了生成电影重放第二遍剧情。
- 普通构建不运行开发程序审计、运行时证明清单、逐次重复对照，也不产生审计 sidecar 或重复基线。`DevelopmentAudit` 保留独立的审计/重放开发路径，不能从生产安装的启动参数开启。
- 生产 `lite` / `full` bundle 不含审计 bootstrap，拒绝生产模式下的 `-BootstrapAssemblyPath`。安装器可在精确哈希确认、字节备份后移除旧 AAVE 自有审计 patcher；普通启动器清除继承的测试和审计环境变量，回滚可恢复旧状态。
- BGM 预加载与解码实际原生资源，将对应原生引用交给原 callback，保留增益、偏移、渐变与循环。自定义请求通过精确原生完成边界处理“失败但不回调”和 callback 后缓存发布；取消不靠超时丢弃仍可能发布的请求。原生调度/释放副作用不明时保留恢复故障，不重复释放。
- 修复预加载把原生无 BGM 描述符命令升级为整段导出拒绝的问题：保留原生缺失警告及原始不加载/淡出行为，不补造音乐，不改写剧情。有实际描述符但资源加载或解码失败仍报错，采集中出现未准备的新来源仍拒绝；不宣称缺失音乐得到恢复。
- 保留有界 GPU 读回/编码流水线、取消清理、原生场景恢复及公开 `RenderControlV1` v1；修正音频排空期间 GPU 读回服务、清理阶段逻辑时钟保持，以及手动渲染帧的共享时钟发布边界。
- 冻结 R2 的原生取消/重复验收通过 8/8：一次取消后两份 640×360 / 30 fps / libx264 / PCM16 成片，每份 453 帧、15.1 秒、724800 个 48 kHz 音频采样帧；解码画面、音频、PTS、实际事件一致，Azurite 三次恢复。原始画面有 35 个像素观测差异、最大每通道 1/255，受限于严格动态字形范围；不声称原始像素逐字节一致。
- 冻结 R2 原生 BGM 生命周期通过 7/7：真实加载失败、取消后失败、无关 callback 隔离、内置音乐恢复、取消后成功请求的缓存发布顺序及缓存命中所有权均通过。
- 完整长剧情功能验收通过 4/4：198 行、1920×1080 / 60 fps / `h264_qsv` / AAC 48 kHz，成片 23614 帧、393.5667 秒，原生音频 18891200 个采样帧；准备入口至完成回调 272.5559 秒。完成后的独立媒体检查在导出计时区间之外；该次运行未控制前台，不用于速度比较。
- 同条件前台窗口对比：实际帧 60→360，R2 为 120.126–126.247 帧/秒、中位 125.090（n=3）；旧版 0.2.4 为 134.458–140.015 帧/秒、中位 137.236（n=2）。没有稳定吞吐提升结论；小样本不作统计显著性承诺。
- 完整短工程：R2 为 12.459 秒（n=1），旧版为 13.081–13.809 秒（n=2）；成片分别为 453 帧和 736/715 帧，时长不同，不计算全程加速比。旧版不支持新严格同步接口，比较报告保留该执行能力差异。
- 生产安装部署脚本测试通过 17/17，安装/回滚专项通过 13/13。生产载荷不含审计 bootstrap 或测试插件，安装状态与实际使用验收以本机回执及独立报告为准。

## 0.2.11 — 性能优化，历史版本，2026-10-31

- 正式冻结二进制完成原生取消与两份完整短篇重复导出，7/7；逐帧画面、PTS、PCM、实际事件及 Azurite 恢复通过，成片 SHA256 与旧版对应参考一致。
- 同剧情、同前台状态及 1920×1080 / 60 fps / H.264 Intel QSV / 4000 kbps VBR / AAC320 / 48 kHz / 严格同步、无超分，第一遍实际帧 60–360：0.2.10 为 88.40–89.11 秒、3.37–3.39 帧/秒；正式 0.2.11 为 22.54–24.11 秒、12.44–13.31 帧/秒，观察约快 3.7–4.0 倍。系统负载波动仍适用。
- 性能数字仅来自有界音频准备窗口，随后正常取消并清理，未发布电影；不能解释为 QSV 编码吞吐、整段双遍导出加速或完整电影内容验收。
- 性能修改：以弱键保存真实 CLR 程序集的不变短名称，仍检查实时程序集清单和唯一性；帧级 recipe 验证省去诊断明细构造，保持原实时验证；对已完整验证的非动态 Harmony 委托逐回调比较原始 metadata 字节及当前绑定，提交时再次完整验证。动态 trampoline、变更历史、文件和发布保护继续生效。
- 两遍机制保留，尚未达到实时 60 帧/秒；完整用户长剧情和 Intel/AAC 最终成片未宣称验收。
- 继续保留公开 `RenderControlV1` 协议 v1、插件 GUID `halocue.aa.videoexport`、现有依赖和 bootstrap 版本 `0.1.0`。旧 `0.2.10` 版本目录保留。

## 0.2.10 — 特效来源修复，历史版本，2026-10-31

- 修复可信具体行与编辑器整体新鲜度标志分歧造成的来源拒绝；依据精确源文件及原始实际播放指令证明，不修改工程、存档或编辑器信任标志。
- 独立原生取消与重复导出验收为 7/7：取消一次后完成两份 640×360 / 30 fps / libx264 / PCM16 测试电影，每份 453 帧、15.1 秒、724800 个 48 kHz 音频采样帧，解码内容与实际事件一致，Azurite 恢复通过。
- 该历史结果仅属于对应冻结版本和夹具，不自动覆盖 0.2.11、其他编码设置或完整用户长剧情。

## 0.2.9 — 原生重复导出历史验收，2026-10-30

- 独立原生 run52 通过 2/2 背景恢复检查；run53 通过 7/7，含一次取消及两份完整电影。每份为 640×360 / 30 fps、453 帧、15.1 秒、724800 个 48 kHz 音频采样帧；解码画面、PTS、PCM 和事件一致，Azurite 每次恢复。
- 两份电影的完整 SHA256 同为 `2FB778C0A8901883CFF7DFC007A9B9A0FE46F0AB853DF51BFB29862BC96C5043`。这些结果只对应冻结的指定主机、Mod 组合与剧情，不证明全部第三方 Mod、任意 AA 构建、编码器或长剧情。

## 0.2.4 — 原生采集历史性能基线

- 原始日志记录了 1920×1080 原生采集下 119.13、126.44，末次累计 140.68 处理帧/秒；当时为 AA 1.0.0-fix、7 个 Mod、输出 30 fps。
- 该环境与后续版本不同，不是同条件 A/B。0.2.11 相对 0.2.10 的音频准备窗口改善不能解释为恢复了 0.2.4 吞吐；0.2.12 也不能据此宣称全程提速。最终比较须使用相同主机、Mod、故事、帧率、分辨率与编码器，并覆盖完整导出耗时。
