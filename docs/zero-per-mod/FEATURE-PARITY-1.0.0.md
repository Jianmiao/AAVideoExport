# AAVE 1.0.0 功能等价矩阵（Stage 0）

状态：基线冻结中。每项功能必须有输入剧情、预期效果、A/B 证据路径、失败判定；未跑前不得标记 PASS。

基准：
- 0.2.12 安装：`F:\AzureArchive_101`（历史功能基准）
- 0.3.0 lab：`F:\AzureArchive_AAVE03Lab`（当前 no-op 基线）
- 1.0.0 构建：待产出，安装到独立 lab
- 主测试剧情：`E:\AAdata\data\saves\AAVE-OfficialAuthoring-20261030.aas`
- 外部观测输出：`artifacts/zero-per-mod/feature-parity-1.0.0/`

---

## 1. 角色语音

| 项 | 内容 |
|---|---|
| 输入 | 含角色语音台词的剧情段落；至少 3 个不同角色、2 种语言 |
| 预期 | 语音在对应台词帧开始；PCM 与 0.2.12 逐样本一致；无静音、无重叠、无延迟漂移 |
| 观测 | `AAVE_TRACE` audio events；导出 WAV/PCM；与 0.2.12 对齐 |
| 0.3.0 基线 | `CharacterVoiceSessionAdapter.IsProviderLoaded == false`，适配器 no-op；语音不可用 |
| 1.0.0 归属 | AudioPlane：AudioEventBus + clip 预取 + AudioRenderer 最终混音；原生旁路走 AudioEndpointCapture |
| 失败判定 | 任一语音段落缺失、错位超过 1 帧、PCM 不匹配 |

## 2. BGM

| 项 | 内容 |
|---|---|
| 输入 | 带 BGM 的场景切换与循环段落；至少 2 次淡入淡出 |
| 预期 | BGM 起止点、循环点、淡入淡出与 0.2.12 一致；PCM 逐样本一致 |
| 观测 | `AAVE_TRACE` audio events；最终 PCM；场景切换帧 |
| 0.3.0 基线 | `NativeBgmSessionAdapter.Prepare` 返回空对象；BGM 不可用 |
| 1.0.0 归属 | AudioPlane：通用 AudioSource 快照/恢复 + 循环调度 |
| 失败判定 | 缺 BGM、循环断裂、淡入淡出错位、PCM 不匹配 |

## 3. 动态背景

| 项 | 内容 |
|---|---|
| 输入 | 带视频背景的剧情场景；至少 1 次背景切换与 1 次暂停/恢复 |
| 预期 | 背景视频帧与 0.2.12 视觉一致；切换点无黑帧、无错位；导出后原场景恢复 |
| 观测 | decoded video 帧差；`AAVE_TRACE` render path；背景 Mod 命令 trace |
| 0.3.0 基线 | `ControlledBackgroundVideoAdapter.CommandTrace` 为空数组；背景不可用 |
| 1.0.0 归属 | RenderPlane：FinalCompositeCapture 直接捕获作用后画面；Present/Window 兜底 |
| 失败判定 | 背景缺失、切换黑帧、帧错位、导出后未恢复 |

## 4. MoreEffects

| 项 | 内容 |
|---|---|
| 输入 | 含画面效果/角色动画时间线的剧情段落 |
| 预期 | 效果出现帧、持续时间、结束帧与 0.2.12 一致；无残留效果 |
| 观测 | decoded video 帧差；`AAVE_TRACE` render path；效果开始/结束帧 |
| 0.3.0 基线 | `MoreEffectsCompatibility` no-op；效果不可用 |
| 1.0.0 归属 | ClockPlane 虚拟时间 + RenderPlane 最终合成；不写效果专用适配器 |
| 失败判定 | 效果缺失、时长错误、结束后残留、帧错位 |

## 5. Multilingual / 字体图集

| 项 | 内容 |
|---|---|
| 输入 | 中/英/日多语言剧情；含动态文本、姓名、选项 |
| 预期 | 文本内容、字体、字宽、描边与 0.2.12 一致；两次导出逐字节一致 |
| 观测 | decoded video 帧差；字体图集快照；文本哈希 |
| 0.3.0 基线 | `MultilingualCaptureAdapter` no-op；G2 仍有 1 LSB 字体图集差异 |
| 1.0.0 归属 | RenderPlane：FontAtlasFreeze + 超集图集预烘焙；ScenePlane 保留当前语言 |
| 失败判定 | 文本缺失/错字、字宽变化、图集重建、两次导出像素差 |

## 6. 菜单 / 启动边界

| 项 | 内容 |
|---|---|
| 输入 | 从主菜单进入鉴赏、跳过 intro、开始剧情；导出后返回菜单 |
| 预期 | 导出画面不含菜单/按钮/指针；启动流程与 0.2.12 一致；导出后菜单状态恢复 |
| 观测 | decoded video 首尾帧；`AAVE_TRACE` scene events；导出后 UI 截图 |
| 0.3.0 基线 | `AASkipIntroExportBoundary`、`RetainedMenuPlaybackBoundary` no-op；启动边界不可用 |
| 1.0.0 归属 | ScenePlane：SceneStateSnapshot + InputSuppression + UiCanvasScope |
| 失败判定 | 菜单泄漏、intro 未跳过、导出后未恢复、输入串扰 |

## 7. Effects source

| 项 | 内容 |
|---|---|
| 输入 | 依赖 effects source 输入的画面效果剧情；含至少 1 个外部效果资源 |
| 预期 | effects source 正确加载并在导出中生效；与 0.2.12 视觉一致 |
| 观测 | `AAVE_TRACE` effects source events；decoded video；资源加载日志 |
| 0.3.0 基线 | `RuntimeModBoundaries.PrepareEffectsSourceInput` 返回 null；effects source 不可用 |
| 1.0.0 归属 | RenderPlane 最终合成 + ScenePlane 依赖准备；不按 Mod 名称 |
| 失败判定 | 效果未加载、画面缺失、资源未释放 |

## 8. 立绘时钟 / 差分

| 项 | 内容 |
|---|---|
| 输入 | 含延时立绘、表情差分的剧情段落 |
| 预期 | 立绘切换帧、差分表情、延时与 0.2.12 一致 |
| 观测 | decoded video 帧差；立绘切换帧；`AAVE_TRACE` clock events |
| 0.3.0 基线 | `FlandrePortraitClock` no-op；延时立绘不可用 |
| 1.0.0 归属 | ClockPlane 虚拟时间 + RenderPlane 最终合成 |
| 失败判定 | 立绘不切换、差分错误、延时漂移 |

---

## 采集顺序

1. 0.2.12：逐项跑一次，保存视频、PCM、trace、关键帧截图。
2. 0.3.0：同样逐项跑一次，确认哪些功能实际失效。
3. 1.0.0 每个 Stage 完成后只跑受影响项；全部 Stage 完成后跑全量。
4. 每项记录：输入剧情 SHA、构建身份、帧数、音频采样数、视频 SHA、PCM SHA、关键帧 diff。

## 通过规则

- 功能存在且时间点正确；
- 视觉 A/B 在关键帧上无结构差异；
- 音频 PCM 逐样本一致；
- 两次完整导出 decoded video + PCM SHA 完全一致；
- 导出后场景、菜单、音频、Mod 文件状态完整恢复。
