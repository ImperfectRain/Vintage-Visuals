#version 330 core

// Mirror mask (D97). The region is cleared to depth 0 ("nothing may draw"),
// and this writes depth 1 ("anything may draw") on the mirror's own
// footprint, with the depth test set to ALWAYS and colour writes off. The
// reflected terrain then passes the depth test only inside the mirror. A
// second draw after the terrain writes alpha 1 (alpha mask only, test
// GREATER) wherever something drew, which the terrain shader reads as
// "the capture saw something here".
out vec4 outColor;

void main()
{
    // Alpha 1: the second draw, after the terrain, marks coverage with it.
    outColor = vec4(0.0, 0.0, 0.0, 1.0);
    gl_FragDepth = 1.0;
}
