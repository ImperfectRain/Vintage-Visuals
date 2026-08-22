#version 330 core

// Fullscreen triangle-pair for the scene capture pass.
//
// Positions arrive already in clip space, so there is no matrix here at all:
// this pass exists only to copy, and a copy that multiplies by a matrix is a
// copy with one more thing that can be wrong.
layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;

out vec2 uv;

void main()
{
    uv = uvIn;
    gl_Position = vec4(vertexPositionIn.xy, 0.0, 1.0);
}
