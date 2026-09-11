shader_type canvas_item;

uniform sampler2D exploration_texture; // 对应 FogOfWar.ExplorVP
uniform sampler2D vision_texture;      // 对应 FogOfWar.VisionVP
uniform vec4 unexplored_color : source_color = vec4(0.0, 0.0, 0.0, 0.6);
uniform vec4 explored_no_vision_color : source_color = vec4(0.3, 0.3, 0.3, 0.4);
uniform vec2 fog_origin = vec2(-10000.0, -10000.0); // 迷雾纹理覆盖的世界范围起点
uniform vec2 fog_size = vec2(20000.0, 20000.0);     // 迷雾纹理覆盖的世界范围大小
uniform vec2 map_origin = vec2(-10000.0, -10000.0); // 小地图显示的世界范围起点
uniform vec2 map_size = vec2(20000.0, 20000.0);     // 小地图显示的世界范围大小

void fragment() {
    // 小地图窗口 UV -> 世界坐标 -> 迷雾纹理 UV（按实际地图边界对齐）
    vec2 world = UV * map_size + map_origin;
    vec2 fog_uv = (world - fog_origin) / fog_size;
    float exploration = texture(exploration_texture, fog_uv).r;
    float vision = texture(vision_texture, fog_uv).r;
    
    vec4 final_color = vec4(0.0);
    
    if (vision > 0.1) {
        // 有视野：透明，不显示遮罩
        final_color = vec4(0.0);
    } else if (exploration > 0.1) {
        // 已探索无视野：显示灰色遮罩
        final_color = explored_no_vision_color;
    } else {
        // 未探索：显示黑色遮罩
        final_color = unexplored_color;
    }
    COLOR = final_color;
}
