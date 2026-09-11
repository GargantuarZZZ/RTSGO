using Godot;
using RTS.Data;
using RTS.Core;
using RTS.Units;
using System.Collections.Generic;

// 伪 3D 视觉模块：优先加载 GLB 模型（UnitModelLibrary 映射表），
// 没有映射时退回“贴图立方体”（BoxMesh + StandardMaterial3D）。
// 材质负责队伍着色 / 蓝图半透明 / 战争迷雾灰显。
[GlobalClass]
public partial class UnitVisuals : Node
{
	private Node3D _rootNode;
	private IEntity _entity;
	private MeshInstance3D _mesh;
	private StandardMaterial3D _material;
	private Node3D _weaponMount;
	private UnitHealthBar3D _healthBar3D;
	private SelectionRing3D _selectionRing3D;
	private bool _isSelected = false;
	private bool _ringDeployed = false;
	private DeployCircleVisual _deployCircle;
	private DeployCircleVisual _rangeCircle;

	private bool _isBlueprint = false;
	private bool _isConstruction = false;
	private bool _fogHidden = false;

	// ---------- GLB 模型 ----------
	private Node3D _modelRoot;
	private AnimationPlayer _animPlayer;
	private UnitModelInfo _modelInfo;
	private readonly List<MeshInstance3D> _modelMeshes = new();
	private string _currentAnim = "";
	private string _cachedIdleName = "";
	private string _cachedMoveName = "";
	private bool _animNamesCached = false;
	private float _animAccumulator = 0f;
	private Vector3 _lastAnimPos;
	private bool _hasAnimPos = false;
	private float _baseRotationY = 0f;
	private Vector3 _restPosition;
	private Node3D _aimNode;
	private bool _aimInitialized = false;
	private bool _hasAimTarget = false;
	private Vector2 _lastAimRequest = new Vector2(float.MinValue, float.MinValue);
	private float _aimCurrentYaw = 0f;
	private float _aimTargetYaw = 0f;
	private float _aimSmoothSpeed = 10f;
	private Node3D _barrelNode;
	private Basis _barrelRestBasis;
	private float _barrelCurrentPitch = 0f;
	private float _barrelTargetPitch = 0f;
	private float _bodyRestYaw = 0f;
	private Node3D _muzzleNode;
	private bool _deployAimInit = false;
	private Quaternion _deployAimCurrent = Quaternion.Identity;
	private int _lastDeployState = 0;
	private bool _deployTurnActive = false;
	private Quaternion _deployTurnStart = Quaternion.Identity;
	private Quaternion _deployTurnEnd = Quaternion.Identity;
	private float _deployTurnTime = 0f;
	private float _deployTurnDuration = 1f;

	// ---------- MultiMesh 批渲染（枪兵等高频小单位） ----------
	private bool _batchMode = false;
	private int _batchSlot = -1;
	private string _batchModelPath = "";
	private string _batchAnimName = "";
	private bool _movingVisualState = false;
	private int _movingDebounceFrames = 0;
	private Color _batchColor = Colors.White;
	private bool _batchDead = false;
	private Transform3D _lastBatchTransform = new Transform3D(Basis.Identity.Scaled(Vector3.Zero), new Vector3(float.MinValue, float.MinValue, float.MinValue));
	private Color _lastBatchColor = new Color(-1f, -1f, -1f, -1f);
	private bool _batchTransformValid = false;
	private Vector3 _batchVisualPos;
	private bool _batchVisualValid = false;
	private int _barBatchSlot = -1;
	private float _batchBarY = 0f;
	private float _batchBarWidth = 48f;
	private int _barUpdateFrame = 0;

	private static readonly Dictionary<string, Node3D> ModelSceneCache = new();

	public void Initialize(Node3D root, IEntity entity)
	{
		_rootNode = root;
		_entity = entity;
		_weaponMount = _rootNode.GetNodeOrNull<Node3D>("Visuals/WeaponMount");
		BuildMesh();
		ResolveAimNode();
		UpdateTeamColor();
	}

	private void BuildMesh()
	{
		if (_entity == null)
			return;
		float barY;
		var prefabModel = _rootNode.GetNodeOrNull<Node3D>("Model");
		_modelInfo = UnitModelLibrary.Get(_entity.DisplayName);

		// 高频单位优先走 MultiMesh 批量渲染：即使预制体里自带 Model，运行时也不挂节点路径。
		if (_modelInfo != null && IsBatchableModel(_entity.DisplayName))
		{
			// 预制体自带的 Model（完整骨架场景）只负责“编辑器里的静态预览”，
			// 批量渲染用 VAT 实例代替它；不隐藏会导致同一单位同时显示静态+动画两个模型。
			if (prefabModel != null)
			{
				prefabModel.Visible = false;
				prefabModel.QueueFree();
			}
			barY = BuildBatchedModel(_modelInfo);
		}
		else if (prefabModel != null)
		{
			barY = BuildPrefabModel(prefabModel);
		}
		else if (_modelInfo != null && TryBuildModel(_modelInfo))
		{
			barY = _modelInfo.TopY + 12f;
		}
		else
		{
			barY = BuildCubeMesh();
		}

		BuildOverlays(barY);
	}

	private float BuildPrefabModel(Node3D model)
	{
		_modelRoot = model;
		var lib = UnitModelLibrary.Get(_entity.DisplayName);
		_modelInfo = lib != null
			? new UnitModelInfo
			{
				ModelPath = lib.ModelPath,
				Scale = 1f,
				OffsetY = 0f,
				TurnWhileMoving = lib.TurnWhileMoving,
				IdleAnimation = lib.IdleAnimation,
				MoveAnimation = lib.MoveAnimation,
				FacingBias = lib.FacingBias,
				RotateToAim = lib.RotateToAim,
				BarrelPath = lib.BarrelPath,
				BarrelNormalPitchDegrees = lib.BarrelNormalPitchDegrees,
				BarrelDeployPitchDegrees = lib.BarrelDeployPitchDegrees,
				BottomMuzzlePath = lib.BottomMuzzlePath,
				DeployAimWithBottom = lib.DeployAimWithBottom
			}
			: new UnitModelInfo
			{
				Scale = 1f,
				OffsetY = 0f,
				TurnWhileMoving = true,
				IdleAnimation = "Idle",
				MoveAnimation = "Walk"
			};
		_baseRotationY = 0f;
		_restPosition = model.Position;

		_animPlayer = FindAnimationPlayer(model);
		if (_animPlayer != null)
		{
			_animPlayer.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
			_animAccumulator = -GetAnimPhaseOffset();
			PlayAnimation(_modelInfo.IdleAnimation);
			CacheAnimationNames();
		}

		// 炮塔/炮管在 Visuals/WeaponMount 下，也一起收集，保证染色/包围盒覆盖整个模型。
		CollectModelMeshes(_rootNode);

		if (TryGetModelBounds(out _, out var half))
		{
			_modelInfo.YMin = 0f;
			_modelInfo.YMax = Mathf.Max(0.01f, half.Y * 2f);
		}

		return _modelInfo.TopY + 12f;
	}

