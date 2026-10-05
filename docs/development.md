# 构建、验证与发布

普通用户从 Release 下载完整安装 ZIP，直接解压到 AA 的 mods 文件夹；本页供开发者使用。

## 构建

准备 .NET SDK，以及自己的 AA / BepInEx 6 IL2CPP 引用。先正常启动一次 AA，生成 BepInEx/interop。宿主引用、素材和个人配置不能进入仓库或 Release。

```powershell
dotnet build src/VideoExport.Plugin/VideoExport.Plugin.csproj -c Release -p:AAInstallPath='你的 AA 安装目录'
```

也可复制 local.props.example 为 local.props 并配置路径；真实 local.props 已忽略。图形依赖版本固定在 Core 项目中，分发时保留第三方声明。

## 功能与接口检查

```powershell
pwsh -File tools/Test-PortableReview.ps1
pwsh -File tools/Test-CloneInstall.ps1
pwsh -File tools/Test-RenderControlContract.ps1 -AAInstallPath '你的 AA 安装目录' -AssemblyPath src/VideoExport.Plugin/bin/Release/net6.0/AAVideoExport.dll
```

功能测试不启动 AA。RenderControl、Host、Audio 测试覆盖真实协作与恢复代码，通过宿主替身验证；它们不能代替完整原生导出、音画同步和画质验收。

MoreEffects 兼容测试在没有宿主 Harmony DLL 时使用固定版本 HarmonyX NuGet，CI 不需要分发 AA 引用。

## 便携 Release ZIP

```powershell
pwsh -File tools/Package-Release.ps1 -AAInstallPath '你的 AA 安装目录' -Variant full -FFmpegDirectory '已核验 FFmpeg 的 bin 目录' -FFmpegNoticesDirectory '包含 LICENSE.txt 和源码来源说明的目录' -OutputDirectory artifacts/release
pwsh -File tools/Test-ReleaseArchive.ps1 -Archive artifacts/release/AAVideoExport-0.2.3-win-x64-full.zip -Variant full
```

ZIP 的唯一顶层是 AAVideoExport/，其下为 0.2.3/；禁止再加 mods/ 或开发包名称。工具固定放在 0.2.3/tools/，插件默认自行查找。两个包均包含九个 Mod/图形 DLL、manifest、图标、校验和、说明和许可证；full 大包另外包含 FFmpeg/ffprobe。不包含测试插件、interop、本机配置、工程或素材。

小包使用 `-Variant lite` 打包并校验，不传 FFmpeg 目录。两个包的 Mod 功能一致，只选其一安装。

Git 中的 0.2.3/ 保留克隆安装需要的小型 DLL，不提交大型 FFmpeg EXE。完整 Release 另外附带 FFmpeg，必须同时发布对应源码和构建来源资料。不要把上游构建配方误当作完整 FFmpeg 源码，也不要声称构建工具版本可实现逐字节复现。

仅更新 src/ 不会更新预编译的 0.2.3/。发布前同步版本常量、csproj、两个 manifest 和该目录的 SHA256SUMS.txt。旧版本可以保留，用户在管理器中选择新版本。

## 固定协作接口

渲染协作接口及顺序见 [render-control-v1.md](render-control-v1.md)。后续发布必须保留 RenderControlV1；UI、编码参数和内部变量可以变化，协议主版本内的含义不能变化。合作方凭协议握手，不凭 DLL 哈希。

## 发布边界

不提交 AA 本体、BepInEx/interop、数据库、人物和音频素材、私人工程、Profile、导出视频、测试日志或凭据。保留第三方贡献来源和许可证，不将性能测试替身当作所有显卡实测。
