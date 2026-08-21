// Vintage Visuals - the material response for entities
//
// The same microfacet lobe the terrain uses, on mobs, animals and players.
//
// This exists because of an inconsistency that is obvious once you look for it
// and invisible until then: a mob standing on PBR-lit ground was shaded by a
// completely different lighting model than the ground it stood on. The floor
// had a specular response to the sun, a sky term, torch highlights and a wet
// response; the creature on it had vanilla's flat diffuse. Whatever the
// material system is worth, it was worth half while that was true.
//
// The important difference from terrain: THERE IS NO MATERIAL ATLAS HERE.
// Entities draw from entityTex, a different atlas from the block one the
// material system derives from, and deriving normals from a mob skin would be
// the "dark is deep" fallacy at its worst - painted-on fur shadows read as
// geometry, and a face would come out with cheekbones where the shading was.
// So entities get a DEFAULT material instead: one roughness, one reflectance,
// and the mesh's own normal, which unlike a block face is real per-vertex
// geometry and is better than anything an atlas could have given us.
//
// What that buys, concretely: a grazing-angle sheen that makes a creature sit
// in the light rather than on top of it, a sky term so it is not lit only from
// the front, torch highlights that agree with the ones on the wall behind it,
// and a wet response in rain.

uniform float vv_pbrEntity;          // master, 0 is vanilla
uniform float vv_pbrEntityRoughness; // how matte a creature reads
uniform float vv_pbrEntitySpecular;  // how much of the lobe reaches the image
uniform float vv_pbrRoughnessBias;   // the same global matte <-> gloss slider terrain uses
// Daylight, wetness and overcast come from scene.glsl, shared with the terrain
// programs so a creature and the ground it stands on cannot disagree about the
// weather.

// No sky-exposure test, unlike terrain.
//
// Terrain thresholds vanilla's per-vertex sun light so rain cannot reach a
// surface under a roof. An entity is a moving thing with no such varying, and
// inventing one would mean a second occlusion model that could disagree with
// the first. Creatures indoors get slightly wet during a storm; that is the
// wrong answer, and it is a smaller wrong answer than a mob standing in the
// rain looking bone dry.

// Reflectance of a dielectric at normal incidence. Skin, fur, cloth, chitin and
// horn all sit within a whisker of 0.04, which is why one number covers every
// creature in the game and a metalness channel would be an invented answer to
// a question nothing here asks.
const float VV_ENTITY_F0 = 0.04;

// What rain does to a creature. Weaker than the terrain's numbers on purpose:
// fur and cloth hold water rather than filming it, so they darken more than
// they gloss, which is the opposite of what happens to stone.
const float VV_ENTITY_WET_ROUGHNESS = 0.35;
const float VV_ENTITY_WET_DARKEN = 0.80;


