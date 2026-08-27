// Vintage Visuals - stage B terrain material-primary restoration.
//
// One VV sampler only: vv_materialTex. No second atlas, scene capture, world
// reflection, canopy context, or old terrain PBR branch is referenced here.

uniform sampler2D vv_materialTex;
uniform float vv_pbrEnabled;
uniform float vv_pbrDebugView;

vec2 vvMaterialPrimarySnap(vec2 materialUv)
{
    vec2 size = vec2(textureSize(vv_materialTex, 0));
    return (floor(materialUv * size) + vec2(0.5)) / size;
}

vec4 vvMaterialPrimarySample(vec2 materialUv)
{
    return texture(vv_materialTex, vvMaterialPrimarySnap(materialUv));
}

vec3 vvMaterialPrimaryGrid(vec2 materialUv)
{
    vec2 size = vec2(textureSize(vv_materialTex, 0));
    vec2 cell = fract(materialUv * size);
    float line = 1.0 - smoothstep(0.0, 0.08, min(min(cell.x, cell.y), min(1.0 - cell.x, 1.0 - cell.y)));
    float footprint = max(length(dFdx(materialUv) * size), length(dFdy(materialUv) * size));
    vec3 base = footprint <= 1.0 ? vec3(0.0, 0.65, 0.25)
              : footprint <= 2.0 ? vec3(0.9, 0.75, 0.05)
              : vec3(0.9, 0.1, 0.05);
    return mix(base, vec3(1.0), line * 0.65);
}

vec4 vvTerrainMaterialPrimary(vec4 color, vec2 materialUv)
{
    if (vv_pbrEnabled < 0.5) return color;

    vec4 material = vvMaterialPrimarySample(materialUv);
    float relief = length(material.rg * 2.0 - 1.0);
    float roughness = clamp(material.b, 0.0, 1.0);
    float specular = clamp(material.a, 0.0, 1.0);

    // Conservative single-sampler response: small local contrast only. It reads
    // the atlas, proves the resource path, and cannot replace vanilla lighting.
    float lift = 1.0 + relief * 0.035 + specular * (1.0 - roughness) * 0.045;
    color.rgb *= clamp(lift, 0.94, 1.08);
    return color;
}

vec4 vvTerrainMaterialPrimaryDebug(vec4 color, vec2 materialUv)
{
    int mode = int(vv_pbrDebugView + 0.5);
    if (mode == 0) return color;

    vec4 material = vvMaterialPrimarySample(materialUv);

    if (mode == 1) return vec4(material.rg, 1.0, color.a);
    if (mode == 2) return vec4(vec3(material.b), color.a);
    if (mode == 3) return vec4(vec3(material.a), color.a);
    if (mode == 33) return vec4(vvMaterialPrimaryGrid(materialUv), color.a);
    if (mode == 52)
    {
        vec2 size = vec2(textureSize(vv_materialTex, 0));
        float footprint = max(length(dFdx(materialUv) * size), length(dFdy(materialUv) * size));
        float nearOne = 1.0 - smoothstep(0.25, 1.0, abs(footprint - 1.0));
        vec3 colorRamp = footprint < 1.0
            ? mix(vec3(0.0, 0.25, 0.8), vec3(0.0, 0.8, 0.2), footprint)
            : mix(vec3(0.0, 0.8, 0.2), vec3(0.95, 0.1, 0.05), clamp((footprint - 1.0) / 3.0, 0.0, 1.0));
        return vec4(mix(colorRamp, vec3(1.0, 0.85, 0.05), nearOne * 0.55), color.a);
    }

    return color;
}
