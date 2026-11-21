# 构建、验证与发布

本文对应 0.2.12 单遍导出。普通用户的生产 bundle 安装与回滚方式见 [README](../README.md)，不要把完整 bundle 直接解压进 `mods`。构建、打包、原生实验和实际安装属于不同验证范围；本机安装/验收状态以安装回执和独立报告为准。

## 构建模式

准备 .NET SDK，以及自己的 AA / BepInEx 6 IL2CPP 引用。先正常启动一次 AA，生成 `BepInEx/interop`。宿主引用、素材和个人配置不能进入仓库或交付包。

```powershell
dotnet build src/VideoExport.Plugin/VideoExport.Plugin.csproj -c Release -p:AAInstallPath='你的 AA 安装目录'
```

也可复制 `local.props.example` 为 `local.props` 并配置路径；真实 `local.props` 已忽略。图形依赖版本固定在 Core 项目中，分发时保留第三方声明。

- 普通 `Release` / `Debug` 默认使用单遍音画执行路径：一个原生剧情播放、统一逻辑帧、连续原生 PCM、有界 GPU 读回和编码队列。
- 普通构建不运行开发程序审计、证明清单、每次导出的重复对照，不产生审计 sidecar 或重复基线，不需要 bootstrap 或审计环境开关。
- `DevelopmentAudit` 是独立开发配置，保留旧审计/重放验证路径。使用 `-c DevelopmentAudit` 构建；只有显式 `-DevelopmentAudit` 打包、验包和启动才允许该模式，不能从生产安装动态启用。

具体编译分支以 `src/VideoExport.Core/ExportBuildProfile.cs` 为准。不要用旧文档中的双遍流程描述普通生产导出。

## 功能与接口检查

```powershell
pwsh -File tools/Test-PortableReview.ps1
pwsh -File tools/Test-CloneInstall.ps1
pwsh -File tools/Test-RenderControlContract.ps1 -AAInstallPath '你的 AA 安装目录' -AssemblyPath src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.dll
```

这些功能测试不启动 AA。RenderControl、Host、Audio 与 BGM 源码测试通过宿主替身覆盖协作、音画时间轴、资源所有权、取消与故障边界；不能代替完整原生导出、同步、画质和真实 Azurite 绑定/恢复验收。MoreEffects 兼容测试在没有宿主 Harmony DLL 时使用固定版本 HarmonyX NuGet，不需要分发 AA 引用。

原生测试插件仅用于隔离实验目录，不能进入生产包或普通启动参数。测试应覆盖单次剧情播放、完整帧/音频长度、重复输出、取消后再次导出、BGM 成功/失败/缓存所有权，以及真实 Azurite 主机识别、绑定、暂停和 FPS/VSync 恢复。资源超时不能用来丢弃仍可能回调的原生工作；副作用不明的释放失败不能盲目重试。

## 冻结与生产打包

指定 Plugin/Core 二进制完成原生验收后，从同一冻结目录打包。下面的路径需要替换为实际经过验收的目录；打包完成只证明载荷身份，不代替本机运行验证。

```powershell
pwsh -File tools/Package-Release.ps1 -AAInstallPath '你的 AA 安装目录' -Variant full -SkipBuild -BuiltAssembliesDirectory '经过验收的冻结 Plugin/Core 目录' -FFmpegDirectory '已核验 FFmpeg 的 bin 目录' -FFmpegNoticesDirectory '包含 LICENSE.txt 和源码来源说明的目录' -OutputDirectory artifacts/release
pwsh -File tools/Test-ReleaseArchive.ps1 -Archive artifacts/release/AAVideoExport-0.2.12-win-x64-full-bundle.zip -Variant full
```

版本从 `manifest.json` 动态读取；示例文件名对应当前 0.2.12。`-SkipBuild` 防止打包时改变已经验收的文件。普通生产模式不传 `-BootstrapAssemblyPath`，脚本会拒绝该组合。开发审计包须显式传 `-DevelopmentAudit`，使用独立输出目录与 `-development-audit-bundle.zip` 文件名；该模式才允许指定开发 bootstrap。

bundle 根目录包含安装、启动、回滚脚本、交付清单、校验和及说明；Mod 载荷位于 `AAVideoExport/0.2.12/`。生产包没有 `bootstrap/` 载荷。`full` 另含 `AAVideoExport/0.2.12/tools/ffmpeg.exe`、`ffprobe.exe` 及许可证；`lite` 使用 `-Variant lite` 且不传 FFmpeg 目录。两种包的 Mod 功能一致。

