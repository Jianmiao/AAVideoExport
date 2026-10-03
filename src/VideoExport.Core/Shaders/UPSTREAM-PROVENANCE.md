# Shader provenance

> This is the original research provenance record. The AA integration derives AA_Anime4K.glsl by changing activation thresholds and adds AA_RCAS.glsl; see docs/shader-provenance-2026-10-03.md. The original hashes below refer to the research inputs, not all files in this directory.

Downloaded 2026-10-02 for isolated offline comparisons. Files are unmodified.

- Anime4K CNN x2 Denoise L: https://raw.githubusercontent.com/bloc97/Anime4K/master/glsl/Upscale%2BDenoise/Anime4K_Upscale_Denoise_CNN_x2_L.glsl
  - SHA256: 6CC4604C9544FD4FD9E3A75FD797511BF5FE1E626E9E1DBCEE03302823A63207
  - MIT license, embedded copyright; upstream LICENSE included.
  - Magpie installed HLSL header identifies this GLSL algorithm as its upstream counterpart. This benchmark uses libplacebo Vulkan, not Magpie's D3D implementation; throughput is not interchangeable.
  - MAIN hook CNN outputs x2 when output/input >1.2; final libplacebo target1080 downscales x2 intermediate via Lanczos.
- FSR1 v1.0.2 EASU+RCAS mpv port by agyild:
  https://gitea.suda.codes/sudacode/mpv/raw/commit/7904708a42d8a7ea266ce4d52778fa68e7a2381f/shaders/FSR.glsl
  - Pinned mirror commit. AMD permission/license embedded.
  - LUMA-plane port, not full RGB Magpie EASU; label `fsr1-luma` explicitly.
  - Default RCAS SHARPNESS=0.2 unchanged.

Neither shader is DLSS, FSR2/3/4 temporal super-resolution, or a temporal anti-aliasing implementation. Offline test includes decode, GPU upload/download, resize, NVENC encode and faststart. Shader performance alone is not total export speed.
