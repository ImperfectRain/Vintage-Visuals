// Vintage Visuals - volumetric cloud shaping
//
// Injected into cloudvolumetric.fsh at vanilla's own octave(), which is the
// function every cloud shape in the frame is built from. Deliberately a small
// intervention in an existing raymarcher rather than a replacement for it:
// vanilla's traversal, lighting and depth handling are all doing work this mod
// has no reason to redo, and the shape is the part that reads as weather.

uniform float vv_cloudDetail;   // extra high-frequency shaping, 0 is vanilla
uniform float vv_cloudDensity;  // multiplies cloud opacity, 1 is vanilla

// Replaces vanilla's octave() entirely - the patch anchors on the whole
// function, so nothing of the original is left to paste back.
//
// Vanilla mixes two frequencies, which gives clouds a smooth billow and no
// edge. A third at roughly three times the second breaks that silhouette into
// the ragged fringe real cumulus have - the detail the eye actually uses to
// judge that something is a cloud rather than a soft grey shape.
//
// Blended rather than added so 0 is exactly vanilla, and weighted so the total
// stays near unity: raising the amplitude instead would inflate every cloud as
// a side effect of sharpening it.
float octave(vec3 p){
    float base = noise(p * 2.0) * 0.66 + noise(p * 6.0) * 0.33;

    base = mix(base, base * 0.82 + noise(p * 17.0) * 0.18, clamp(vv_cloudDetail, 0.0, 1.0));

    return base * 2.0 - 1.0;
}
