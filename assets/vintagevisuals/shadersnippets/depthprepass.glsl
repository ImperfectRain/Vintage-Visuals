// Vintage Visuals - depth prepass flag (D88), injected into chunkopaque.fsh by
// shaderpatches/depthprepass.yaml. 1 only while src/Common/Rendering/
// DepthPrepass.cs draws the depth-only copy of the opaque and leaf pools;
// 0 (the unset value) is vanilla. A float, never a sampler: it cannot move a
// texture unit.
uniform float vv_depthPrepass;

// D100. The prepass has a program of its own: src/Common/Rendering/
// ChunkProgramSplit.cs compiles this same source once more with
// VV_DEPTH_ONLY defined, so every test below is a constant there and all of
// the material after the early return is dead code the compiler removes.
// That twin no longer pays the main program's register budget. Here, in the
// game's own program, it stays the uniform test it always was, so the
// prepass still works (more slowly) when the split is off or failed.
// vv_depthPrepass still says WHICH prepass (1 blended pool, 2 unblended) in
// both programs.
#ifdef VV_DEPTH_ONLY
#define VV_PREPASS_ON true
#else
#define VV_PREPASS_ON (vv_depthPrepass > 0.5)
#endif
