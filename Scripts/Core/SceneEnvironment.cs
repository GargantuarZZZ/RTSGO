using Godot;

namespace RTS.Core
{
	// =========================================================
	// 全局渲染环境（自动加载单例）
	//
	// 为什么需要它：**本项目此前没有任何 WorldEnvironment**。
	// 全项目 grep `WorldEnvironment|Environment|Sky` 命中 0 —— 于是：
	//   · 没有环境光   → 背对太阳的面直接是全黑（不是"暗"，是没有光照项）
	//   · 没有天空     → 背景是清屏色
	//   · 没有色调映射 → 默认线性输出，亮部硬切
	//   · 没有泛光     → 所有"发光"特效只是纯色块；
	//                    而 `EmissionEnergyMultiplier` 更是毫无意义
	//                    （Unshaded 下引擎本来就忽略 Emission）
	//
	// 做成 autoload 而不是往 4 个 .tscn 里各塞一份：
	//   1. 只有一份配置，不会出现"改了 main 忘了 main_1v1"；
	//   2. 任何新场景（测试/压测/工具）自动获得同一套观感；
	//   3. WorldEnvironment 是"当前场景内生效"的节点，全局单例持有唯一
	//      一份 Environment 资源，不会两份互相打架。
	//
	// 挂载策略：不用 NodeAdded 事件（会为每个节点触发，且时序不稳），
	// 改为低频自检（默认每 0.5 秒）。理由是本项目存在**不换 CurrentScene** 的
	// 启动路径（教程/离线直达是往根节点加子场景），只靠一次 _Ready 会漏挂。
	// 自检只是比较节点引用，开销可忽略。
	//
	// 线程约定：纯表现层，只在主线程，不碰模拟。
	// =========================================================

	public partial class SceneEnvironment : Node
	{
		/// <summary>当前生效的环境资源（全局唯一一份，改参数全局生效）。</summary>
		public static Godot.Environment Current { get; private set; }

		/// <summary>自检间隔（秒）。0 = 只在 _Ready 时挂一次。</summary>
		[Export] public float EnsureIntervalSeconds = 0.5f;

		/// <summary>
		/// 太阳阴影覆盖距离（世界单位，1 格 = 64）。
		///
		/// 取值是**画质与性能的折中**：阴影贴图分辨率固定，覆盖越大每 texel 越粗，
		/// 阴影边缘越糊。相机默认高度 2500、最高可拉到 6000：
		///   · 给到 6000 → 覆盖全部视野，但单位投影会糊成一团，白掉一张阴影图；
		///   · 给到 2500 → 与默认视高一致，投影清晰，超出部分自然没有影子。
		///     拉远到 6000 时"远处没影子"在俯视 RTS 里几乎看不出来。
		/// 所以选 2500。
		/// </summary>
		[Export] public float SunShadowMaxDistance = 2500f;

		/// <summary>太阳光强度与颜色（与 SceneEnvironment 的环境光搭配过）。</summary>
		[Export] public float SunEnergy = 1.15f;
		[Export] public Color SunColor = new Color(1.0f, 0.97f, 0.92f);

		private double _timer;
		private static bool _loggedOnce;

		public override void _Ready()
		{
			// 环境是表现层资源：暂停/切场景也应当继续存在
			ProcessMode = ProcessModeEnum.Always;

			// 构造与 _loggedOnce 无关，保证多实例（理论上不会）也不会拿到 null
			Current ??= Build();

			Ensure();
		}

		public override void _Process(double delta)
		{
			if (EnsureIntervalSeconds <= 0f)
				return;

			_timer += delta;
			if (_timer < EnsureIntervalSeconds)
				return;
			_timer = 0.0;

			Ensure();
		}

		/// <summary>
		/// 确保当前场景里有且只有一个 WorldEnvironment，且用的是我们的 Environment。
		/// 幂等：已存在就只刷新引用，不重复添加。
		/// </summary>
		public void Ensure()
		{
			Current ??= Build();

			var scene = GetTree()?.CurrentScene;
			if (scene == null) return;

			WorldEnvironment target = null;
			bool created = false;
			foreach (var child in scene.GetChildren())
			{
				if (child is not WorldEnvironment we) continue;

				if (target == null)
				{
					target = we;
				}
				else
				{
					// 场景自带了不止一个：多余的清掉，避免叠加
					we.QueueFree();
				}
			}

			if (target == null)
			{
				target = new WorldEnvironment { Name = "WorldEnvironment" };
				scene.AddChild(target);
				created = true;
			}

			bool envChanged = target.Environment != Current;
			if (envChanged)
				target.Environment = Current;

			// 顺序很重要：LogOnce 里的阴影统计必须在 ConfigureSun **之后**取，
			// 否则打印的是"配置前"的值。
			// （这里踩过：日志一直显示"太阳阴影=0 个光源"，一度以为光照没生效，
			//   实际是诊断跑在配置前面 —— 先怀疑自己的测量，再怀疑被测对象。）
			ConfigureSun(scene);

			if ((created || envChanged) && !_loggedOnce)
				LogOnce(scene, created ? "新建" : "刷新");
		}