	// 高频小单位走 MultiMesh：不实例化 60+ 节点的骨骼模型
	private static bool IsBatchableModel(string name)
	{
		// 顶点动画纹理（VAT）批量渲染：只有已烘焙动画数据的模型才走批量路径，
		// 未烘焙的模型继续走预制体/GLB 常规渲染。
		var info = UnitModelLibrary.Get(name);
		return info != null && RTS.Core.UnitVatRenderer.HasVat(info.ModelPath);
	}

	private float BuildBatchedModel(UnitModelInfo info)
	{
		_batchMode = true;
		_baseRotationY = Mathf.DegToRad(info.RotationY);
		RTS.Core.UnitVatRenderer.EnsureParent(Main.Instance ?? _rootNode.GetTree().CurrentScene);
		_batchModelPath = info.ModelPath;
		_batchSlot = RTS.Core.UnitVatRenderer.Register(info.ModelPath);
		_lastAnimPos = _rootNode.GlobalPosition;
		_hasAnimPos = true;
		_batchColor = GetBatchColor();
		_batchAnimName = "";
		// 视觉更新统一由 VatFlusher 驱动，关闭自身的 _Process 分发
		SetProcess(false);
		RTS.Units.Unit batchUnit = _rootNode as RTS.Units.Unit;
		batchUnit?.SetProcess(false);
		RTS.Core.UnitVatRenderer.RegisterManagedVisual(this, batchUnit);
		UpdateBatch();
		return info.TopY + 12f;
	}

	public int LastSnapTick = -1;

	public bool TryGetVisualTarget(out Vector3 pos)
	{
		pos = default;
		if (_entity?.LogicEntity is RTS.Simulation.SimUnit su)
		{
			if (RTS.Core.SimManager.Instance != null &&
				RTS.Core.SimManager.Instance.TryGetUnitVisualState(su.ID, out float sx, out float sy, out _, out _))
				pos = new Vector3(sx, 0f, sy);
			else
				pos = new Vector3((float)su.Position.X, 0f, (float)su.Position.Y);
			return true;
		}
		return false;
	}

	public void UpdateBatch()
	{
		if (_batchSlot < 0)
			return;

		// 用逻辑坐标 + 自维护插值：生成瞬间节点的 GlobalPosition 不可靠，
		// 且逻辑 20Hz 更新需要插值避免一顿一顿
		Vector3 pos;
		if (_entity?.LogicEntity is RTS.Simulation.SimUnit su)
		{
			Vector3 target;
			if (RTS.Core.SimManager.Instance != null &&
				RTS.Core.SimManager.Instance.TryGetUnitVisualState(su.ID, out float sx, out float sy, out _, out _))
				target = new Vector3(sx, 0f, sy);
			else
				target = new Vector3((float)su.Position.X, 0f, (float)su.Position.Y);
			if (!_batchVisualValid)
			{
				_batchVisualPos = target;
				_batchVisualValid = true;
			}
			else if (!_batchDead)
			{
				_batchVisualPos = _batchVisualPos.Lerp(target, Mathf.Clamp(RTS.Core.UnitVatRenderer.LastFrameDelta * 15f, 0f, 1f));
			}
			else
			{
				// 死亡下沉：跟随节点 tween（节点 GlobalPosition 在沉）
				_batchVisualPos = new Vector3(_rootNode.GlobalPosition.X, 0f, _rootNode.GlobalPosition.Z);
			}
			pos = _batchVisualPos;
		}
		else
			// Unit._Ready 里 SimUnitData 在 Visuals.Initialize 之后才创建；
			// 此时本地 Position 就是 EntitySpawner 注入的逻辑坐标
			pos = new Vector3(_rootNode.Position.X, 0f, _rootNode.Position.Z);
		float rotY = _baseRotationY;
		bool movingNow = IsMoving();

		if (_modelInfo.TurnWhileMoving && movingNow)
		{
			Vector3 d = pos - _lastAnimPos;
			if (d.LengthSquared() > 0.01f)
				rotY = _baseRotationY + Mathf.Atan2(d.X, d.Z) + Mathf.DegToRad(_modelInfo.FacingBias);
		}
		_lastAnimPos = pos;

		if (_fogHidden)
		{
			// 隐藏单位：用远下方合法矩阵，不用零缩放（零缩放 → 退化 AABB → 整屏闪烁）。
			RTS.Core.UnitVatRenderer.SetHidden(_batchModelPath, _batchSlot);
			_batchTransformValid = false;
			return;
		}

		// VAT 动画在 GPU 上自动播放，这里只在 Idle/Move 切换时下发一次。
		string wantAnim = movingNow ? _modelInfo.MoveAnimation : _modelInfo.IdleAnimation;
		if (wantAnim != _batchAnimName)
		{
			RTS.Core.UnitVatRenderer.Play(_batchModelPath, _batchSlot, wantAnim, 0.15f);
			_batchAnimName = wantAnim;
		}

		float scale = _modelInfo.Scale;
		var basis = new Basis(Vector3.Up, rotY);
		basis = basis.Scaled(Vector3.One * scale);
		pos.Y += _modelInfo.OffsetY;

		var xf = new Transform3D(basis, pos);
		if (!_batchTransformValid || xf != _lastBatchTransform)
		{
			RTS.Core.UnitVatRenderer.SetTransform(_batchModelPath, _batchSlot, xf);
			_lastBatchTransform = xf;
			_batchTransformValid = true;
		}

		if (_lastBatchColor != _batchColor)
		{
			RTS.Core.UnitVatRenderer.SetColor(_batchModelPath, _batchSlot, _batchColor);
			_lastBatchColor = _batchColor;
		}

		// 批渲染单位没有独立血条节点：血条由批渲染器直接驱动
		if (_barBatchSlot >= 0 && _entity?.LifeModule != null && (_barUpdateFrame++ & 1) == 0)
		{
			float ratio = _entity.LifeModule.MaxHp > 0f
				? Mathf.Clamp(_entity.LifeModule.CurrentHp / _entity.LifeModule.MaxHp, 0f, 1f)
				: 1f;
			RTS.Core.HealthBarBatchRenderer.Update(
				_barBatchSlot,
				new Vector3(pos.X, _batchBarY, pos.Z),
				_batchBarWidth,
				ratio,
				!_fogHidden);
		}
	}

	private Color GetBatchColor()
	{
		if (_batchDead)
			return new Color(0.45f, 0.45f, 0.45f);
		if (_isBlueprint)
			return new Color(0.3f, 0.7f, 1f);
		if (_isConstruction)
			return new Color(1f, 0.75f, 0.2f);
		if (_fogHidden)
			return new Color(0.5f, 0.5f, 0.5f);

		// 与 ApplyTint 同样的哨兵约定：Main 未就绪时不判定敌我（保持白色），
		// 而不是拿 0 去和真实队伍号比较（那会把友军当敌军）。
		int myLocalId = Main.Instance != null ? Main.Instance.LocalPlayerID : -1;
		if (myLocalId < 0)
			return Colors.White;

		return (_entity.TeamID == myLocalId || _entity.TeamID == -1)
			? Colors.White
			: new Color(1f, 0.45f, 0.45f);
	}

