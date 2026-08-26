// Vintage Visuals - zero-sampler terrain PBR restoration baseline.
//
// This snippet is intentionally a no-op. It proves that Vintage Story's terrain
// programs can safely host a VV source patch before any auxiliary sampler,
// material texture, reflection source, or canopy resource is reintroduced.

vec4 vvTerrainBaseIdentity(vec4 color)
{
    return color;
}