		/// <summary>
		/// 统一配置场景里的平行光（"太阳"）。
		///
		/// 为什么这件事必须做：4 个主场景里的 DirectionalLight3D **都只写了 transform**，
		/// 而 Godot 的 `shadow_enabled` 默认是 **false** ——
		/// 也就是说整个项目此前**没有任何阴影**，所有单位/建筑看起来都像贴在地上的纸片
		/// （没有落地投影 = 无法判断单位站在哪一格、离地面多高）。
		///
		/// 放在这里而不是改 4 份 .tscn：一份配置、不遗漏，新场景自动生效。
		///
		/// 用显式递归遍历而不是 `Node.FindChildren("*", "DirectionalLight3D")`：
		/// 本项目多处用 FindChildren 过滤类型，行为在不同 Godot 版本上不够直观，
		/// 递归遍历结果确定、不依赖该 API 的类型匹配语义。
		/// （注：最初以为是 FindChildren 查不到 Sun，实际是统计跑在配置之前 —— 见 Ensure 的注释。）
		/// </summary>
		private void ConfigureSun(Node scene)
		{
			int seen = 0;
			// "有 3D 内容"的判据是**存在可渲染的 3D 网格**，不是"存在 Node3D"。
			// 纯 Node3D 容器（分层用的空节点、测试探针）不需要光，
			// 用 Node3D 判断会误报（菜单里挂个空 Node3D 就会告警）。
			bool hasRenderable3D = false;
			var stack = new System.Collections.Generic.Stack<Node>();
			stack.Push(scene);

			while (stack.Count > 0)
			{
				var node = stack.Pop();
				if (node is MeshInstance3D or MultiMeshInstance3D) hasRenderable3D = true;
				if (node is DirectionalLight3D sun)
				{
					seen++;
					ConfigureOneSun(sun);
				}

				foreach (var child in node.GetChildren())
					stack.Push(child);
			}

			// 只在"确实有可渲染 3D 内容"的场景里报缺光：
			// 纯 2D 界面（主菜单/房间列表）本来就没有也不该有 3D 光源，
			// 对它报警只会变成噪音，掩盖真正的问题场景。
			if (seen == 0 && hasRenderable3D &&
				System.Environment.GetEnvironmentVariable("ART_DEBUG") == "1" && !_sunMissingLogged)
			{
				_sunMissingLogged = true;
				GD.PrintErr($"[Art] 场景 '{scene.Name}' 有可渲染的 3D 网格但没有 DirectionalLight3D ——" +
					"阴影无法开启，单位会看起来像贴在地上的纸片。");
			}
		}

		private void ConfigureOneSun(DirectionalLight3D sun)
		{
			sun.ShadowEnabled = true;

			// 正交平行光：用角度决定阴影边缘的柔度。
			// 0 会非常硬（锯齿明显），给一点点让边缘自然。
			sun.LightAngularDistance = 0.5f;

			sun.LightEnergy = SunEnergy;
			sun.LightColor = SunColor;

			// 阴影参数按本项目尺度调（世界单位，1 格 = 64）。
			// max_distance：见 SunShadowMaxDistance 的说明（画质/性能折中）。
			sun.DirectionalShadowMaxDistance = SunShadowMaxDistance;
			sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
			// 4 级级联：近处更清晰、远处靠粗级联兜住
			sun.DirectionalShadowSplit1 = 0.1f;
			sun.DirectionalShadowSplit2 = 0.2f;
			sun.DirectionalShadowSplit3 = 0.5f;
			// bias 防阴影痤疮（自阴影噪点）：默认 0.1 在 64 单位格尺度下偏小
			sun.ShadowBias = 0.06f;
			sun.ShadowNormalBias = 1.5f;
			sun.DirectionalShadowBlendSplits = true;
		}

		private static bool _sunMissingLogged;