	// ===================== GLB 模型 =====================

	private bool TryBuildModel(UnitModelInfo info)
	{
		if (info.ModelPath.ToLowerInvariant().EndsWith(".obj"))
			return TryBuildObjModel(info);

		if (!ModelSceneCache.TryGetValue(info.ModelPath, out var template))
		{
			template = LoadModelTemplate(info.ModelPath);
			if (template == null)
				return false;

			ModelSceneCache[info.ModelPath] = template;
		}

		var instance = (Node3D)template.Duplicate();
		instance.Name = "Model";
		instance.Scale = Vector3.One * info.Scale;
		instance.Position = new Vector3(0f, info.OffsetY, 0f);
		if (info.RotationY != 0f)
			instance.Rotation = new Vector3(0f, Mathf.DegToRad(info.RotationY), 0f);
		_baseRotationY = Mathf.DegToRad(info.RotationY);

		_rootNode.AddChild(instance);
		_modelRoot = instance;

		_animPlayer = FindAnimationPlayer(instance);
		if (_animPlayer != null)
		{
			// 骨骼动画手动节流：不随渲染帧 60fps 全量推进，由 _Process 按 15fps 分批 advance，
			// 200+ 枪兵这类带骨骼模型时能省掉约 3/4 的动画计算量。
			_animPlayer.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
			_animAccumulator = -GetAnimPhaseOffset();
			PlayAnimation(info.IdleAnimation);
			CacheAnimationNames();
		}

		CollectModelMeshes(instance);

		// 自动居中：用模型自身（局部）包围盒的 XZ 中心对齐实体原点，
		// 不能用世界包围盒，否则会把模型按出生坐标反向推走
		if (TryGetModelLocalBounds(out var center, out _))
		{
			instance.Position = new Vector3(
				instance.Position.X - center.X + info.CenterOffsetX * info.Scale,
				instance.Position.Y,
				instance.Position.Z - center.Z + info.CenterOffsetZ * info.Scale
			);
		}
		_restPosition = instance.Position;

		return true;
	}

	private static Node3D LoadModelTemplate(string modelPath)
	{
		// 导出包里 .glb 会以 .remap 形式存在（如 Planet.glb.remap），
		// 直接 GltfDocument.AppendFromFile 会 FileNotFound。
		// 优先用 ResourceLoader 加载编辑器已导入的场景（导出/编辑器都有效）。
		var packed = ResourceLoader.Load<PackedScene>(modelPath);
		if (packed != null)
		{
			var root = packed.Instantiate();
			if (root is Node3D node3D)
				return node3D;

			root?.QueueFree();
		}

		// 开发期兜底：没有导入缓存时直接读原始 GLB
		var state = new GltfState();
		var doc = new GltfDocument();
		var err = doc.AppendFromFile(modelPath, state);
		if (err != Error.Ok)
		{
			GD.PrintErr($"[UnitVisuals] 模型加载失败: {modelPath} ({err})");
			return null;
		}

		return doc.GenerateScene(state) as Node3D;
	}

	private bool TryBuildObjModel(UnitModelInfo info)
	{
		var mesh = ResourceLoader.Load<Mesh>(info.ModelPath);
		if (mesh == null)
		{
			GD.PrintErr($"[UnitVisuals] OBJ 模型加载失败: {info.ModelPath}");
			return false;
		}

		var instance = new MeshInstance3D
		{
			Name = "Model",
			Mesh = mesh
		};

		float scale = info.Scale;
		float offsetY = info.OffsetY;

		if (info.AutoFitFootprint > 0f)
		{
			Aabb raw = mesh.GetAabb();
			float spanX = raw.Size.X;
			float spanZ = raw.Size.Z;
			if (spanX > 0.001f || spanZ > 0.001f)
			{
				scale = info.AutoFitFootprint / Mathf.Max(spanX, spanZ);
				offsetY = -raw.Position.Y * scale;

				instance.Scale = Vector3.One * scale;
				instance.Position = new Vector3(
					-(raw.Position.X + raw.Size.X * 0.5f) * scale,
					offsetY,
					-(raw.Position.Z + raw.Size.Z * 0.5f) * scale
				);
				if (info.RotationY != 0f)
					instance.Rotation = new Vector3(0f, Mathf.DegToRad(info.RotationY), 0f);

				// 同步尺寸数据，让血条/顶部偏移按实际缩放后的模型计算
				info.Scale = scale;
				info.OffsetY = offsetY;
				info.YMin = raw.Position.Y;
				info.YMax = raw.Position.Y + raw.Size.Y;
			}
		}
		else
		{
			instance.Scale = Vector3.One * scale;
			instance.Position = new Vector3(0f, offsetY, 0f);
			if (info.RotationY != 0f)
				instance.Rotation = new Vector3(0f, Mathf.DegToRad(info.RotationY), 0f);
		}
		if (info.CenterOffsetX != 0f || info.CenterOffsetZ != 0f)
		{
			instance.Position += new Vector3(
				info.CenterOffsetX * scale,
				0f,
				info.CenterOffsetZ * scale);
		}

		_rootNode.AddChild(instance);
		_modelRoot = instance;
		_modelMeshes.Add(instance);
		_restPosition = instance.Position;
		return true;
	}

	private static AnimationPlayer FindAnimationPlayer(Node node)
	{
		if (node is AnimationPlayer ap)
			return ap;

		foreach (Node child in node.GetChildren())
		{
			var found = FindAnimationPlayer(child);
			if (found != null)
				return found;
		}

		return null;
	}

	private void CollectModelMeshes(Node node)
	{
		if (node is MeshInstance3D mi)
			_modelMeshes.Add(mi);

		foreach (Node child in node.GetChildren())
			CollectModelMeshes(child);
	}

	private void PlayAnimation(string token)
	{
		if (_animPlayer == null || string.IsNullOrEmpty(token))
			return;

		if (!_animNamesCached)
			CacheAnimationNames();

		string name = token == _modelInfo.IdleAnimation ? _cachedIdleName : _cachedMoveName;
		if (string.IsNullOrEmpty(name) || _currentAnim == name)
			return;

		var anim = _animPlayer.GetAnimation(name);
		if (anim != null)
			anim.LoopMode = Animation.LoopModeEnum.Linear;
		_animPlayer.Play(name);
		_currentAnim = name;
	}

	// 每个模型只扫描一次动画名列表，之后用缓存名，避免每帧每单位都做字符串匹配
	private void CacheAnimationNames()
	{
		_cachedIdleName = FindAnimationName(_modelInfo.IdleAnimation);
		_cachedMoveName = FindAnimationName(_modelInfo.MoveAnimation);
		_animNamesCached = true;
	}

	private string FindAnimationName(string token)
	{
		if (_animPlayer == null || string.IsNullOrEmpty(token))
			return "";

		foreach (var animName in _animPlayer.GetAnimationList())
		{
			string name = animName.ToString();
			if (name.Contains(token, System.StringComparison.OrdinalIgnoreCase))
				return name;
		}

		return "";
	}

