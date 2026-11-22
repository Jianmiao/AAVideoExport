# AAVideoExport 1.0.0 运行期虚拟化（RVL）

普通 `Release` / `Debug` 构建默认采用单遍音画导出。1.0.0 用 Clock / Audio / Render / Scene 四个通用运行期平面替代 0.3.0 兼容接缝的实际行为；生产源码不按 Mod 程序集名、GUID、MVID、版本或文件名做身份特判。本文中的本机安装/验收状态以安装回执、`artifacts/rvl-1.0.0/g6-a47b/g6-evidence.json` 与独立报告为准。开发审计使用单独的 `DevelopmentAudit` 构建，不属于正常导出流程。

剧情、动画、特效、语音、背景音乐和视频使用同一逻辑帧时间与明确的更新/采集顺序。所选剧情只实际播放一次：播放时提交视频帧并连续记录 Unity 原生离线混音，完成后按实际剧情终点封存 PCM、排空有界 GPU/编码队列并合成电影。音频临时落盘与拷贝不会再次播放剧情。原生 AUTO、Mod 回调、局部起始偏移、增益、渐变、循环和语音等待保持原有行为；资源尚未就绪时先等待，不提前推进剧情帧。

**1.0.0 验收：G1–G6 全 PASS。** G1 为 111 个生产 `src` 文件 / 19 个已安装 Mod / 0 条身份字面量违规；G2 在同一进程连续两次完整导出均为 644 帧、1,030,400 个 48 kHz 采样帧，decoded video SHA256 `9236A756…` 与 PCM16 SHA256 `654FDD50…` 两次完全一致，不含 font-atlas 例外；G3 未知时钟 Mod 4/0、41 个时钟 patch、3,869 次 capture 读取；G4 未知 render-owner 4/0、owner 相机 depth 100 的 640×360 洋红帧 230,400/230,400 像素进入成片；G5 91 个第三方文件 / 12,531,834 B 前后 0 差异；G6 纯产品 CLI 8/8，1280×720 下 owner 洋红帧 921,600/921,600。证据根目录为 `artifacts/rvl-1.0.0/g6-a47b`，最终构建 `A47B563B6E22E6D97156F1CBB942406398B7F312517E751F7E76E2A09B39AA3D`。

**正常生产构建不运行开发程序审计、运行时证明清单、逐次重复对照，也不输出审计 sidecar 或重复基线文件。** 普通交付包不含审计 bootstrap，正常启动不需要打开审计环境变量。功能所需的资源准备、错误拒绝、取消清理和 `RenderControlV1` 协作仍保留。

## 已验证范围

- **取消与重复导出 8/8：** 冻结 R2 在独立原生实验目录完成一次取消和两份 640×360 / 30 fps / libx264 / PCM16 电影，每份 453 帧、15.1 秒、724800 个 48 kHz 音频采样帧。解码画面、音频、PTS 和实际事件一致，三次 Azurite 绑定/暂停/恢复通过。
- **原始像素边界：** 编码前原始画面并非逐字节相同；独立比较发现 35 个像素观测存在差异，最大每通道 1/255，全部受限于实际动态字体图集变更所对应的字形三角形。字体外像素仍要求完全相同，未扩大遮罩或放宽全画面阈值；最终解码画面一致。这项比较只在开发验收运行。
- **BGM 生命周期 7/7：** 实际缺失文件失败、取消后失败、无关请求隔离、随后内置音乐恢复、取消后成功请求的缓存发布顺序，以及缓存命中不额外释放均通过。
- **完整长剧情 4/4：** 一份 198 行剧情完成 1920×1080 / 60 fps / Intel `h264_qsv` / AAC 48 kHz 导出，成片 23614 帧、393.5667 秒，原生音频 18891200 个采样帧。从导出准备入口到完成回调耗时 272.5559 秒；完成后的独立媒体检查属于开发验收，位于导出计时区间之外。该运行没有前台控制，不能用来与旧版比较速度。
- 安装部署脚本测试通过 17/17，生产安装/回滚专项通过 13/13。上述实验记录不代表任意机器、Mod、剧情或编码组合的全面保证；具体本机安装状态仍须核对对应回执与报告。

## 性能结果与边界

同一规定对比组使用相同主机、其他 Mod、剧情、工具及输出设置，并验证实际前台状态。1920×1080 / 60 fps / Intel `h264_qsv` 的实际帧 60→360 窗口结果如下；这是 300 帧处理吞吐，不是完整导出速度：

