// Vintage Visuals - the shared scene vocabulary
//
// One set of names for what the scene is doing, injected into every program
// this mod shades with. The rule that comes with it matters more than the file:
//
//   A new shader maps its scene inputs into THESE uniforms and reads THESE
//   lanes. It does not invent a local meaning for wet, cold, enclosed, night,
//   restrained or readable.
//
// Without that rule, five subsystems each grow their own idea of "wet" and
// nothing anywhere says they have stopped agreeing - which is exactly what was
// beginning to happen when this was written: pseudopbr.glsl and weather.glsl
// each declared their own wetness and overcast uniforms with the meanings
// matched only by convention.
//
// It is the same idea as pbrcore.glsl one layer up: that file is the one
// evaluation of the lighting maths, this is the one description of the
// conditions it runs in.
//
// The anchor is `in vec4 rgbaFog;`, which all three target shaders declare and
// which sits above every other injection this mod makes into them. Replacement
// content is literal rather than a regex template, so the anchor is pasted back
// below or the declaration goes with the match.

in vec4 rgbaFog; // vintagevisuals: anchor, asserted and pasted back by scene.glsl

// --- what the world is doing ------------------------------------------------
//
// Every one of these is 0..1 and every zero means vanilla, which is not a
// convention but a requirement: an unset GLSL uniform reads as exactly 0, and a
// uniform can be unset because the binder skipped, the program was not patched,
// or a group rolled back.

uniform float vv_sceneDayLight;        // 0 midnight, 1 noon
uniform float vv_sceneWetness;         // 0 dry, 1 as wet as rain makes it
uniform float vv_sceneOvercast;        // 0 clear sky, 1 sun fully diffused
uniform float vv_sceneEnclosure;       // 0 open sky, 1 fully boxed in
uniform float vv_sceneArtificialLight; // 0 lit by sky, 1 lit by fire

// A clock, pre-wrapped to 0..1 on the CPU in double precision.
//
// Wrapped there rather than here because a shader can only wrap what it can
// still resolve: an unbounded float32 clock loses the ability to separate two
// phases at all past about ten million, which is how every rain drop in the
// world ended up landing on the same frame.
uniform float vv_sceneClock;

// --- season -----------------------------------------------------------------
//
// Two lanes rather than a single 0..1 year position, and computed on the CPU
// from the game's own GetSeason and GetSeasonRel rather than from a mapping
// this file would have to assume. The game also knows which hemisphere the
// player is in, and getting that backwards would put autumn in spring for half
// the world.
//
// NOTHING HERE RECOLOURS ANYTHING. Vanilla owns seasonal appearance completely
// and does it better than this mod could: colormapData carries a season map
// index, a climate map index, per-tree colour offsets, and a seasonWeight that
// already accounts for temperature, rainfall and altitude. These lanes exist to
// change how surfaces RESPOND - what takes water, what frosts - not how they
// look.

uniform float vv_sceneAutumn;  // 0 away from autumn, 1 at its middle
uniform float vv_sceneWinter;  // 0 away from winter, 1 at its middle

// How much the environmental frost layer is allowed to do, on top of vanilla's
// own frost mask. 0 leaves vanilla's frost exactly as it was.
uniform float vv_sceneFrost;

// How much snow may lie on a surface, before per-fragment gating.
uniform float vv_sceneSnow;

// --- what the scene needs ---------------------------------------------------

// How much the mod should hold back.
//
// Rises where the scene is ALREADY hard to read - deep underground, at night,
// in a storm - and scales down everything that removes light, colour or
// contrast. Most of the arbitration happens on the CPU where it can be recorded
// and explained; this is the residue the shaders need, for the terms whose cost
// is only knowable per fragment.
uniform float vv_sceneRestraint;

// How much this scene needs help being legible. Sets floors, never looks.
uniform float vv_sceneReadability;

// --- derived lanes ----------------------------------------------------------
//
// Named so the call sites read as intent rather than as arithmetic. A term
// written `* vvSceneDampen()` says why it is being scaled; the same expression
// spelled out inline does not, and gets deleted by whoever tunes it next.

// What a light-removing term is allowed to keep.
//
// The floor is not 0. An effect that can be driven to nothing by conditions is
// an effect that silently stops existing, and nobody ever works out why - so
// the darkest, most restrained scene still keeps a third of it.
float vvSceneDampen()
{
    return mix(1.0, 0.33, clamp(vv_sceneRestraint, 0.0, 1.0));
}

// The stronger version, for terms that specifically cost VISIBILITY rather than
// merely appearance: fog, shadow, anything that hides geometry the player may
// be about to walk into or be hit by.
float vvSceneVisibilityDampen()
{
    float hold = max(clamp(vv_sceneRestraint, 0.0, 1.0), clamp(vv_sceneReadability, 0.0, 1.0));
    return mix(1.0, 0.25, hold);
}

// True where the sky is doing the lighting. Weather is an outdoor phenomenon
// and a cellar is the same colour whatever it is doing outside.
float vvSceneOpenAir()
{
    return 1.0 - clamp(vv_sceneEnclosure, 0.0, 1.0);
}

// Where fire rather than sky is the light source, which is most of this game's
// interiors and all of its nights.
float vvSceneFirelit()
{
    return clamp(vv_sceneArtificialLight, 0.0, 1.0) * (1.0 - clamp(vv_sceneDayLight, 0.0, 1.0) * 0.5);
}