// Applied to the colour vanilla has already lit, exactly as on terrain: this
// adds a specular lobe and the energy that lobe takes back out of the diffuse,
// and leaves vanilla's diffuse - which the whole game is balanced around - to
// do its job.
vec4 vvApplyEntityPbr(vec4 litColor, vec3 albedo, vec3 faceNormal, vec3 cameraRelativePos,
                      float shadowBrightness, float fog, vec3 environment, vec3 blockLightColor)
{
    if (vv_pbrEntity < 0.5 || vv_pbrEntitySpecular < 0.001) return litColor;

    float wetness = clamp(vv_sceneWetness, 0.0, 1.0);

    float roughness = clamp(mix(vv_pbrEntityRoughness, VV_ENTITY_WET_ROUGHNESS, wetness)
                            + vv_pbrRoughnessBias, 0.04, 1.0);

    vec3 n = normalize(faceNormal);
    vec3 l = normalize(lightPosition);
    vec3 v = normalize(-cameraRelativePos);
    vec3 h = normalize(l + v);

    float ndotl = max(dot(n, l), 0.0);
    float ndotv = max(dot(n, v), 1e-4);
    float ndoth = max(dot(n, h), 0.0);
    float vdoth = max(dot(v, h), 0.0);

    // Widened by however much the normal varies inside this pixel. It matters
    // more here than on terrain: entity meshes are small on screen and curved,
    // so a whole highlight can fall inside one pixel and sparkle as the
    // creature moves.
    roughness = vvFilteredRoughness(roughness, n);

    vec3 f0 = vec3(VV_ENTITY_F0);
    vec3 fresnel = vvFresnelSchlick(vdoth, f0);

    float distribution = vvDistributionGGX(ndoth, roughness);
    float geometry = vvGeometrySmith(ndotv, ndotl, roughness);

    vec3 specular = (distribution * geometry * fresnel) / max(1e-4, 4.0 * ndotv * ndotl);

    float overcast = clamp(vv_sceneOvercast, 0.0, 1.0);

    float visibility = clamp(shadowBrightness, 0.0, 1.0)
                     * clamp(vv_sceneDayLight, 0.0, 1.0)
                     * clamp(1.0 - fog, 0.0, 1.0)
                     * mix(1.0, VV_OVERCAST_DIRECT, overcast);

    // Wet fur and wet cloth go dark. This is the half of wetness that sells it
    // on a creature, more than the highlight does.
    vec3 result = litColor.rgb * mix(1.0, VV_ENTITY_WET_DARKEN, wetness);

    // Energy conservation, as on terrain: light reflected specularly is light
    // that did not scatter diffusely.
    result *= mix(vec3(1.0), 1.0 - fresnel, vv_pbrEntitySpecular);

    result += specular * ndotl * visibility * vv_pbrEntitySpecular;

    // Torches, lava and glowing blocks, with the direction recovered from the
    // block-light gradient exactly as on terrain - so a creature beside a forge
    // catches the same highlight the wall behind it does.
    result += vvBlockLightSpecular(f0, roughness, n, v, blockLightColor, cameraRelativePos)
            * clamp(1.0 - fog, 0.0, 1.0)
            * vv_pbrEntitySpecular;

    // Sky. Not shadowed, for the same reason as on terrain: sky light reaches
    // what the sun does not, and killing it in shadow is what makes a creature
    // in a doorway look like a cutout.
    result += vvAmbientSpecular(f0, roughness, ndotv, environment)
            * clamp(vv_sceneDayLight, 0.0, 1.0)
            * clamp(1.0 - fog, 0.0, 1.0)
            * vv_pbrEntitySpecular
            * mix(1.0, VV_OVERCAST_AMBIENT, overcast);

    return vec4(result, litColor.a);
}

uniform float vv_pbrEntityDebug;     // 0 normal, 1 lobe only, 2 wetness, 3 scene restraint

// Debug views for the entity path, numbered from 1 in ITS OWN terms.
//
// Deliberately not sharing the material system's list. Those numbers mean
// material layers - normal map, roughness, relief - and an entity has none of
// them; a shared list would have to skip most of itself to stay meaningful, and
// two subsystems would eventually both want the same number.
vec4 vvEntityDebugView(vec4 color, vec3 faceNormal, vec3 cameraRelativePos,
                       float shadowBrightness, vec3 environment, vec3 blockLightColor)
{
    int mode = int(vv_pbrEntityDebug + 0.5);
    if (mode <= 0) return color;

    // 1: the specular lobe alone, on black. A creature should catch a highlight
    // that moves with the sun; a flat result means the lobe is not running.
    if (mode == 1)
    {
        vec4 lobe = vvApplyEntityPbr(vec4(0.0, 0.0, 0.0, color.a), vec3(1.0), faceNormal,
                                     cameraRelativePos, shadowBrightness, 0.0,
                                     environment, blockLightColor);
        return vec4(lobe.rgb, color.a);
    }

    // 2: how wet this creature is. White in a downpour, black indoors - except
    // that entities have no sky-exposure test, so indoors is only darker rather
    // than black. That is the known compromise, made visible.
    if (mode == 2) return vec4(vec3(clamp(vv_sceneWetness, 0.0, 1.0)), color.a);

    // 3: how much the scene is holding the mod back. Bright underground and at
    // night, black in open daylight.
    if (mode == 3) return vec4(vec3(clamp(vv_sceneRestraint, 0.0, 1.0)), color.a);

    return color;
}
// The anchor line, pasted back. Replacement content is literal, never a regex
// template, so dropping this would delete the function every fog path calls.
vec4 applyFogAndShadow(vec4 rgbaPixel, float fogWeight) {