	// 相位错开：同屏大量单位不在同一帧一起推进骨骼，避免动画更新造成单帧尖峰
	private float GetAnimPhaseOffset()
	{
		long id = _entity?.LogicEntity?.ID ?? (long)_rootNode.GetInstanceId();
		const int buckets = 12;
		const float interval = 1f / 15f;
		return (id % buckets) * (interval / buckets);
	}

	private void UpdateModelAnimation(float dt)
	{
		if (_modelRoot == null || _modelInfo == null)
			return;

		Vector3 pos = _rootNode.GlobalPosition;
		Vector3 delta = pos - _lastAnimPos;
		// 优先用逻辑速度判定移动：低移速时视觉插值每帧位移太小，
		// 按位移阈值会把 Idle/Walk 一帧一换导致模型抽搐
		bool moving = false;

		if (_entity?.LogicEntity is RTS.Simulation.SimUnit su)
		{
			// 复用带迟滞的移动判定，避免碰撞推挤时静止/移动动画反复切换
			moving = IsMoving();
		}
		else
		{
			moving = _hasAnimPos && delta.Length() > Mathf.Max(0.05f, 24f * dt);
		}

		// 转向不依赖动画：配置了 TurnWhileMoving 的移动模型朝移动方向旋转（HP 等无动画模型同样生效）
		if (moving && _modelInfo.TurnWhileMoving)
		{
			float facing = Mathf.Atan2(delta.X, delta.Z);
			float yaw = _baseRotationY + facing + Mathf.DegToRad(_modelInfo.FacingBias);
			_modelRoot.Rotation = new Vector3(0f, yaw, 0f);

			// 绕指定轴心旋转（例如 SCV 绕车轮中心），避免包围盒中心不在车轴上导致转向时甩尾
			if (_modelInfo.PivotOffsetX != 0f || _modelInfo.PivotOffsetZ != 0f)
			{
				Vector3 pivot = new Vector3(
					_modelInfo.PivotOffsetX * _modelInfo.Scale,
					0f,
					_modelInfo.PivotOffsetZ * _modelInfo.Scale);
				var rot = new Basis(Vector3.Up, yaw);
				_modelRoot.Position = _restPosition + pivot - rot * pivot;
			}
		}

		_lastAnimPos = pos;
		_hasAnimPos = true;

		if (_animPlayer != null)
		{
			if (moving)
				PlayAnimation(_modelInfo.MoveAnimation);
			else
				PlayAnimation(_modelInfo.IdleAnimation);
		}
	}

	private void ApplyModelTint(Color color, bool transparent)
	{
		if (_modelRoot == null)
			return;

		foreach (var mesh in _modelMeshes)
		{
			if (mesh.Mesh == null || mesh.Mesh.GetSurfaceCount() == 0)
				continue;

			var baseMat = mesh.MaterialOverride ?? mesh.GetActiveMaterial(0);
			Material mat;
			if (baseMat != null)
				mat = (Material)baseMat.Duplicate();
			else
				mat = new StandardMaterial3D();

			if (mat is BaseMaterial3D bm)
			{
				bm.Transparency = transparent
					? BaseMaterial3D.TransparencyEnum.Alpha
					: BaseMaterial3D.TransparencyEnum.Disabled;
				bm.AlbedoColor = color;
			}
			mesh.MaterialOverride = mat;
		}
	}

	// 友军 / 中立单位恢复模型原始共享材质，减少每单位复制材质
	private void ClearModelTint()
	{
		if (_modelRoot == null)
			return;

		foreach (var mesh in _modelMeshes)
		{
			if (mesh.Mesh == null)
				continue;
			mesh.MaterialOverride = null;
		}
	}

	// ===================== 贴图立方体（旧方案） =====================

	private float BuildCubeMesh()
	{
		_mesh = new MeshInstance3D
		{
			Name = "BodyMesh"
		};

		Vector3 boxSize;

		if (_entity.IsStructure && _entity is Structure structure)
		{
			// 1 格 = 64 世界单位：建筑 = GridSize*64 的等边立方体
			float size = structure.GridSize * 64f;
			boxSize = new Vector3(size, size, size);
			_mesh.Position = new Vector3(0f, size * 0.5f, 0f);
		}
		else
		{
			// 单位：40 立方体（约 0.6 格）
			boxSize = new Vector3(40f, 40f, 40f);
			_mesh.Position = new Vector3(0f, 20f, 0f);

			if (_entity.DisplayName == "NanoBehemoth")
			{
				boxSize = new Vector3(200f, 200f, 200f);
				_mesh.Position = new Vector3(0f, 100f, 0f);
			}
		}

		_mesh.Mesh = RTS.Core.CubeMeshBuilder.Build(boxSize);
		_material = new StandardMaterial3D();
		_material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
		_material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
		_material.Uv1Scale = Vector3.One;

		string texName = _entity.DisplayName switch
		{
			"IronOre" => "Ore",
			"CommandCenter" => "Base",
			"RifleMan" => "Ganman",
			_ => _entity.DisplayName
		};
		var tex = RTS.Core.TextureLoader3D.LoadPng($"res://ArtRes/imgs3d/{texName}.png");

		if (tex != null)
		{
			_material.AlbedoTexture = tex;
		}
		else
		{
			_material.AlbedoColor = new Color(0.62f, 0.62f, 0.62f);
			AddNameLabel(boxSize);
		}

		_mesh.MaterialOverride = _material;
		_rootNode.AddChild(_mesh);
		return boxSize.Y + 12f;
	}

	private void BuildOverlays(float barY)
	{
		float barWidth = _entity.IsStructure ? 120f : 48f;
		float ringRadius = _entity.IsStructure && _entity is Structure structure
			? Mathf.Min(structure.GridSize * 32f, 160f)
			: 40f;

		// GLB 模型：血条宽度与选中圈半径跟随模型实际大小
		if (_modelRoot != null && TryGetModelBounds(out var modelCenter, out var modelHalf))
		{
			float span = Mathf.Max(modelHalf.X, modelHalf.Z) * 2f;
			barWidth = Mathf.Clamp(span, 48f, 320f);
			ringRadius = Mathf.Max(modelHalf.X, modelHalf.Z) + 10f;
		}

		if (_entity.LifeModule != null)
		{
			if (_batchMode)
			{
				// 批渲染单位：不建血条节点，血条由 UnitVisuals 直接驱动 MultiMesh
				_batchBarY = barY;
				_batchBarWidth = Mathf.Clamp(barWidth, 48f, 320f);
				RTS.Core.HealthBarBatchRenderer.EnsureParent(Main.Instance ?? GetTree()?.CurrentScene);
				_barBatchSlot = RTS.Core.HealthBarBatchRenderer.Register();
			}
			else
			{
				_healthBar3D = new UnitHealthBar3D { Name = "HealthBar3D" };
				_healthBar3D.Position = new Vector3(0f, barY, 0f);
				_healthBar3D.Setup(_entity.LifeModule, barWidth, _entity);
				_rootNode.AddChild(_healthBar3D);
			}
		}

		// 批渲染单位：选中圈按需创建，避免 400 个单位各挂一个隐藏圆盘
		if (_batchMode)
			return;

		_selectionRing3D = new SelectionRing3D { Name = "SelectionRing3D" };
		_selectionRing3D.Setup(ringRadius);

		// 绑定数量标签：贴在血条正上方一点点（地狱城怨灵 x/12）
		if (_entity is Structure boundStructure)
		{
			var boundCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(boundStructure.StructureName);
			if (boundCfg != null && boundCfg.AutoProduceMaxBound > 0)
			{
				_rootNode.AddChild(new BoundCountLabel3D
				{
					Name = "BoundCountLabel3D",
					Position = new Vector3(0f, barY + 10f, 0f)
				});
			}

			// 泰伦藻类工厂：驻扎工人计数（与怨灵计数同款）
			if (boundCfg != null && boundCfg.GarrisonCapacity > 0)
			{
				_rootNode.AddChild(new GarrisonCountLabel3D
				{
					Name = "GarrisonCountLabel3D",
					Position = new Vector3(0f, barY + 10f, 0f)
				});
			}
		}

		// 飞行单位：选中圈放回地面（当阴影标记用），不跟着模型飘在空中
		if (_entity is RTS.Units.Unit flyingUnit && flyingUnit.FlyHeight > 0f)
		{
			_selectionRing3D.Position = new Vector3(0f, -flyingUnit.FlyHeight + 1.2f, 0f);

			// 本体到地面判定环之间的细线
			var tether = new MeshInstance3D
			{
				Name = "GroundTether",
				Mesh = new CylinderMesh
				{
					TopRadius = 1.5f,
					BottomRadius = 1.5f,
					Height = flyingUnit.FlyHeight,
					RadialSegments = 6
				},
				Position = new Vector3(0f, -flyingUnit.FlyHeight * 0.5f, 0f)
			};
			tether.MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.75f, 0.85f, 1f, 0.35f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled
			};
			_rootNode.AddChild(tether);
		}

