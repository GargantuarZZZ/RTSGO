shader_type canvas_item;

// 输入两张纹理
uniform sampler2D vision_texture;      // 当前视野 (白色代表可见)
uniform sampler2D exploration_texture; // 探索历史 (白色代表去过)

// 颜色配置
uniform vec4 unknown_color : source_color = vec4(0.0, 0.0, 0.0, 0.8); // 黑色区域
uniform vec4 fog_color : source_color = vec4(0.0, 0.0, 0.0, 0.5);     // 灰色区域 (半透明黑)

void fragment() {
    // 读取纹理颜色 (只需要红色通道，因为我们画的是黑白图)
    float current_vis = texture(vision_texture, UV).r;
    float explored = texture(exploration_texture, UV).r;
    
    if (current_vis > 0.1) {
        // 1. 当前可见：完全透明，显示原本的游戏画面
        COLOR = vec4(0.0, 0.0, 0.0, 0.0);
    } 
    else if (explored > 0.1) {
        // 2. 曾经去过：显示半透明灰色迷雾
        COLOR = fog_color;
    } 
    else {
        // 3. 从未去过：显示纯黑
        COLOR = unknown_color;
    }
}