		private static void LogOnce(Node scene, string how)
		{
			if (_loggedOnce) return;
			if (System.Environment.GetEnvironmentVariable("ART_DEBUG") != "1") return;

			_loggedOnce = true;
			GD.Print($"[Art] 环境已{how}并挂到场景 '{scene.Name}'：" +
				$"天空={(Current.Sky != null)} 环境光={Current.AmbientLightEnergy:0.##} " +
				$"泛光={Current.GlowEnabled}(阈值 {Current.GlowHdrThreshold:0.##}, 强度 {Current.GlowIntensity:0.##}) " +
				$"色调映射={Current.TonemapMode} 调整={Current.AdjustmentEnabled} " +
				$"太阳阴影={CountShadowCasters(scene)} 个光源");
		}

		/// <summary>统计场景里已开阴影的平行光（诊断用）。</summary>
		private static int CountShadowCasters(Node scene)
		{
			int n = 0;
			var stack = new System.Collections.Generic.Stack<Node>();
			stack.Push(scene);
			while (stack.Count > 0)
			{
				var node = stack.Pop();
				if (node is DirectionalLight3D d && d.ShadowEnabled) n++;
				foreach (var child in node.GetChildren()) stack.Push(child);
			}
			return n;
		}

		private static Godot.Environment Build()
		{
			return new Godot.Environment
			{
				// ---- 背景：渐变天空 ----
				// 不用程序化昼夜/体积云：RTS 是固定 55° 俯视，天空只负责
				// "别让背景是纯清屏色"，重天空是纯浪费。
				BackgroundMode = Godot.Environment.BGMode.Sky,
				Sky = new Sky
				{
					SkyMaterial = new ProceduralSkyMaterial
					{
						SkyTopColor = new Color(0.32f, 0.46f, 0.68f),
						SkyHorizonColor = new Color(0.68f, 0.73f, 0.80f),
						GroundBottomColor = new Color(0.22f, 0.24f, 0.26f),
						GroundHorizonColor = new Color(0.42f, 0.44f, 0.46f),
						SunAngleMax = 30f,
						SunCurve = 0.15f,
					},
				},
				SkyCustomFov = 70f,

				// ---- 环境光：这是"背光面全黑"的解药 ----
				// 用"固定色 + 一半天空贡献"的混合：纯天空会让所有阴面偏蓝过重，
				// 纯固定色又会丢掉天空的方向感。
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = new Color(0.55f, 0.60f, 0.68f),
				AmbientLightSkyContribution = 0.5f,
				AmbientLightEnergy = 0.35f,

				// ---- 反射：给一点天空反射，免得金属面是死黑 ----
				ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,

				// ---- 色调映射 ----
				// Filmic 比默认线性在暗部更耐看、亮部不硬切。
				TonemapMode = Godot.Environment.ToneMapper.Filmic,
				TonemapExposure = 1.0f,
				TonemapWhite = 1.0f,

				// ---- 泛光：本次美术优化的核心目标 ----
				// 阈值 0.9：只有被 GlowMaterialFactory.Boost 抬到 >1.0 的像素才溢出，
				// 普通地形/单位（albedo ≤ 1）不参与，不会整屏发雾。
				GlowEnabled = true,
				GlowIntensity = 0.35f,
				GlowStrength = 1.0f,
				GlowBloom = 0.0f,
				GlowHdrThreshold = 0.9f,
				GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Softlight,
				GlowNormalized = true,

				// ---- 刻意不启用 ----
				// SSAO / SSIL / SDFGI / 体积雾：全是大范围开销，而这是固定俯视角的
				// RTS 地图，收益极低、掉帧明显。
				// 阴影也不在这里开 —— 它属于场景里的 DirectionalLight3D，且要按
				// 地图尺度调 directional_shadow_max_distance，不能一刀切。
				SsaoEnabled = false,
				SsilEnabled = false,
				SdfgiEnabled = false,
				FogEnabled = false,
				VolumetricFogEnabled = false,

				// 抗锯齿（ScreenSpaceAA / MSAA）是 **Viewport** 属性而非 Environment，
				// 所以放在 project.godot 的 [rendering] 里统一配。

				// ---- 调整：轻微提饱和 + 提对比，抵消天空环境光带来的灰 ----
				AdjustmentEnabled = true,
				AdjustmentSaturation = 1.08f,
				AdjustmentContrast = 1.03f,
				AdjustmentBrightness = 1.0f,
			};
		}
	}
}
