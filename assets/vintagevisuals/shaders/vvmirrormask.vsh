#version 330 core

// Mirror mask (D97): one quad per visible mirror rectangle, drawn into the
// mirror camera's atlas region before the reflected terrain, so the terrain
// lands only on pixels the mirror itself covers.
layout(location = 0) in vec3 vertexPositionIn;

uniform mat4 vv_mvp;

void main()
{
    gl_Position = vv_mvp * vec4(vertexPositionIn, 1.0);
}
