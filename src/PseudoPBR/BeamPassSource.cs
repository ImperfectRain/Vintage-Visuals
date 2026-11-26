using System;
using System.Text;
using System.Text.RegularExpressions;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// D87. The GLSL of the fast beam pass, assembled from the SAME snippets
    /// the terrain shader injects, so it shares every helper (the leaf map,
    /// the scene gates, the beam colour) rather than copying them. Since D92
    /// its march is <c>vvBeamShadowInscatter</c> (beampass.glsl), which reads
    /// the game's shadow map; the terrain's own <c>vvTerrainBeamInscatter</c>
    /// is only the fallback used while this pass is off or has failed.
    ///
    /// Pure string work with no game types, so <c>tools/verifypatches</c> can
    /// build exactly what the game will compile and run glslang over it.
    ///
    /// The terrain snippet refers to vanilla chunk-shader names (the vertex
    /// varyings, renderFlags, the shadow inputs). The pass draws no terrain, so
    /// none of them means anything here: <see cref="Stubs"/> declares each as a
    /// plain global so the one snippet compiles unchanged, and the compiler
    /// drops every function that reads them because main calls only the march.
    /// Every <c>in</c> declaration the snippets make is rewritten into a global
    /// for the same reason - a fragment input nothing writes does not link.
    /// </summary>
    public static class BeamPassSource
    {
        public const string SceneSnippet = "scene.glsl";
        public const string CoreSnippet = "pbrcore.glsl";
        public const string TerrainSnippet = "pbrterrainmaterial.glsl";
        public const string ReflectSnippet = "reflect.glsl";
        public const string BeamSnippet = "beampass.glsl";   // D92: the shadow-map march

        /// <summary>Where the terrain group pastes reflect.glsl; the pass does the same.</summary>
        public const string ReflectMarker = "// vintagevisuals-hook: terrain scene capture define";

        public const string DepthUniform = "vv_beamDepth";
        public const string InverseViewProjectionUniform = "vv_beamInvViewProj";
        public const string FogUniform = "vv_beamFog";            // density, min, flat density, flat start
        public const string LowResUniform = "vv_beamLowRes";
        public const string LowSizeUniform = "vv_beamLowSize";
        public const string ZPlanesUniform = "vv_beamZ";          // zNear, zFar
        public const string ShadowFarUniform = "vv_beamShadowFar";
        public const string ShadowNearUniform = "vv_beamShadowNear";
        public const string ToShadowFarUniform = "vv_beamToShadowFar";
        public const string ToShadowNearUniform = "vv_beamToShadowNear";
        public const string ShadowValidUniform = "vv_beamShadowValid";

        /// <summary>
        /// The prefix defines the game may or may not supply to a memory
        /// program. The pass pins every one to 0 itself, so the source compiles
        /// identically whatever the game prepends (and without any).
        /// </summary>
        private static readonly string[] PinnedDefines =
            { "SHADOWQUALITY", "SSAOLEVEL", "GODRAYS", "NORMALVIEW", "SHINYEFFECT", "USEOIT", "FOAMEFFECT" };

        public const string Vertex =
@"#version 330 core
// D87 fast beam pass: a clip-space quad, no matrix.
layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
out vec2 vv_beamUv;
void main()
{
    vv_beamUv = uvIn;
    gl_Position = vec4(vertexPositionIn.xy, 0.0, 1.0);
}
";

        public const string Stubs =
@"// Vanilla chunk-shader names the shared terrain snippet refers to. The beam
// pass draws no terrain, so none of these is ever meaningful here.
int renderFlags = 0;
const int WindModeBitMask = 0;
const int WindModePosition = 0;
const int WindDataPosition = 0;
vec2 uv = vec2(0.0);
vec4 worldPos = vec4(0.0);
vec3 normal = vec3(0.0, 1.0, 0.0);
vec3 blockLight = vec3(0.0);
vec4 rgbaLightIn = vec4(0.0);
float blockBrightness = 0.0;
float shadowIntensity = 0.0;
float vv_vanillaOcclusion = -1.0;
uniform sampler2D terrainTex;
uniform sampler2D liquidDepth;
";

        /// <summary>
        /// The low-resolution march. One fragment per target pixel: rebuild the
        /// feet-origin position from the primary depth (the space every terrain
        /// position is in, D74), take vanilla's fog level for it (the vertex
        /// stage's getFogLevel, the one chunkopaque's fogAmount comes from),
        /// and call the terrain's own march. Alpha carries linear depth for the
        /// upsample; the sky stores a far depth and no light.
        /// </summary>
        public const string MarchMain =
@"
// ---- D87 fast beam pass: low-resolution march --------------------------
uniform sampler2D vv_beamDepth;
uniform mat4 vv_beamInvViewProj;
uniform vec4 vv_beamFog;   // fogDensityIn, fogMinIn, flatFogDensity, flatFogStart
uniform vec2 vv_beamZ;     // zNear, zFar
in vec2 vv_beamUv;
layout(location = 0) out vec4 vv_beamOut;

float vvBeamFogLevel(vec3 p)
{
    float depth = length(p);
    float clampedDepth = min(250.0, depth);
    float heightDiff = p.y - vv_beamFog.w;
    float extraDistanceFog = max(-vv_beamFog.z * clampedDepth * vv_beamFog.w / 60.0, 0.0);
    float distanceFog = 1.0 - 1.0 / exp(clampedDepth * vv_beamFog.x + extraDistanceFog);
    float flatFog = 1.0 - 1.0 / exp(heightDiff * vv_beamFog.z);
    float val = max(flatFog, distanceFog);
    float nearnessToPlayer = clamp((8.0 - depth) / 8.0, 0.0, 0.9);
    val = max(min(0.04, val), val - nearnessToPlayer);
    return clamp(val + vv_beamFog.y, 0.0, 1.0);
}

void main()
{
    float d = texture(vv_beamDepth, vv_beamUv).r;
    float zn = vv_beamZ.x;
    float zf = vv_beamZ.y;
    vec4 p = vv_beamInvViewProj * vec4(vv_beamUv * 2.0 - 1.0, min(d, 0.9999) * 2.0 - 1.0, 1.0);
    vec3 pos = p.xyz / p.w;
    float fogAmount;
    float linear;
    if (d >= 1.0)
    {
        // D92: open sky behind. The air in front of it still carries shafts
        // (looking up through a gap), so march a ray of full reach; the sky
        // is already fogged by the game, so no surface fog applies.
        pos = vv_pbrEye + normalize(pos - vv_pbrEye) * VV_BEAM_REACH;
        fogAmount = 0.0;
        linear = zf;
    }
    else
    {
        fogAmount = vvBeamFogLevel(pos);
        linear = (2.0 * zn * zf) / (zf + zn - (d * 2.0 - 1.0) * (zf - zn));
    }
    float level = vvBeamShadowInscatter(pos, vv_beamFog.x) * (1.0 - fogAmount);
    vv_beamOut = vec4(vvTerrainBeamColor() * level, linear);
}
";

        /// <summary>
        /// The full-resolution composite, added onto the primary framebuffer.
        ///
        /// A 3x3 depth-aware filter over the low-resolution march: each tap is
        /// weighted by how close its depth is to this pixel's, so a beam behind
        /// a trunk does not bleed onto the trunk and the march's per-pixel
        /// sample offset is averaged out rather than drawn as a crosshatch.
        /// Writes nothing but light: every output has alpha 0 under the
        /// premultiplied blend (ONE, ONE_MINUS_SRC_ALPHA), which makes it a pure
        /// add, and alpha is masked off by the renderer.
        /// </summary>
        public const string Composite =
@"#version 330 core
// D87 fast beam pass: depth-aware upsample, added onto the primary buffer.
uniform sampler2D vv_beamLowRes;
uniform sampler2D vv_beamDepth;
uniform vec2 vv_beamLowSize;
uniform vec2 vv_beamZ;     // zNear, zFar
in vec2 vv_beamUv;
layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;

void main()
{
    outGlow = vec4(0.0);
    outGNormal = vec4(0.0);
    outGPosition = vec4(0.0);
    float d = texture(vv_beamDepth, vv_beamUv).r;
    float zn = vv_beamZ.x;
    float zf = vv_beamZ.y;
    // The sky is zFar, as the march stores it (D92 draws shafts against it).
    float linear = d >= 1.0 ? zf : (2.0 * zn * zf) / (zf + zn - (d * 2.0 - 1.0) * (zf - zn));
    float tolerance = 0.06 * linear + 0.15;

    vec2 texel = vv_beamUv * vv_beamLowSize - 0.5;
    ivec2 base = ivec2(floor(texel + 0.5));
    ivec2 limit = ivec2(vv_beamLowSize) - 1;
    vec3 sum = vec3(0.0);
    float weight = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            ivec2 at = clamp(base + ivec2(x, y), ivec2(0), limit);
            vec4 s = texelFetch(vv_beamLowRes, at, 0);
            vec2 off = vec2(at) - texel;
            float spatial = max(0.0, 1.0 - dot(off, off) * 0.25);
            float w = spatial * exp(-abs(s.a - linear) / tolerance) + 1e-5;
            sum += s.rgb * w;
            weight += w;
        }
    }
    vec3 beam = sum / weight;
    if (dot(beam, vec3(1.0)) <= 0.0) discard;
    outColor = vec4(beam, 0.0);   // alpha 0: (ONE, ONE_MINUS_SRC_ALPHA) adds
}
";

        private static readonly Regex InputDeclaration =
            new Regex(@"^[ \t]*in[ \t]+(float|vec2|vec3|vec4)[ \t]+(\w+)[ \t]*;", RegexOptions.Multiline);

        /// <summary>
        /// The march program's fragment source, or null when a snippet is
        /// missing (the renderer then fails safe and the old beams stay).
        /// </summary>
        public static string March(Func<string, string> snippet)
        {
            string scene = snippet(SceneSnippet);
            string core = snippet(CoreSnippet);
            string terrain = snippet(TerrainSnippet);
            string reflect = snippet(ReflectSnippet);
            string beam = snippet(BeamSnippet);
            if (scene == null || core == null || terrain == null || reflect == null || beam == null) return null;

            int marker = terrain.IndexOf(ReflectMarker, StringComparison.Ordinal);
            if (marker < 0) return null;
            terrain = terrain.Insert(marker, reflect + "\n");

            var sb = new StringBuilder();
            sb.Append("#version 330 core\n");
            foreach (string name in PinnedDefines)
            {
                sb.Append("#ifdef ").Append(name).Append("\n#undef ").Append(name).Append("\n#endif\n");
                sb.Append("#define ").Append(name).Append(" 0\n");
            }
            sb.Append(Stubs);
            sb.Append(Globalise(scene)).Append('\n');
            sb.Append(Globalise(core)).Append('\n');
            sb.Append(Globalise(terrain)).Append('\n');
            sb.Append(beam).Append('\n');
            sb.Append(MarchMain);
            return sb.ToString();
        }

        /// <summary>
        /// Turns each fragment input into a zero global. A varying nothing
        /// writes fails the link, and in this pass nothing writes them.
        /// </summary>
        internal static string Globalise(string source)
        {
            return InputDeclaration.Replace(source, m => m.Groups[1].Value + " " + m.Groups[2].Value +
                                                         " = " + m.Groups[1].Value + "(0.0);");
        }
    }
}
