shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

uniform sampler2D vision_texture;      // 当前视野（白色=可见）
uniform sampler2D exploration_texture; // 探索历史（白色=去过）
uniform vec4 fog_color : source_color = vec4(0.05, 0.05, 0.08, 0.55);
uniform vec4 unknown_color : source_color = vec4(0.0, 0.0, 0.0, 0.85);

void fragment() {
    // PlaneMesh 的 UV(0..1) 覆盖整个地面，与迷雾纹理一一对应
    vec2 uv = UV;
    float current_vis = texture(vision_texture, uv).r;
    float explored = texture(exploration_texture, uv).r;

    if (current_vis > 0.1) {
        ALBEDO = vec3(0.0);
        ALPHA = 0.0;
    } else if (explored > 0.1) {
        ALBEDO = fog_color.rgb;
        ALPHA = fog_color.a;
    } else {
        ALBEDO = unknown_color.rgb;
        ALPHA = unknown_color.a;
    }
}
