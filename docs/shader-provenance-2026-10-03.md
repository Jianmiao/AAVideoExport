# GPU 放大着色器来源与派生

这些文件从此前用户研究目录 `native-export-probe/upscale-shaders` 接入，随 Core DLL 作为资源嵌入。许可证正文保留在每个着色器文件头，运行包同时包含本文与 THIRD_PARTY_NOTICES。

- Anime4K v3.2 Upscale Denoise CNN x2 L，上游 [bloc97/Anime4K](https://github.com/bloc97/Anime4K)，[原文件](https://raw.githubusercontent.com/bloc97/Anime4K/master/glsl/Upscale%2BDenoise/Anime4K_Upscale_Denoise_CNN_x2_L.glsl)。2026-10-02 研究快照的原文件 SHA256：`6CC4604C9544FD4FD9E3A75FD797511BF5FE1E626E9E1DBCEE03302823A63207`。MIT，Copyright 2019–2021 bloc97。派生 `AA_Anime4K.glsl` 新增说明并将十个 pass 的触发倍率 `1.200 >` 改为 `1.000 >`，其余权重与实现保留。不是未修改的上游文件。
- FSR1 v1.0.2 EASU + RCAS，agyild 的 mpv 亮度通道移植版，[固定镜像](https://gitea.suda.codes/sudacode/mpv/raw/commit/7904708a42d8a7ea266ce4d52778fa68e7a2381f/shaders/FSR.glsl)。原 SHA256：`56D8597FC6B7BF6D13F8C3B2BDF1CDC43B06175D51746AAB44CF1DCA16929B9E`；`AA_FSR1.glsl` 内容不改。AMD MIT 许可全文嵌入。这是 LUMA 版，不是 Magpie 的完整 RGB EASU。
- `AA_RCAS.glsl` 是 RGB RCAS 的 AA GLSL 适配，数学来源为 [AMD ffx_fsr1.h 固定提交](https://github.com/GPUOpen-Effects/FidelityFX-FSR/blob/a21ffb8f6c13233ba336352bdff293894c706575/ffx-fsr/ffx_fsr1.h)，保留 AMD MIT 许可。使用五点邻域、0.1875 lobe 限制、噪声抑制和直接 0–1 强度参数；为恒定黑白区域增加分母保护。强度语义参照截图中的 Magpie RCAS，但执行后端为 libplacebo / Vulkan，不宣称与 Magpie FP16 像素完全相同。

Anime4K → RCAS 在本次独占临时目录生成合并着色器：先运行 Anime4K 的 MAIN passes，最终尺寸 SCALED hook 执行 RGB RCAS。锐度 0 时只生成 Anime4K 内容，精确绕过锐化。滤镜中没有用普通插值替换所选模型；实际帧哈希对照验证模型与 RCAS 的启用。

技术依据：[FFmpeg libplacebo 文档](https://ffmpeg.org/ffmpeg-filters.html#libplacebo)、[AMD FSR1](https://gpuopen.com/fidelityfx-superresolution/)、[Intel XeSS 输入要求](https://www.intel.com/content/www/us/en/developer/articles/technical/xess-sr-developer-guide.html)。XeSS／DLSS 未完成接入，不作为可用模型。
