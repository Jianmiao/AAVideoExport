// Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// AA GLSL integration of the FSR1 RGB RCAS equations from AMD's ffx_fsr1.h:
// https://github.com/GPUOpen-Effects/FidelityFX-FSR/blob/a21ffb8f6c13233ba336352bdff293894c706575/ffx-fsr/ffx_fsr1.h
// Direct strength 0..1 follows the Magpie UI convention. SCALED runs after
// Anime4K's MAIN reconstruction and final resize, before output conversion.
// Guard zero denominators on constant black/white to avoid NaN output.

//!HOOK SCALED
//!BIND HOOKED
//!DESC AA Anime4K -> FSR RCAS RGB sharpening

#define AA_RCAS_STRENGTH @RCAS_SHARPNESS@

vec4 hook() {
    vec4 center = HOOKED_texOff(vec2(0.0));
    vec3 b = HOOKED_texOff(vec2(0.0, -1.0)).rgb;
    vec3 d = HOOKED_texOff(vec2(-1.0, 0.0)).rgb;
    vec3 e = center.rgb;
    vec3 f = HOOKED_texOff(vec2(1.0, 0.0)).rgb;
    vec3 h = HOOKED_texOff(vec2(0.0, 1.0)).rgb;
    vec3 weights = vec3(0.5, 1.0, 0.5);
    float bL = dot(b, weights), dL = dot(d, weights), eL = dot(e, weights);
    float fL = dot(f, weights), hL = dot(h, weights);
    float minimum = min(min(min(bL, dL), min(fL, hL)), eL);
    float maximum = max(max(max(bL, dL), max(fL, hL)), eL);
    float noise = 0.25 * (bL + dL + fL + hL) - eL;
    noise = 1.0 - 0.5 * clamp(abs(noise) / max(maximum - minimum, 1e-6), 0.0, 1.0);
    vec3 ringMin = min(min(b, d), min(f, h));
    vec3 ringMax = max(max(b, d), max(f, h));
    vec3 hitMin = min(ringMin, e) / max(4.0 * ringMax, vec3(1e-6));
    vec3 hitMax = (vec3(1.0) - max(ringMax, e)) / min(4.0 * ringMin - vec3(4.0), vec3(-1e-6));
    vec3 lobes = max(-hitMin, hitMax);
    float lobe = max(-0.1875, min(max(lobes.r, max(lobes.g, lobes.b)), 0.0));
    lobe *= AA_RCAS_STRENGTH * noise;
    return vec4(clamp((lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0), 0.0, 1.0), center.a);
}
