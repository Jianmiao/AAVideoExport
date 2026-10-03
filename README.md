# AAVideoExport 0.2.0

AzureArchive 的原生逐帧视频导出模组。直接渲染剧情相机、采集 Unity 离线音频并交给硬件编码器，不录制桌面。

## 下载和安装

1. 退出 AA，从 [Releases](https://github.com/Jianmiao/AAVideoExport/releases/latest) 选择下表中的一个安装包。不要下载 GitHub 自动生成的 Source code ZIP，它是源码。
2. 将压缩包内容直接解压到 **AA 本体的 mods 文件夹**。
3. 启动 AA，在模组管理器中选择 **AAVideoExport 0.2.0** 并启用，按 AA 提示重启。

| 安装包 | 内容 | 适用情况 |
|---|---|---|
| **AAVideoExport-0.2.0-win-x64-lite.zip（小包）** | Mod 和图形依赖，不含 FFmpeg/ffprobe | 已有可用编码器的电脑，沿用之前的小包安装方式 |
| **AAVideoExport-0.2.0-win-x64-full.zip（大包）** | 同一份 Mod，额外附带 FFmpeg/ffprobe | 不想单独准备编码器，解压后自动使用包内工具 |

只需下载其中一个。两个包的 Mod 功能和 DLL 完全相同，区别只有编码器及其许可/来源文件。小包缺少可用编码器时须配置 FFmpeg 或改用大包。

解压后的目录应为（tools 仅大包包含）：

```text
AA 本体/
└─ mods/
   └─ AAVideoExport/
      └─ 0.2.0/
         ├─ manifest.json
         ├─ AAVideoExport.dll
         ├─ AAVideoExport.Core.dll
         ├─ 图形依赖 DLL
         ├─ icon.png
         ├─ tools/
         │  ├─ ffmpeg.exe
         │  └─ ffprobe.exe
         └─ licenses/
```

**不要再套一层 mods 或压缩包名称文件夹，也不要放到 BepInEx/plugins。** 升级时可以保留旧的版本目录，在管理器中切换到 0.2.0；不要同时启用两份导出插件。不需要删除作品或 Profile。

两个包都无需安装 SDK 或编译。大包已附 FFmpeg、ffprobe，无需配置 PATH。FFmpeg 作为独立程序使用，许可证和来源随大包保留。大包首次运行使用包内工具，小包使用 PATH 或配置中的工具；已有配置若明确指定其他编码器路径，仍尊重该设置。要切回大包内工具，将配置中的 FFmpeg、FFprobe 分别设为 `ffmpeg.exe`、`ffprobe.exe`。

## 环境和使用

- Windows x64、AA 1.0 系列及其正常工作的 BepInEx 6 IL2CPP / ModTheAzureArchive 模组环境。安装包不包含 AA 本体或素材。
- 需要当前显卡驱动支持的硬件编码器。程序实际探测 NVIDIA NVENC、Intel QSV、AMD AMF；没有可用硬件时会提示，不会自动改成 CPU 编码。
- 在鉴赏模式选择剧情，点击导出入口，或按 **Ctrl+Shift+E** 打开设置。选择输出目录和参数后开始导出。
- 首次默认保存到当前用户的“视频”目录；会记住上次选择。包内不包含开发者个人路径或配置。

## 主要功能

| 项目 | 内容 |
|---|---|
| 分辨率和比例 | 常用分辨率、横竖屏比例、当前窗口及自定义偶数尺寸；按输出画布排版 |
| 视频 | 24/25/30/50/60 fps；本机可用的 H.264、HEVC、AV1；MP4/MOV/MKV |
| 码率 | 默认 4.0 Mbps VBR，可选 CBR，面板输入及滑条调节 |
| 音频 | 关闭、AAC、PCM；44.1/48 kHz 立体声，使用剧情离线混音 |
| 超分辨率 | 可选 Anime4K、Anime4K + RCAS、FSR1、双线性、双三次、Lanczos；不提供虚假的 XeSS/DLSS 开关 |
| 控件与封面 | 选择是否保留播放按钮，可生成首帧 PNG |
| 流程 | 沿用 AA 剧情、Auto、语音和动作时序，支持取消和资源恢复 |

超分、像素格式、分辨率和编码可用性仍取决于硬件与驱动；包含 FFmpeg 不等于所有显卡支持全部选项。MP4 + PCM 的播放器兼容性有限，通用播放建议 AAC，剪辑可考虑 MOV + PCM。

## 0.2.0 更新

- 新增固定公开渲染协作接口 **RenderControlV1**，与 Azurite 0.7.0 及以后兼容实现交接。内部变量改名、UI 或编码功能更新不再要求逐个 DLL 适配；接口版本和语义仍须保持一致。
- 导出接管前通知优化模组恢复状态，全部状态恢复后才交还；交接失败会停止接管，防止保存错误快照。
- 保留新版单页设置、高级选项展开、路径记忆、硬件导出和可选超分。
- 包含“更多的画面效果”动作计时适配。具体支持范围和外部触发限制见 [兼容说明](docs/moreeffects-clock-compatibility.md)。该模组本身不随包分发。
- Release 同时提供小包和包含编码工具的大包，均可直接解压到 mods；自动从模组目录查找包内工具。

本轮完成构建、协议/恢复、路径解析和功能回归；没有将模拟测试称作完整 AA 成片验收，也不承诺三倍导出速度或所有显卡的性能。

## 开发与来源

[构建和验证](docs/development.md) · [渲染协作协议](docs/render-control-v1.md) · [第三方声明](THIRD_PARTY_NOTICES.md)

仓库保留可克隆安装的预编译 Mod DLL；直接克隆不含较大的 FFmpeg 程序，需要可用的外部工具。普通用户可选择上述两个 Release ZIP 之一。
