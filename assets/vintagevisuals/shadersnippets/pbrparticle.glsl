// Vintage Visuals - lighting for particles
//
// Falling leaves, pollen, dust motes, sparks, embers and smoke. There are a lot
// of them in this game and until now they belonged to no lighting model at all
// - exactly the defect entities had, where a thing lit by one set of rules
// drifts past a world lit by another and never quite sits in it.
//
// Two shaders, and they are not equally equipped:
//
//   particlescube  has normal, worldPos, glowLevel, rgbaFog. Full treatment.
//   particlesquad  has glowLevel and little else. No normal means no microfacet
//                  lobe is possible, so it gets emission only - which is the
//                  half that matters for quads anyway, because quads are what
//                  sparks and fire particles are drawn as.
//
// Deliberately restrained even where the data allows more. A particle is small,
// numerous and in motion; a highlight that would read as detail on a wall reads
// as noise on a cloud of dust, and the whole point of Level 1 in the visual
// hierarchy is that the player has to be able to see past these.

uniform float vv_pbrParticle;         // master, 0 is vanilla
uniform float vv_pbrParticleSpecular; // how much lobe reaches the image
uniform float vv_pbrRoughnessBias;    // the same global matte <-> gloss slider everything else uses

// One dielectric reflectance for everything. A particle has no material atlas
// and never will: it is a few pixels of a texture that was authored as a
// silhouette, and running anything over it would be reading noise.
const float VV_PARTICLE_F0 = 0.04;

// Rougher than a creature. Dust, ash, pollen and leaf fragments are matte, and
// a particle with a tight highlight twinkles as it tumbles - which is the
// distracting kind of realism this project's visual language rules out.
const float VV_PARTICLE_ROUGHNESS = 0.78;

// The cube path: a real normal, so a real response.
//
// Falling leaves catch the sun the way the canopy they fell from does, and a
// leaf tumbling through a shaft of light is one of the few particle effects
// worth the instructions.
vec4 vvApplyParticlePbr(vec4 litColor, vec3 albedo, vec3 faceNormal, vec3 cameraRelativePos,
                        float shadowBrightness, float fog, vec3 environment, float glow)
{
    if (vv_pbrParticle < 0.5) return litColor;

    vec3 result = litColor.rgb;

    if (vv_pbrParticleSpecular > 0.001)
    {
        // Wetness, snow and frost all reach particles through the same resolve
        // everything else uses - rain-soaked ash should darken like rain-soaked
        // anything. Snow is zero for the same reason it is zero on creatures:
        // nothing settles on something that is falling.
        VvSurface surface = vvApplyEnvironmentLayers(
            VvSurface(albedo, VV_PARTICLE_ROUGHNESS, VV_PARTICLE_F0),
            clamp(vv_sceneWetness, 0.0, 1.0), 0.0, 0.0, 1.0);

        float roughness = clamp(surface.roughness + vv_pbrRoughnessBias, 0.04, 1.0);

        vec3 n = normalize(faceNormal);
        vec3 l = normalize(lightPosition);
        vec3 v = normalize(-cameraRelativePos);
        vec3 h = normalize(l + v);

        float ndotl = max(dot(n, l), 0.0);
        float ndotv = max(dot(n, v), 1e-4);

        vec3 f0 = vec3(surface.specular);
        vec3 fresnel = vvFresnelSchlick(max(dot(v, h), 0.0), f0);

        float distribution = vvDistributionGGX(max(dot(n, h), 0.0), roughness);
        float geometry = vvGeometrySmith(ndotv, ndotl, roughness);

        vec3 specular = (distribution * geometry * fresnel) / max(1e-4, 4.0 * ndotv * ndotl);

        float visibility = clamp(shadowBrightness, 0.0, 1.0)
                         * clamp(vv_sceneDayLight, 0.0, 1.0)
                         * clamp(1.0 - fog, 0.0, 1.0)
                         * mix(1.0, VV_OVERCAST_DIRECT, clamp(vv_sceneOvercast, 0.0, 1.0));

        result *= mix(vec3(1.0), 1.0 - fresnel, vv_pbrParticleSpecular);
        result += specular * ndotl * visibility * vv_pbrParticleSpecular;

        // Sky light, unshadowed as everywhere else. It matters more here than
        // on terrain: a mote of dust is lit almost entirely by the sky around
        // it rather than by anything it is sitting on.
        result += vvAmbientSpecular(f0, roughness, ndotv, environment)
                * clamp(vv_sceneDayLight, 0.0, 1.0)
                * clamp(1.0 - fog, 0.0, 1.0)
                * vv_pbrParticleSpecular;
    }

    // Emission, and for the quad path this is the whole of it. Sparks and
    // embers are the reason: vanilla draws them bright, and this makes them
    // read as hot and lets them feed the bloom pass the game already has.
    result += vvEmission(albedo, glow, cameraRelativePos) * clamp(1.0 - fog, 0.0, 1.0);

    return vec4(result, litColor.a);
}

// The quad path. No normal, so no lobe - emission and nothing else.
vec4 vvApplyParticleGlow(vec4 litColor, vec3 seedPos, float fog, float glow)
{
    if (vv_pbrParticle < 0.5) return litColor;

    // seedPos only seeds the flicker, and passing a constant here is a bug that
    // is easy to write and hard to see: every spark and ember in the world
    // would flicker in step, which reads as the whole fire pulsing rather than
    // as separate embers. The same mistake, in the same shape, as the rain drops
    // that all landed on one frame.
    return vec4(litColor.rgb + vvEmission(litColor.rgb, glow, seedPos) * clamp(1.0 - fog, 0.0, 1.0),
                litColor.a);
}

// The anchor line, pasted back. Replacement content is literal, never a regex
// template, so dropping this deletes the function every fog path calls - which
// is exactly what happened on the first attempt.
vec4 applyFogAndShadow(vec4 rgbaPixel, float fogWeight) {
