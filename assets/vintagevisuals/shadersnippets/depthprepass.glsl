// Vintage Visuals - depth prepass flag (D88), injected into chunkopaque.fsh by
// shaderpatches/depthprepass.yaml. 1 only while src/Common/Rendering/
// DepthPrepass.cs draws the depth-only copy of the opaque and leaf pools;
// 0 (the unset value) is vanilla. A float, never a sampler: it cannot move a
// texture unit.
uniform float vv_depthPrepass;
