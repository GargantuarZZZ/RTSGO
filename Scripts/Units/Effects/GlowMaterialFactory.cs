using Godot;

namespace RTS.Units
{
	/// <summary>光束/射线特效公因式：直线光束与曲线光束原来各写一份
	/// “自发光、无光照、半透明、双面”的 StandardMaterial3D 配置，统一到这里。</summary>
	public static class GlowMaterialFactory
	{
		/// <summary>
		/// 发光材质。
		///
		/// 关键点（原来这里是自相矛盾的）：**Godot 在 Unshaded 下会完全忽略
		/// Emission / EmissionEnergyMultiplier**，所以之前那个
		/// `EmissionEnergyMultiplier = energy` 从来没有生效过 ——
		/// energy 参数写了等于没写，光束亮度只由 AlbedoColor 决定。
		///
		/// 现在的做法：Unshaded 下把能量直接烘进 AlbedoColor。
		/// 这样"能量更高 = 更亮"立刻可见，而且不依赖 WorldEnvironment 的 Glow
		/// （没有环境时也能看出差别）；一旦场景加了 Glow，超过 1.0 的部分还会自然泛光。
		/// </summary>
		public static StandardMaterial3D CreateGlow(Color color, float energy = 5f)
		{
			return new StandardMaterial3D
			{
				AlbedoColor = Boost(color, energy),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};
		}

		/// <summary>
		/// 把"亮度能量"映射成 RGB 亮度倍率（保留 alpha）。
		///
		/// 调用方传进来的 energy 量级是 1~6（原来是 EmissionEnergyMultiplier 的写法）。
		/// 这里把它**归一化**再映射到 [1.0, 2.2] 区间：
		///
		///   energy ≈ 1 → ×1.0（原色，不额外提亮）
		///   energy ≈ 6 → ×2.2（明显更亮）
		///
		/// 为什么不直接乘：原来的 4/5/6 是 Emission 的语义（可远超 1 倍），
		/// 直接当 albedo 倍率会让所有特效一起过曝成白块，反而丢掉颜色。
		/// 映射之后既让 energy 参数重新"有效"（不同值看得出区别），
		/// 又保住了色相 —— 而且一旦场景加了 Glow，>1.0 的部分会自然泛光。
		/// </summary>
		public static Color Boost(Color color, float energy)
		{
			float t = Mathf.Clamp((energy - 1f) / 5f, 0f, 1f); // 1..6 → 0..1
			float k = 1f + t * 1.2f;                           // → 1.0..2.2
			return new Color(color.R * k, color.G * k, color.B * k, color.A);
		}
	}
}
