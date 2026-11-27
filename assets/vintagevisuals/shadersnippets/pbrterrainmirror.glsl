// Vintage Visuals - mirror surfaces on the modular terrain path (D97).
//
// The player marks surfaces as mirrors (.vvmirror, Ctrl+Shift+M). For each
// mirror plane in view, MirrorRenderer runs the game's own terrain pass once
// more through a reflected camera into a small region of a shared atlas. This
// file is the terrain shader's half: which fragments are on a mirror, and
// which atlas pixel each one shows.
//
// Its own patch group because it owns a sampler, declared here directly above
// main and so below vanilla's last sampler (liquidDepth), on unit 9, which the
// texture slot report shows is free in chunkopaque. The group exists in source
// only while the world has at least one mirror and mirrors are switched on, so
// a world without mirrors compiles none of this.
//
// Zero is vanilla: vv_mirrorBoxCount 0 means no fragment is on a mirror, and
// an unset atlas rect (all zero) means "no picture", which leaves the normal
// reflection in place.
//
// THE TEXEL GRID. A fragment reads the atlas at the screen position of the
// centre of its reflected CELL, not its own: one texture pixel (1/32 block)
// up close, then 2x2, 4x4 ... texels with distance, the same octave rule the
// CPU used to size the capture, so one capture pixel lands on each cell.

#define VV_MIRROR_MAX_BOXES 32
#define VV_MIRROR_MAX_SLOTS 8

uniform sampler2D vv_mirrorTex;
uniform float vv_mirrorBoxCount;                       // boxes in use, 0 = no mirrors
uniform vec4 vv_mirrorBox[VV_MIRROR_MAX_BOXES * 2];    // [min.xyz, slot] [max.xyz, 0], camera-relative
uniform vec4 vv_mirrorPlane[VV_MIRROR_MAX_SLOTS];      // n.xyz, w: the plane n.p + w = 0
uniform vec4 vv_mirrorAtlas[VV_MIRROR_MAX_SLOTS];      // atlas rect xy, size; zero = no picture
uniform vec4 vv_mirrorCrop[VV_MIRROR_MAX_SLOTS];       // the screen rect the capture covers, uv min, max
uniform vec4 vv_mirrorLook[VV_MIRROR_MAX_SLOTS];       // polish, strength, LOD start, 0
uniform mat4 vv_mirrorViewProj;                        // the view-projection this pass sees mirrors through

int vvTerrainMirrorSlot(int box)
{
    return int(clamp(vv_mirrorBox[box * 2].w, 0.0, float(VV_MIRROR_MAX_SLOTS - 1)) + 0.5);
}

// The mirror box holding this fragment and facing the same way, or -1.
int vvTerrainMirrorFind(vec3 cameraRelativePos, vec3 faceNormal)
{
    int count = int(clamp(vv_mirrorBoxCount, 0.0, float(VV_MIRROR_MAX_BOXES)) + 0.5);
    for (int i = 0; i < VV_MIRROR_MAX_BOXES; i++)
    {
        if (i >= count) break;
        vec3 lo = vv_mirrorBox[i * 2].xyz;
        vec3 hi = vv_mirrorBox[i * 2 + 1].xyz;
        if (all(greaterThanEqual(cameraRelativePos, lo)) && all(lessThanEqual(cameraRelativePos, hi)))
        {
            if (dot(faceNormal, vv_mirrorPlane[vvTerrainMirrorSlot(i)].xyz) > 0.9) return i;
        }
    }
    return -1;
}

vec4 vvTerrainMirrorLook(int box)
{
    return vv_mirrorLook[vvTerrainMirrorSlot(box)];
}

// What the mirror shows at this fragment's cell: rgb, and alpha 1 where the
// capture drew something (0 where it saw only sky, or has no picture), so the
// caller keeps its own reflection there.
vec4 vvTerrainMirrorSample(int box, vec3 cameraRelativePos)
{
    int slot = vvTerrainMirrorSlot(box);
    vec4 atlas = vv_mirrorAtlas[slot];
    if (atlas.z <= 0.0 || atlas.w <= 0.0) return vec4(0.0);

    float distance = vvTerrainEyeDistance(cameraRelativePos);
    float start = max(vv_mirrorLook[slot].z, 0.25);
    float lod = distance <= start ? 0.0 : min(6.0, floor(log2(distance / start)) + 1.0);
    float cell = exp2(lod) / 32.0;

    // Snap across the plane only; the plane coordinate itself stays exact.
    // The wrapped origin is a multiple of every cell size, so the grid lands
    // on the texture grid wherever the player stands.
    vec3 across = vec3(1.0) - abs(vv_mirrorPlane[slot].xyz);
    vec3 world = cameraRelativePos + vv_pbrOrigin;
    vec3 snapped = mix(world, (floor(world / cell) + 0.5) * cell, across);
    vec3 q = snapped - vv_pbrOrigin;
    // A big cell at the edge of a small mirror would centre off the mirror,
    // where the capture is masked out; keep it on the mirror.
    q = clamp(q, vv_mirrorBox[box * 2].xyz + 0.002, vv_mirrorBox[box * 2 + 1].xyz - 0.002);

    vec4 clip = vv_mirrorViewProj * vec4(q, 1.0);
    if (clip.w <= 1e-5) return vec4(0.0);
    vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
    vec4 crop = vv_mirrorCrop[slot];
    vec2 local = clamp((uv - crop.xy) / max(crop.zw - crop.xy, vec2(1e-6)), vec2(0.0), vec2(1.0 - 1e-4));
    vec4 seen = textureLod(vv_mirrorTex, atlas.xy + local * atlas.zw, 0.0);
    return vec4(max(seen.rgb, vec3(0.0)), step(0.5, seen.a));
}