打包脚本校验实际 Core 审计模式与请求模式一致。验包脚本检查载荷身份、完整校验和、路径及禁入内容。生产安装器只创建新版本目录、修改当前 Profile 的 AAVE 选择；若旧 AAVE 审计 bootstrap 存在，须核实其精确哈希并备份后移除。普通启动器清除继承的测试和审计变量；回滚恢复原 Profile 及已备份的旧自有 patcher。其他 Mod 和配置不属于此次写入范围。

仅修改 `src/` 不会更新冻结 DLL 或已安装版本。发布时须核对 Plugin/Core/manifest 版本、实际打包哈希、最终原生证据、安装回执和回滚文件。不要把旧 `UnifiedTimeline` 构建或旧版性能结果改名当作新版本验收。

## 冻结 R2 证据与测量边界

2026-10-31 的 0.2.12 R2 运行时身份为：

- Plugin SHA256：`47DF9237E6891121E97C96ADB59EC83212FA8F5C471B12DF39DB25083B958021`。
- Core SHA256：`F728E74DA722B803EF5FF4367E4D7A2CA9F3E75038292EB702B3B464C72BCFCC`。

原生短工程取消/重复验收 8/8，自定义 BGM 生命周期 7/7；长剧情功能验收 4/4。短工程两份成片解码像素、音频、PTS 和事件一致；编码前原始像素有 35 个观测差异、最大每通道 1/255，只满足严格动态字体图集字形范围内的例外规则，不能描述为原始像素完全一致。字体范围外仍须完全一致，例外比较只存在于独立开发测试。

长剧情为 198 行、23614 帧、393.5667 秒、1920×1080 / 60 fps / Intel `h264_qsv` / AAC 48 kHz，原生音频 18891200 个采样帧。从原始 `Prepare` 到 `ReportCompleted` 的导出区间为 272.5558842 秒；后续独立媒体检查不计入该导出区间，生产导出也不运行这项开发检查。该次长运行未控制前台，只用于功能验收，不作为性能比较。

有效前台窗口在实际 Capturing 开始后取得本进程前台，帧 60 边界要求原生窗口归属与 Unity focus 同时有效，并持续检查至帧 360；准备和预热不属于该窗口。每次测量 300 个采集/接收/写入帧后取消，不生成电影。R2 为 120.126–126.247 帧/秒、中位 125.090（n=3），原版 0.2.4 为 134.458–140.015 帧/秒、中位 137.236（n=2）；没有稳定吞吐提升结论。

前台受控的完整短工程测量覆盖原始 `Prepare`→`ReportCompleted`：准备/就绪、采集/排空、合成/发布三个相邻区间之和才是全程耗时，不能另加边界重叠的 session 计数。R2 为 12.459 秒（n=1），旧版为 13.081–13.809 秒（n=2），但成片为 453 帧与 736/715 帧，不能据此计算相同内容的加速比。对比组核对主机、其他 Mod、剧情、选项、工具、实际编码器和前台合同；旧版不支持新严格同步接口，这一能力差异保留在报告中。前台失败和其他被排除观察不进入统计。

生产部署脚本测试通过 17/17，安装/回滚专项通过 13/13。独立实验包含测试观察器；测试插件不进入交付包或普通启动。实验结果与本机安装验收分别记录，不以实验目录标记、脚本通过或媒体探测代替普通 UI 实际使用证据。详细性能测量以独立 R2 报告及其 JSON 为准。

## 固定协作接口与发布边界

渲染协作接口及顺序见 [render-control-v1.md](render-control-v1.md)。后续发布保留公开 `RenderControlV1` v1；UI、编码参数和内部变量可以变化，协议主版本内含义不能变化。合作方凭协议握手，不凭 DLL 哈希；实际兼容验收还须证明当前安装和运行状态。

不分发 AA 本体、interop、数据库、个人素材/工程/Profile、测试插件、测试日志或凭据。保留第三方来源与许可证；FFmpeg 分发应保留对应源码和构建来源资料，不把上游构建配方当作完整源码，也不宣称工具版本足以逐字节复现。性能测试替身不等于所有显卡实测。