		_rootNode.AddChild(_selectionRing3D);
	}

	// 无贴图实体：头顶加文字（Label3D 自动面向相机）
	private void AddNameLabel(Vector3 boxSize)
	{
		var label = new Label3D
		{
			Name = "NameLabel",
			Text = _entity.DisplayName,
			FontSize = 96,
			OutlineSize = 12,
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			Modulate = Colors.White,
			OutlineModulate = Colors.Black,
			Position = new Vector3(0f, boxSize.Y * 0.5f + 24f, 0f)
		};
		_rootNode.AddChild(label);
	}

	public void Tick(double delta) { }

	// 供弹道特效计算“敌人表面”使用：返回模型在世界空间中的包围盒中心与半边长
	public bool TryGetModelBounds(out Vector3 center, out Vector3 half)
	{
		center = Vector3.Zero;
		half = Vector3.Zero;
		if (_batchMode && _modelInfo != null)
		{
			float h = Mathf.Max(1f, (_modelInfo.YMax - _modelInfo.YMin) * _modelInfo.Scale);
			float w = Mathf.Max(30f, (float)(_entity?.LogicEntity?.Radius ?? (FixMath.NET.Fix64)32m) * 1.5f);
			// 批量单位没有模型节点：包围盒必须用单位的实际世界坐标，
			// 否则武器特效会把 (0, h/2, 0) 当世界坐标，全部指向地图中心。
			Vector3 pos = _batchVisualValid
				? _batchVisualPos
				: new Vector3(_rootNode.GlobalPosition.X, 0f, _rootNode.GlobalPosition.Z);
			float yMin = _modelInfo.OffsetY + _modelInfo.YMin * _modelInfo.Scale;
			float yMax = _modelInfo.OffsetY + _modelInfo.YMax * _modelInfo.Scale;
			center = new Vector3(pos.X, (yMin + yMax) * 0.5f, pos.Z);
			half = new Vector3(w * 0.5f, h * 0.5f, w * 0.5f);
			return true;
		}
		if (_modelRoot == null || _modelMeshes.Count == 0)
			return false;

		Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
		Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
		bool any = false;

		foreach (var mesh in _modelMeshes)
		{
			Aabb aabb = mesh.GetAabb();
			Transform3D xf = mesh.GlobalTransform;
			Vector3 o = aabb.Position;
			Vector3 s = aabb.Size;

			Vector3[] corners =
			{
				o,
				o + new Vector3(s.X, 0f, 0f),
				o + new Vector3(0f, s.Y, 0f),
				o + new Vector3(0f, 0f, s.Z),
				o + new Vector3(s.X, s.Y, 0f),
				o + new Vector3(s.X, 0f, s.Z),
				o + new Vector3(0f, s.Y, s.Z),
				o + s
			};

			foreach (var corner in corners)
			{
				Vector3 w = xf * corner;
				min = min.Min(w);
				max = max.Max(w);
				any = true;
			}
		}

		if (!any)
			return false;

		center = (min + max) * 0.5f;
		half = (max - min) * 0.5f;
		return true;
	}

	// 模型自身（未叠加实体世界坐标）的包围盒：用于自动居中
	private bool TryGetModelLocalBounds(out Vector3 center, out Vector3 half)
	{
		center = Vector3.Zero;
		half = Vector3.Zero;
		if (_modelRoot == null)
			return false;

		Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
		Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
		bool any = false;
		AccumulateLocalBounds(_modelRoot, Transform3D.Identity, ref min, ref max, ref any);

		if (!any)
			return false;

		center = (min + max) * 0.5f;
		half = (max - min) * 0.5f;
		return true;
	}

	private static void AccumulateLocalBounds(
		Node node,
		Transform3D parentXf,
		ref Vector3 min,
		ref Vector3 max,
		ref bool any)
	{
		Transform3D localXf = node is Node3D n3 ? n3.Transform : Transform3D.Identity;
		Transform3D xf = parentXf * localXf;

		if (node is MeshInstance3D mesh)
		{
			Aabb aabb = mesh.GetAabb();
			Vector3 o = aabb.Position;
			Vector3 s = aabb.Size;

			Vector3[] corners =
			{
				o,
				o + new Vector3(s.X, 0f, 0f),
				o + new Vector3(0f, s.Y, 0f),
				o + new Vector3(0f, 0f, s.Z),
				o + new Vector3(s.X, s.Y, 0f),
				o + new Vector3(s.X, 0f, s.Z),
				o + new Vector3(0f, s.Y, s.Z),
				o + s
			};

			foreach (var corner in corners)
			{
				Vector3 w = xf * corner;
				min = min.Min(w);
				max = max.Max(w);
				any = true;
			}
		}

		foreach (Node child in node.GetChildren())
			AccumulateLocalBounds(child, xf, ref min, ref max, ref any);
	}

	public override void _Process(double delta)
	{
		// 解放者射击圈：全员可见虚线圆（圈心 = 架设目标点）
		UpdateDeployCircle();
		// 远程单位选中射程圈（射程 > 10 格，虚线圆）
		UpdateRangeCircle();

		// 批渲染单位：每帧只更新一个 MultiMesh 实例变换，不碰任何模型节点
		if (_batchSlot >= 0)
		{
			UpdateBatch();
			return;
		}

		UpdateAim((float)delta);

		if (_isSelected)
			RefreshRingDeployColor();

		// 迷雾隐藏或屏幕外的单位暂停动画，省掉逐帧骨骼计算
		bool shouldAnimate = !_fogHidden && IsOnScreen();
		if (!shouldAnimate)
		{
			if (_animPlayer != null && _animPlayer.IsPlaying())
				_animPlayer.Pause();
			return;
		}

		// 只有移动中的单位才推进骨骼动画；停下交战时冻结姿态，省掉大部分空闲动画开销
		bool moving = IsMoving();
		if (_animPlayer != null)
		{
			if (!_animPlayer.IsPlaying() && _currentAnim.Length > 0)
				_animPlayer.Play(_currentAnim);

			// 手动节流：累计到 1/15 秒才推进一次骨骼动画（未播放的模型不推进）
			if (moving && _animPlayer.IsPlaying())
			{
				_animAccumulator += (float)delta;
				if (_animAccumulator >= 1f / 15f)
				{
					_animPlayer.Advance(_animAccumulator);
					_animAccumulator -= 1f / 15f;
				}
			}
		}

		UpdateModelAnimation((float)delta);
	}

	private bool IsMoving()
	{
		if (_entity?.LogicEntity is RTS.Simulation.SimUnit su)
		{
			if (RTS.Core.SimManager.Instance != null &&
				RTS.Core.SimManager.Instance.TryGetUnitVisualState(su.ID, out _, out _, out float vx, out float vy))
			{
				var snapV2 = (FixMath.NET.Fix64)vx * (FixMath.NET.Fix64)vx + (FixMath.NET.Fix64)vy * (FixMath.NET.Fix64)vy;
				bool rawMove = _movingVisualState
					? snapV2 > (FixMath.NET.Fix64)4m
					: snapV2 > (FixMath.NET.Fix64)64m;
				if (rawMove != _movingVisualState)
				{
					if (++_movingDebounceFrames >= 3)
					{
						_movingVisualState = rawMove;
						_movingDebounceFrames = 0;
					}
				}
				else
				{
					_movingDebounceFrames = 0;
				}
				return _movingVisualState;
			}

			// 迟滞 + 连续帧确认：推挤/碰撞时速度在阈值附近抖动会导致 Idle/Run 反复闪烁。
			// 已移动时速度降到 <2 才算停；已静止时速度超过 8 才算动。
			var v2 = su.Velocity.MagnitudeSquared();
			bool raw = _movingVisualState
				? v2 > (FixMath.NET.Fix64)4m
				: v2 > (FixMath.NET.Fix64)64m;
			if (raw != _movingVisualState)
			{
				if (++_movingDebounceFrames >= 3)
				{
					_movingVisualState = raw;
					_movingDebounceFrames = 0;
				}
			}
			else
			{
				_movingDebounceFrames = 0;
			}
			return _movingVisualState;
		}
		return false;
	}

	private bool IsOnScreen()
	{
		if (_modelRoot == null)
			return true;

		var cam = GetViewport()?.GetCamera3D();
		if (cam == null)
			return true;

		return cam.IsPositionInFrustum(_rootNode.GlobalPosition + new Vector3(0f, 30f, 0f));
	}

	public void UpdateTeamColor()
	{
		ApplyTint();
	}

	// 蓝图：蓝色半透明；施工完成：恢复正常
	public void SetBlueprintVisual(bool isBlueprint)
	{
		_isBlueprint = isBlueprint;
		ApplyTint();
	}

	// 施工中：橙黄半透明（与蓝图/完成态区分）
	public void SetConstructionVisual(bool isConstruction)
	{
		_isConstruction = isConstruction;
		ApplyTint();
	}

	// 战争迷雾：不可见建筑显示为灰色（已探索记忆）
	public void SetFogHidden(bool hidden)
	{
		_fogHidden = hidden;
		// 脱视野：模型保留灰色记忆显示，但血条隐藏
		if (_healthBar3D != null)
			_healthBar3D.SetFogHidden(hidden);
		ApplyTint();
	}

	public bool IsFogHidden => _fogHidden;
	public bool IsBatched => _batchSlot >= 0;

	/// <summary>
	/// 建筑被摧毁时的记忆幽灵：复制当前模型并整体灰显（脱视野残留用）。
	///
	/// 原来这里是不透明的实心灰（`Unshaded` + 不透明白），
	/// 结果"记忆中的建筑"看起来像一坨实心灰块，比看不见还糟 ——
	/// 玩家分不清那是可交互的实体还是残留影像。
	/// 改成**半透明 + 偏向背景灰蓝**，让它读起来就是"影像"。
	/// </summary>
	public Node3D CreateGhostCopy()
	{
		if (_modelRoot == null)
			return null;

		var copy = (Node3D)_modelRoot.Duplicate();
		var ghost = new StandardMaterial3D
		{
			// 偏蓝的灰：比中性灰更像"雾里的轮廓"，也不会和真实建筑抢注意力
			AlbedoColor = new Color(0.62f, 0.68f, 0.78f, 0.42f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			// 幽灵不该被背面剔除影响外观（模型法线方向不一）
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			// 多块面片叠加时不写深度，避免自身互相遮挡出现硬边
			NoDepthTest = false,
		};

		foreach (Node child in copy.FindChildren("*", "MeshInstance3D", true, false))
		{
			if (child is MeshInstance3D mi)
			{
				mi.MaterialOverride = ghost;
				// 透明物体要参与排序，否则会被地形/单位遮挡错乱
				mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
			}
		}

		return copy;
	}

	private void ApplyTint()
	{
		if (_batchSlot >= 0)
		{
			_batchColor = GetBatchColor();
			UpdateBatch();
			return;
		}

		// GLB 模型分支
		if (_modelRoot != null)
		{
			if (_isBlueprint)
			{
				ApplyModelTint(new Color(0.3f, 0.7f, 1.0f, 0.6f), true);
				return;
			}

			if (_isConstruction)
			{
				ApplyModelTint(new Color(1.0f, 0.75f, 0.2f, 0.75f), true);
				return;
			}

			if (_fogHidden)
			{
				ApplyModelTint(new Color(0.5f, 0.5f, 0.5f), false);
				return;
			}

			// 本地玩家号未就绪时**不能**当成 0 来判定敌我：
			// TeamID 从 1 开始，`myLocalId=0` 会把所有单位都判成敌人（包括自己人），
			// 表现就是"开局一瞬间自己的兵全被染红"。
			// 这里与 FogOfWar 统一用 -1 作哨兵，未就绪时干脆不染色（保持原色），
			// 等 Main 就绪后的下一次刷新再正确判定。
			int myLocalId = Main.Instance != null ? Main.Instance.LocalPlayerID : -1;
			if (myLocalId < 0)
			{
				ClearModelTint();
				return;
			}

			bool friendly = _entity.TeamID == myLocalId || _entity.TeamID == -1;
			if (friendly)
			{
				ClearModelTint();
				return;
			}

			ApplyModelTint(new Color(1.0f, 0.45f, 0.45f), false);
			return;
		}

		// 贴图立方体分支
		if (_material == null || _entity == null)
			return;

		if (_isBlueprint)
		{
			_material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
			_material.AlbedoColor = new Color(0.3f, 0.7f, 1.0f, 0.6f);
			return;
		}

		if (_isConstruction)
		{
			_material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
			_material.AlbedoColor = new Color(1.0f, 0.75f, 0.2f, 0.75f);
			return;
		}

		_material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;

		if (_fogHidden)
		{
			_material.AlbedoColor = new Color(0.5f, 0.5f, 0.5f);
			return;
		}

		int myLocalId2 = Main.Instance != null ? Main.Instance.LocalPlayerID : 0;

		if (_entity.TeamID == myLocalId2 || _entity.TeamID == -1)
			_material.AlbedoColor = Colors.White;
		else
			_material.AlbedoColor = new Color(1.0f, 0.45f, 0.45f);
	}

	public void SetSelected(bool isSelected)
	{
		_isSelected = isSelected;

		if (_batchMode && isSelected && _selectionRing3D == null && _modelInfo != null)
		{
			_selectionRing3D = new SelectionRing3D { Name = "SelectionRing3D" };
			float ringRadius = Mathf.Max(40f, (_modelInfo.YMax - _modelInfo.YMin) * _modelInfo.Scale * 0.55f);
			_selectionRing3D.Setup(ringRadius);
			_rootNode.AddChild(_selectionRing3D);
		}

		_selectionRing3D?.SetSelected(isSelected);

		if (isSelected)
			RefreshRingDeployColor();
		UpdateRangeCircle();
	}

	private void RefreshRingDeployColor()
	{
		if (_selectionRing3D == null)
			return;

		bool deployed = _entity?.LogicEntity is RTS.Simulation.SimUnit su && su.IsDeployed;
		if (deployed != _ringDeployed)
		{
			_ringDeployed = deployed;
			_selectionRing3D.SetDeployed(deployed);
		}
	}

	public void AimAt(Vector2 targetPos)
	{
		// 瞄准属于视觉层：模拟线程可能调用，统一延迟到主线程；
		// 目标未变化时跳过，避免每帧为每个单位分配闭包（GC 尖峰来源）
		if ((targetPos - _lastAimRequest).LengthSquared() < 1f)
			return;
		_lastAimRequest = targetPos;
		RTS.Core.SimEventQueue.EnqueueMain(() => AimAtMain(targetPos));
	}

	// 解放者射击圈：圈心 = 架设目标点，半径 4 格虚线圆，全员可见
	private void UpdateDeployCircle()
	{
		var su = _entity?.LogicEntity as RTS.Simulation.SimUnit;
		bool show = su != null && su.DeployRequiresTargetCircle && su.IsDeployed &&
			su.DeployTargetRadius > FixMath.NET.Fix64.Zero;

		if (show)
		{
			if (_deployCircle == null || !GodotObject.IsInstanceValid(_deployCircle))
			{
				_deployCircle = new DeployCircleVisual { Name = "DeployCircle" };
				GetTree().Root.AddChild(_deployCircle);
			}
			_deployCircle.UpdateCircle(
				new Vector3((float)su.DeployTargetCenter.X, 0f, (float)su.DeployTargetCenter.Y),
				(float)su.DeployTargetRadius,
				new Color(1f, 0.6f, 0.25f, 0.9f));
		}
		else if (_deployCircle != null && GodotObject.IsInstanceValid(_deployCircle))
		{
			_deployCircle.QueueFree();
			_deployCircle = null;
		}
	}

	// 远程单位（最大武器射程 > 10 格）选中时显示蓝色虚线射程圈，随单位移动
	private void UpdateRangeCircle()
	{
		var selUnit = _entity as Unit;
		bool show = _isSelected && selUnit != null && selUnit.CombatModule != null &&
			selUnit.CombatModule.GetAttackRange() > 640f && !selUnit.IsDeadOrNull();

		if (show)
		{
			if (_rangeCircle == null || !GodotObject.IsInstanceValid(_rangeCircle))
			{
				_rangeCircle = new DeployCircleVisual { Name = "RangeCircle" };
				GetTree().Root.AddChild(_rangeCircle);
			}

			float range = selUnit.CombatModule.GetAttackRange();
			var su = _entity?.LogicEntity as RTS.Simulation.SimUnit;
			Vector3 center = su != null
				? new Vector3((float)su.Position.X, 0f, (float)su.Position.Y)
				: _rootNode.GlobalPosition;
			_rangeCircle.UpdateCircle(center, range, new Color(0.35f, 0.75f, 1f, 0.9f));
		}
		else if (_rangeCircle != null && GodotObject.IsInstanceValid(_rangeCircle))
		{
			_rangeCircle.QueueFree();
			_rangeCircle = null;
		}
	}

	private void AimAtMain(Vector2 targetPos)
	{
		if (_aimNode == null)
			return;

		Vector3 target = new Vector3(targetPos.X, 0f, targetPos.Y);
		Vector3 dir = target - _aimNode.GlobalPosition;
		dir.Y = 0f;
		if (dir.LengthSquared() < 1f)
			return;

		if (!_aimInitialized)
		{
			_aimCurrentYaw = _aimNode.Rotation.Y;
			_aimInitialized = true;
		}

		_aimTargetYaw = Mathf.Atan2(dir.X, dir.Z);
		if (_modelInfo != null)
			_aimTargetYaw += Mathf.DegToRad(_modelInfo.FacingBias);
		_hasAimTarget = true;
	}

	public void ResetAim()
	{
		// Idle: smoothly return to the body facing in UpdateAim.
		// bool 字段写入原子，可直接跨线程赋值（真正的视觉操作在 UpdateAim 主线程里做）
		_hasAimTarget = false;
	}

	private void ResolveAimNode()
	{
		// 优先旋转真正可见的炮塔/炮管（例如自行火炮的 Turret/Barrel 挂在 WeaponMount 下）；
		// 否则炮塔型预制体的可见炮身就是整个 Model 节点。
		if (_weaponMount != null && HasVisibleMesh(_weaponMount))
			_aimNode = _weaponMount;
		else if (_modelRoot != null && _modelInfo != null && _modelInfo.RotateToAim)
			_aimNode = _modelRoot;
		else
			_aimNode = _weaponMount;
		if (_aimNode == _modelRoot && _modelRoot != null)
			_bodyRestYaw = _modelRoot.Rotation.Y;
		ResolveBarrelNode();
	}

	private void ResolveBarrelNode()
	{
		_barrelNode = null;
		if (_aimNode != null && _modelInfo != null && !string.IsNullOrEmpty(_modelInfo.BarrelPath))
			_barrelNode = _aimNode.GetNodeOrNull<Node3D>(_modelInfo.BarrelPath);
		if (_barrelNode != null)
			_barrelRestBasis = _barrelNode.Basis;

		_muzzleNode = null;
		if (_modelRoot != null && _modelInfo != null && !string.IsNullOrEmpty(_modelInfo.BottomMuzzlePath))
			_muzzleNode = _modelRoot.GetNodeOrNull<Node3D>(_modelInfo.BottomMuzzlePath);
	}

	private static bool HasVisibleMesh(Node3D node)
	{
		if (node is MeshInstance3D)
			return true;
		foreach (Node child in node.GetChildren())
		{
			if (child is Node3D child3D && HasVisibleMesh(child3D))
				return true;
		}
		return false;
	}

	private void UpdateAim(float dt)
	{
		UpdateDeployAim(dt);

		if (_aimNode == null)
			return;

		if (!_aimInitialized)
		{
			_aimCurrentYaw = _aimNode.Rotation.Y;
			_aimInitialized = true;
		}

		float targetYaw = _hasAimTarget ? _aimTargetYaw : GetBodyYaw();
		_aimCurrentYaw = Mathf.LerpAngle(
			_aimCurrentYaw,
			targetYaw,
			1f - Mathf.Exp(-_aimSmoothSpeed * dt));
		_aimNode.Rotation = new Vector3(0f, _aimCurrentYaw, 0f);

		UpdateBarrelPitch(dt);
	}

	private float GetBodyYaw()
	{
		// 炮塔和身体是同一个节点（炮塔型建筑）时，身体朝向就是模型默认朝向；
		// 否则身体是 Model（车体/船体），闲置时炮塔回归它的当前朝向。
		if (_aimNode == _modelRoot || _modelRoot == null)
			return _bodyRestYaw;
		return _modelRoot.Rotation.Y;
	}

	private void UpdateBarrelPitch(float dt)
	{
		if (_barrelNode == null || _modelInfo == null)
			return;

		bool deployed = _entity?.LogicEntity is RTS.Simulation.SimUnit su && su.IsDeployed;
		float targetDeg = deployed
			? _modelInfo.BarrelDeployPitchDegrees
			: _modelInfo.BarrelNormalPitchDegrees;
		_barrelTargetPitch = Mathf.DegToRad(targetDeg);
		_barrelCurrentPitch = Mathf.LerpAngle(
			_barrelCurrentPitch,
			_barrelTargetPitch,
			1f - Mathf.Exp(-_aimSmoothSpeed * dt));
		_barrelNode.Basis = _barrelRestBasis * new Basis(Vector3.Right, _barrelCurrentPitch);
	}
	// 天基炮类：架设时整个舰体旋转，让底部炮口对准目标；发射点就是 Muzzle（模型最下端）。
	// 架设/收起的转向带角加速-减速（smoothstep），在架设时间内完成，显得笨重。
	private void UpdateDeployAim(float dt)
	{
		if (_modelRoot == null || _modelInfo == null || !_modelInfo.DeployAimWithBottom || _muzzleNode == null)
			return;

		if (_entity?.LogicEntity is not RTS.Simulation.SimUnit su)
			return;

		int state = su.DeployState;

		// 状态切换：启动一次笨重转向（架设→指向目标，收起→回正）
		if (state != _lastDeployState)
		{
			_lastDeployState = state;
			_deployTurnActive = true;
			_deployTurnTime = 0f;
			_deployTurnStart = _modelRoot.Basis.GetRotationQuaternion();
			_deployTurnDuration = GetDeployTurnDuration(su);
			_deployTurnEnd = (state == 1 || state == 2)
				? ComputeDeployAimQuat(su)
				: Quaternion.Identity;
		}

		if (_deployTurnActive)
		{
			_deployTurnTime += dt;
			float t = Mathf.Clamp(_deployTurnTime / Mathf.Max(0.01f, _deployTurnDuration), 0f, 1f);
			float s = t * t * (3f - 2f * t); // smoothstep：先加速后减速
			Quaternion current = _deployTurnStart.Slerp(_deployTurnEnd, s);
			_modelRoot.Basis = new Basis(current).Scaled(_modelRoot.Scale);
			if (t >= 1f)
			{
				_deployTurnActive = false;
				_deployAimInit = false;
			}
			return;
		}

		if (su.IsDeployed)
		{
			Quaternion target = ComputeDeployAimQuat(su);
			if (!_deployAimInit)
			{
				_deployAimCurrent = _modelRoot.Basis.GetRotationQuaternion();
				_deployAimInit = true;
			}
			_deployAimCurrent = _deployAimCurrent.Slerp(target, 1f - Mathf.Exp(-_aimSmoothSpeed * dt));
			_modelRoot.Basis = new Basis(_deployAimCurrent).Scaled(_modelRoot.Scale);
			return;
		}

		// 未架设且不在转向中：姿态交给移动/默认逻辑
		_deployAimInit = false;
	}

	private Quaternion ComputeDeployAimQuat(RTS.Simulation.SimUnit su)
	{
		// 目标：当前攻击目标优先，否则对准架设圈中心
		Vector3 target;
		var combat = _entity?.CombatModule;
		if (combat?.CurrentTarget != null && !combat.CurrentTarget.IsDeadOrNull() && combat.CurrentTarget.LogicEntity != null)
		{
			target = new Vector3(
				(float)combat.CurrentTarget.LogicEntity.Position.X,
				0f,
				(float)combat.CurrentTarget.LogicEntity.Position.Y);
		}
		else if (su.DeployTargetRadius > FixMath.NET.Fix64.Zero)
		{
			target = new Vector3((float)su.DeployTargetCenter.X, 0f, (float)su.DeployTargetCenter.Y);
		}
		else
			return Quaternion.Identity;

		Vector3 muzzle = _muzzleNode.GlobalPosition;
		Vector3 dir = target - muzzle;
		if (dir.LengthSquared() < 1f)
			return Quaternion.Identity;
		dir = dir.Normalized();

		// 让模型本地 -Y（底部）对准目标方向
		Vector3 down = new Vector3(0f, -1f, 0f);
		float dot = Mathf.Clamp(down.Dot(dir), -1f, 1f);
		Vector3 axis = down.Cross(dir);
		if (axis.LengthSquared() < 1e-6f)
			return dot < 0f ? new Quaternion(Vector3.Right, Mathf.Pi) : Quaternion.Identity;
		return new Quaternion(axis.Normalized(), Mathf.Acos(dot));
	}

	private float GetDeployTurnDuration(RTS.Simulation.SimUnit su)
	{
		float seconds = RTS.Data.Configs.ConfigDatabase.GetUnit(_entity?.DisplayName ?? "")?.DeployTimeSeconds ?? 1f;
		return Mathf.Max(0.1f, seconds * (float)su.DeployTimeMultiplier);
	}

	public void OnUnitDied()
	{
		if (_batchSlot >= 0)
		{
			_batchDead = true;
			ApplyTint();
			return;
		}

		_animPlayer?.Pause();

		if (_material != null)
			_material.AlbedoColor = new Color(0.45f, 0.45f, 0.45f);

		if (_modelRoot != null)
			ApplyModelTint(new Color(0.45f, 0.45f, 0.45f), false);
	}

	public override void _ExitTree()
	{
		if (_barBatchSlot >= 0)
		{
			RTS.Core.HealthBarBatchRenderer.Unregister(_barBatchSlot);
			_barBatchSlot = -1;
		}

		if (_batchSlot >= 0)
		{
			RTS.Core.UnitVatRenderer.Unregister(_batchModelPath, _batchSlot);
			RTS.Core.UnitVatRenderer.UnregisterManagedVisual(this);
			_batchSlot = -1;
		}
	}
}
