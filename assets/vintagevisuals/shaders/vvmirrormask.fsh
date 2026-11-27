#version 330 core

// Mirror mask (D97). The region is cleared to depth 0 ("nothing may draw"),
// and this writes depth 1 ("anything may draw") on the mirror's own
// footprint, with the depth test set to ALWAYS and colour writes off. The
// reflected terrain then passes the depth test only inside the mirror.
out vec4 outColor;

void main()
{
    outColor = vec4(0.0);
    gl_FragDepth = 1.0;
}
