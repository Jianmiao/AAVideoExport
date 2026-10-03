# AA 视频渲染导出 · AAVideoExport

在 AzureArchive 内将剧情直接渲染为视频。使用 AA 的原生演出相机逐帧渲染和混音器导出音频，不录制桌面或系统声音。

当前版本：**0.1.0 开发验证版**，构建标记 `20261003-unified-settings-accordion`。视频、音频、超分设置在同一页面，高级设置向下展开；窗口居中，内容可滚动，导出按钮固定。

## 安装：在 mods 文件夹中克隆

先退出 AA，在 **AA 安装目录的 `mods` 文件夹**打开终端，执行：

```powershell
git clone https://github.com/Jianmiao/AAVideoExport.git
```

完成后启动 AA，在模组管理中为当前 Profile 启用 **AAVideoExport**，按 AA 的提示重启。安装无需 .NET SDK、编译或移动文件。

仓库已包含可以加载的 Mod 和全部随包图形依赖，克隆后的结构为：

```text
AA 安装目录/
└─ mods/
   └─ AAVideoExport/
      ├─ 0.1.0/
      │  ├─ manifest.json
      │  ├─ AAVideoExport.dll
      │  ├─ AAVideoExport.Core.dll
      │  ├─ Vortice.*.dll / SharpGen.*.dll
      │  └─ icon.png
      ├─ src/
      └─ README.md
```

请保持目录名 `AAVideoExport`，不要再套一层 `mods`，也不要同时放入 `BepInEx/plugins`。如果已手工安装过同名目录，先将旧目录移到 `mods` 外备份，再执行克隆；不需要删除作品或 Profile。

### 使用前提

- Windows x64，AA 1.0 系列，已具备 **BepInEx 6 IL2CPP 和 ModTheAzureArchive** 模组环境。这里安装的是导出 Mod，不会替你安装或修改 AA 本体。
- 自备 **FFmpeg 与 ffprobe**，应来自同一发行版。放入系统 `PATH`，或在 AA 为本 Mod 生成的配置中设置 `[Encoder]` 下的 `FFmpeg`、`FFprobe` 路径。修改后重启 AA。未随仓库分发 FFmpeg。
- NVIDIA、AMD 或 Intel 支持的硬件编码器及驱动。面板只提供通过本机小样编码预检的选项；没有可用硬件编码器时会提示原因，不会悄悄改用 CPU。
- 部分超分路径还需要 FFmpeg 的 `libplacebo` 和显卡 Vulkan 支持；D3D11 路径需要 feature level 11.0。导出前检查实际能力，缺少依赖不会假装已启用超分。

## 使用

1. 在鉴赏列表选中要导出的剧情，点击底部工具条中新增的导出图标。备用快捷键：**Ctrl+Shift+E**。
2. 确认名称和保存位置，选择分辨率、帧率、格式、编码、码率及音频。首次保存到当前 Windows 用户的“视频”目录，之后记住上次选择的位置。
3. 点击**导出**。Mod 自动加载所选剧情、准备素材并开始离屏渲染，无需再手动进入播放页面。导出期间 AA 必须保持运行。
4. 完成后生成视频；已有同名文件不会被覆盖。中断导出会先确认，安全清理后返回设置页并保留设置。

## 可以设置什么

| 项目 | 支持内容 |
| --- | --- |
| 输出画布 | 480P、720P、1080P、1440P、4K、8K、当前窗口或自定义尺寸；横屏、竖屏、方形等比例 |
| 渲染布局 | 按目标画布重新排版渲染，不依赖实际显示器分辨率；自定义尺寸以填写的宽高为准 |
| 帧率 | 24、25、30、50、60 fps，默认 30 fps |
| 编码 / 格式 | 本机可用的 H.264、HEVC/H.265、AV1；MP4、MOV、MKV，自动限制不兼容组合 |
| 视频码率 | 可输入或拖动调整；CBR / VBR |
| 音频 | 关闭、AAC 128/192/320 Kbps、PCM 16/24 bit；44.1/48 kHz 立体声 |
| 超分 | Anime4K、Anime4K + RCAS、FSR1、双线性、双三次、Lanczos；可完全关闭 |
| 720P 超分 | 质量档内部约 600P、平衡档内部 480P，再放大到 720P；性能档禁用 |
| 画面元素 | 可选保留右上角菜单 / 自动按钮；可选导出第一帧 PNG 封面 |

原生模式按目标分辨率渲染；超分先以较低内部尺寸渲染，再重建到输出尺寸，两者不是相同的像素结果。XeSS / DLSS 尚未接入。极端比例下，素材构图仍遵循 AA 自己的适配规则。

PCM 的封装支持取决于 FFmpeg 和播放器；通用播放建议 AAC，剪辑母版可考虑 MOV + PCM。Mod 不附 AA 素材、数据库、个人工程或配置。

## 更新与卸载

关闭 AA，在克隆得到的 `AAVideoExport` 目录运行：

```powershell
git pull --ff-only
```

更新后重新启动 AA。个人导出路径等配置保存在 AA 的 Profile 中，不在 Git 仓库内。若自行修改过仓库文件，先保存自己的改动，不要强制覆盖。

卸载时先在 AA 模组管理中停用并退出 AA，再把 `mods/AAVideoExport` 移出 `mods`。这不会删除导出视频、作品或 Profile。

## 验证与限制

本版已完成 206 项纯功能回归，以及本机 AA 的单页设置、展开/收起、滚动、音频选择和 720P 超分设置验收。发布时另行检查全新克隆的目录、依赖与文件校验值，见 [发布记录](docs/handoffs/2026-10-03-github-publication.md)。

这是开发验证版，不承诺所有 GPU、AA 版本、剧情分支、冷启动音效和锁屏/最小化场景均已验收，也不承诺固定的导出倍速。NVIDIA / AMD / Intel 有对应设备与编码策略，不代表每款显卡都已实测。实验性的四槽 D3D11 读回默认关闭，保留稳定的单槽设置。

源码与测试见 `src/` 和 `tests/`；开发者参阅 [构建与验证](docs/development.md)。克隆安装只需根目录的 `0.1.0` 运行文件，**不需要运行开发测试插件**。

## 来源与许可

保留协作者的插件基础、原始控件、剧情加载、音频和导出生命周期实现，以及后续原生内录研究、性能/超分管线、设备兼容和界面改进的贡献。第三方图形库和着色器保留原作者声明。

本项目自有代码采用 [MIT](LICENSE)；具体范围、研究文件原始来源和第三方组件许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。本项目不是 AA 或上述显卡厂商的官方产品。
