# 构建与验证

普通用户直接克隆即可安装，不需要 SDK。以下只供开发者使用。

## 构建

安装 .NET SDK 和 .NET 6 运行时，准备自己的 AA / BepInEx 6 IL2CPP 环境。
先正常启动一次 AA，使本机生成 `BepInEx/interop` 引用。宿主引用不会打入发布包。
复制 `local.props.example` 为 `local.props`，填入自己的 `AAInstallPath`，或在命令中明确指定：

```powershell
dotnet build src/VideoExport.Plugin/VideoExport.Plugin.csproj -c Release -p:AAInstallPath='你的 AA 安装目录'
```

NuGet 图形依赖版本在 Core 项目文件中固定；分发它们时保留 `docs/third-party` 中的声明。源码中没有 AA 的实现代码、生成 interop 或素材。

## 纯功能测试

```powershell
pwsh -File tools/Test-PortableReview.ps1
pwsh -File tools/Test-CloneInstall.ps1
```

前者运行原有 17 组、206 项纯功能检查，以及 14 项计时适配检查，不启动 AA、不渲染作品、不运行性能测试。计时适配测试默认恢复固定版本的 HarmonyX；提供 `AAInstallPath` 时优先使用本机宿主的 Harmony。
后者检查克隆安装目录、预编译组件、版本与 SHA-256。结果不能替代 AA 原生画面和音画同步验收。
媒体 / GPU 测试及显式启用的 AA 测试插件分别见各测试项目 README。

## 更新运行文件

`0.1.0/` 是普通用户实际安装的版本。仅修改 `src/` 并不会更新安装版。
完成编译与所需原生验收后，更新该目录内两个 Mod DLL、七个图形依赖 DLL、manifest 和图标；保留声明，并刷新 `SHA256SUMS.txt`。
运行克隆校验和纯功能回归，通过后一起提交源码与匹配的运行文件。不要用 Git LFS 指针替代这些小型 DLL。

`tools/Package.ps1` 用于生成传统 ZIP 安装包；本仓库的直接克隆安装不需要执行它。
离线构建可使用 `tools/Build-Offline.ps1`，必须提供自己的编译器、运行时与宿主引用路径。

## 发布边界

禁止把本机的 AA 本体、`BepInEx/interop`、数据库、人物/声音素材、私人工程、Profile、导出视频、测试日志或凭据提交到此仓库。
不得把模拟测试结果称为原生运行验收，也不得把单台机器结果推广为三家 GPU 全型号实测。