| 版本 | 有效次数 | 处理帧/秒范围 | 中位数 |
| --- | ---: | ---: | ---: |
| 0.2.4 | 2 | 134.458–140.015 | 137.236 |
| 0.2.12 R2 | 3 | 120.126–126.247 | 125.090 |

**该窗口没有显示稳定吞吐提升。** 样本数量较少，不作统计显著性或普遍性能承诺。旧版不支持新版本的严格同步接口；不能把不同执行能力隐藏为完全相同的软件路径。

同一短工程的 1080p/30 完整导出，R2 一次为 12.459 秒，旧版两次为 13.081–13.809 秒；但 R2 成片为 453 帧，旧版为 736/715 帧，内容时长不同，**不能计算或宣传全程加速倍数**。长剧情功能验收和历史音频准备窗口也不用于替代该对比。历史版本数据见 [版本变更记录](CHANGELOG.md)。

## 安装包与环境

安装前核对交付清单、校验和及所用 AA 环境；本机安装/验收状态以安装回执和独立报告为准。

- 当前目标环境为 Windows x64 / AA 1.0.1、BepInEx 6 IL2CPP / ModTheAzureArchive。已测试的主机与 Mod 组合不等于任意 AA 构建或所有第三方 Mod 都兼容。
- 生产交付包包含版本化 Mod、安装/回滚脚本和专用启动脚本，**不包含 `AAVideoExport.RuntimeBootstrap.dll`**。不要把整个 bundle 直接解压进 `mods`。
- `lite-bundle` 不带 FFmpeg/ffprobe；`full-bundle` 额外包含实际编码器及其许可证。Plugin/Core 必须来自同一经过验收的冻结构建。
- 管理脚本要求 **PowerShell 7.2+（`pwsh`）**；使用已构建交付包不需要 .NET SDK。大包默认使用自身 `tools` 目录，明确配置的编码器路径仍会被尊重。

生产包结构为：

```text
交付目录/
├─ Install-Mod.ps1
├─ Rollback-Mod.ps1
├─ Deployment.Common.ps1
├─ Start-AA-AAVE.ps1
├─ delivery-manifest.json
├─ SHA256SUMS.txt
└─ AAVideoExport/0.2.12/
   ├─ manifest.json
   ├─ AAVideoExport.dll
   ├─ AAVideoExport.Core.dll
   ├─ 图形依赖 DLL、icon.png、docs、licenses
   └─ tools/ffmpeg.exe、ffprobe.exe（仅 full）
```

## 安装和正常启动

1. 完整退出 AA，将经过验收的生产包解压到独立目录。保留原版本、作品和 Profile。
2. 在该目录使用 PowerShell 7 运行安装脚本，将路径替换为实际 AA 根目录：

   ```powershell
   pwsh -NoProfile -File .\Install-Mod.ps1 -AAInstallPath '你的 AA 安装目录'
   ```

   可先加 `-PlanOnly` 查看只读计划。安装器校验完整校验和、程序集版本、生产/审计构建模式和当前 Profile；仅创建新的 `mods/AAVideoExport/0.2.12`，并将当前 Profile 中唯一的 AAVideoExport 选择改为该版本。其他 Mod、旧版本目录和其他 Mod 配置不会被覆盖；同版本目录已存在时拒绝安装。

   若旧版本留下了 AAVE 自己的审计 bootstrap，须先核对其精确 SHA256，并通过 `-ExpectedExistingBootstrapSha256` 提供该值。生产安装先按字节备份，再移除这份经过核实的旧 AAVE patcher，回滚时可恢复；不会删除或替换其他 patcher。普通生产包不安装新的 bootstrap。

3. 安装器输出安装回执、启动器和回滚脚本的位置。之后可双击 AA 根目录中的 **`Start-AA-AAVE-0.2.12.cmd`**，或从 PowerShell 7 运行：

   ```powershell
   pwsh -NoProfile -File .\Start-AA-AAVE-0.2.12.ps1
   ```

   `.cmd` 使用安装时核实的 PowerShell 7 完整路径，不依赖 PATH，不加 `Bypass`，不改变全局执行策略。启动器核对当前 Profile、Plugin/Core 哈希和构建模式，确认审计 bootstrap 不存在，并避免启动第二个 AA 实例。

   正常启动从 AA 子进程环境中清除继承的 AAVE 测试和审计开关，包括 `AA_VIDEOEXPORT_RUNTIME_RECIPES` 与 `AA_VIDEOEXPORT_RUNTIME_ATTRIBUTION`；不设置新的审计变量、不附加测试参数，也不修改用户或机器环境。生产导出功能不依赖这些环境开关。

