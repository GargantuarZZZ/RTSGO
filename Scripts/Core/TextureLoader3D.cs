using Godot;

namespace RTS.Core
{
	// 伪 3D 贴图加载：直接读 PNG 生成 ImageTexture，
	// 不依赖 Godot 编辑器导入缓存（新增图片后无需手动导入）
	public static class TextureLoader3D
	{
		public static Texture2D LoadPng(string resPath)
		{
			// 优先走正式资源加载（导出后有效）
			if (ResourceLoader.Exists(resPath))
			{
				var imported = GD.Load<Texture2D>(resPath);
				if (imported != null)
					return imported;
			}

			// 开发期兜底：直接读 PNG（未导入也能显示）
			if (!FileAccess.FileExists(resPath))
				return null;

			Image image = Image.LoadFromFile(resPath);
			if (image == null)
				return null;

			return ImageTexture.CreateFromImage(image);
		}

		// =========================================================
		// 程序化地形贴图（PBR 三件套）
		//
		// 由 `Scripts/Tools/GenTerrainTextures.gd` 生成到
		//   res://ArtRes/imgs3d/terrain/<Name>_{Albedo,Normal,Roughness}.png
		//
		// 老的 res://ArtRes/imgs3d/<Name>.png 是 64x64 的纯色占位图
		// （实测独立颜色数 3~14），这里做**渐进替换**：
		// 新贴图存在就用新的，不存在就退回旧的 ——
		// 这样即使有人删了 terrain 目录、或者旧地图还没重新生成，
		// 也不会出现"地面整个变黑/变紫"。
		// =========================================================

		public const string TerrainDir = "res://ArtRes/imgs3d/terrain";

		/// <summary>地形贴图名（Grass / Wall / NanoCreep / PlantCreep）。</summary>
		public enum TerrainTexture
		{
			Grass,
			Wall,
			NanoCreep,
			PlantCreep,
		}

		/// <summary>
		/// 是否禁用 PBR（诊断/兜底）。设 `TERRAIN_NO_PBR=1` 时只上 albedo，
		/// 不上法线与粗糙度 —— 用来区分"albedo 本身有问题"与"法线/粗糙度接错"。
		/// </summary>
		private static bool PbrDisabled =>
			System.Environment.GetEnvironmentVariable("TERRAIN_NO_PBR") == "1";

		/// <summary>
		/// 把 PBR 三件套写进材质。
		/// 优先用新生成的高精度贴图；缺失时退回旧的单张 albedo。
		/// </summary>
		public static void ApplyTerrainPbr(BaseMaterial3D mat, TerrainTexture kind,
			string legacyAlbedoPath)
		{
			if (mat == null)
				return;

			string name = kind.ToString();
			string albedoPath = $"{TerrainDir}/{name}_Albedo.png";
			string normalPath = $"{TerrainDir}/{name}_Normal.png";
			string roughPath = $"{TerrainDir}/{name}_Roughness.png";

			var albedo = LoadPng(albedoPath);
			bool usingNew = albedo != null;

			if (!usingNew)
				albedo = LoadPng(legacyAlbedoPath);
			if (albedo == null)
				return;

			mat.AlbedoTexture = albedo;

			// 诊断：把"到底加载到了哪张图、是不是 null"打出来。
			// 之前一直靠"看画面颜色"推断，绕了很多轮；直接把绑定结果打出来最快。
			if (System.Environment.GetEnvironmentVariable("TERRAIN_DEBUG") == "1")
			{
				GD.Print($"[Tex] {name}: 加载 albedo={DescribeTexture(albedo)} " +
					$"（新图={(usingNew ? "有" : "无，走旧图回退")}）");
			}

			if (usingNew && !PbrDisabled)
			{
				// 三平面 UV 由调用方决定（地面用逐格 UV，墙用立方体 UV），
				// 这里只负责贴图本身与采样方式。
				mat.NormalEnabled = true;
				mat.NormalTexture = LoadPng(normalPath);
				// 法线强度刻意压得很低：这是**俯视** RTS，
				// 相机大范围缩放时法线细节会退化成高频噪点（"地面在闪/在爬"）。
				// 0.35 只提供一点表面起伏感，不抢地形可读性。
				mat.NormalScale = kind switch
				{
					// 墙面：板缝是内嵌结构，两侧斜面朝向相反，
					// 法线一强就在缝边出品红/青色镶边，而墙占屏幕面积最大。
					TerrainTexture.Wall => 0.45f,
					TerrainTexture.Grass => 0.30f,
					_ => 0.40f,
				};

				mat.RoughnessTexture = LoadPng(roughPath);
				mat.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Red;
			}

			// 高精度贴图必须按 mip 采样：512 贴图铺满大平面时，
			// 没有 mip 链会在缩远时剧烈闪烁（这也是旧 64px 贴图的毛病之一）。
			mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
		}

		/// <summary>把纹理描述成可读文本（诊断用）。</summary>
		private static string DescribeTexture(Texture2D tex)
		{
			if (tex == null) return "<null>";
			return $"{tex.GetWidth()}x{tex.GetHeight()}({tex.ResourcePath.GetFile()})";
		}
	}
}