4. 在鉴赏模式选择实际剧情，使用导出入口或 **Ctrl+Shift+E** 打开设置，先运行真实编码器检测，再选择输出目录与参数。硬件编码失败不会暗中改成 CPU 编码。

公开 `RenderControlV1` 仍为协议 v1；导出期间交接渲染设置，退出后恢复普通 AA/Mod 行为。本版不要求修改 Azurite 白名单或覆盖其文件。

## 回滚与检查

退出 AA，使用安装时输出的准确回执路径运行根目录中的版本化回滚脚本：

```powershell
pwsh -NoProfile -File .\Rollback-AA-AAVE-0.2.12.ps1 -ReceiptPath "安装器输出的 install-receipt.json 完整路径"
```

回滚先检查安装回执、原始备份和当前文件哈希，再恢复原 Profile 字节，以及安装前存在的旧 AAVE bootstrap，最后移除本次新增版本和工具。备份与恢复回执保留；备份目录还包含独立恢复脚本。若之后切换过 ActiveProfile、改过 Profile 选择或安装文件、增加未跟踪文件，回滚会停止并保留现状，不会覆盖后续工作。

只读启动检查可使用 `Start-AA-AAVE-0.2.12.ps1 -PlanOnly`；它不启动 AA。实验目录结果须与最终安装后的普通启动验收区分，不能把实验目录标记复制到主安装来绕过运行保护。

## 导出与兼容边界

- 剧情由 AA 原生 AUTO、语音、动画和效果回调推进；一次实际播放同时生成音画。取消后必须释放输入、解码器、渲染与 Mod 协作状态。
- 背景音乐准备的是实际原生资源，播放仍由原 callback/协程执行。原生解析器返回无描述符时，保留 AA 的缺失警告和原始不加载命令，不补造音乐；这不表示缺失资源已经恢复。有实际描述符但加载/解码失败仍报错；副作用不明的原生释放/调度异常明确报告恢复失败，不重复释放或伪装为可再次导出。
- 保留画布比例/偶数尺寸、可用的视频编码、VBR/CBR 和音频选项。实际组合以本机真实编码器探测为准。
- 超分保留 Anime4K、RCAS、FSR1 与已有插值算法；不提供虚假的 XeSS/DLSS 开关。
- 关闭音轨输出也不跳过原生混音时钟。MOV + PCM 适合需要无损音频的剪辑；播放器兼容性取决于实际容器与编码组合。
- 生产导出不读取旧审计重复基线来授权发布，不生成 runtime-recipes 审计 sidecar。独立测试可以严格比较重复输出，但结果仅证明对应二进制、主机、Mod 组合和输入。

## 开发、构建与来源

[构建和验证](docs/development.md) · [渲染协作协议](docs/render-control-v1.md) · [AUTO 选项机制](docs/auto-selection-export.md) · [第三方声明](THIRD_PARTY_NOTICES.md)

普通 `Release` / `Debug` 使用单遍执行路径且关闭开发审计。`DevelopmentAudit` 保留独立的旧审计/重放验证路径；只有显式选择该构建与 `-DevelopmentAudit` 打包模式，才允许携带开发用 bootstrap。生产安装不能通过启动参数切换为审计模式。

`tools/Package-Release.ps1` 从 manifest 读取版本，默认生成无 bootstrap 的生产 bundle，并校验实际 Core 的审计模式。最终二进制验收后，使用 `-SkipBuild` 和 `-BuiltAssembliesDirectory` 指向已验收的冻结文件，不在打包时重新构建。普通生产打包**不传 `-BootstrapAssemblyPath`**；脚本会拒绝该组合。该参数仅用于明确的 `-DevelopmentAudit` 开发包。

`tools/Test-ReleaseArchive.ps1` 校验完整 ZIP；`tools/tests/Test-AaveDeployment.ps1` 在合成临时文件树中验证安装与回滚。打包、哈希和脚本测试不替代最终二进制的真实 AA 导出验收。

源码构建依赖本机 AA 生成的 interop 引用；交付包不包含 AA 本体、原生宿主程序集、个人作品、配置或测试插件。FFmpeg 独立使用，来源与许可证随 full bundle 保留；图形依赖与 shader 声明保留在 `licenses` 和第三方文档中。
