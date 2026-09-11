using Godot;
using System.Collections.Generic;
using RTS.Data;
using RTS.Simulation;
using RTS.World;

// 3D 菌毯视觉：只显示“当前被点亮格子”上的菌毯（与 2D 逻辑一致），
// 用 MultiMesh 合批渲染扁方块；纳米菌毯按归属分成“我方/敌方”两种颜色。
public partial class CreepVisual3D : Node3D
{
	private ulong _lastRebuildMs;
	private const ulong RebuildIntervalMs = 100; // 菌毯变化慢，~10fps 重建即可

	private MultiMeshInstance3D _friendlyInstance;
	private MultiMeshInstance3D _enemyInstance;
	private MultiMeshInstance3D _plantInstance;
	private StandardMaterial3D _friendlyMaterial;
	private StandardMaterial3D _enemyMaterial;
	private StandardMaterial3D _plantMaterial;
	private MultiMesh _friendlyMultiMesh;
	private MultiMesh _enemyMultiMesh;
	private MultiMesh _plantMultiMesh;

	// 菌毯探索记忆：已走过区域保留最后一次看到的画面（含当时的归属）
	private readonly Dictionary<Vector2I, int> _memory = new();
	private readonly Dictionary<Vector2I, int> _plantMemory = new();

	public override void _Process(double delta)
	{
		ulong now = Time.GetTicksMsec();
		if (now - _lastRebuildMs < RebuildIntervalMs)
			return;
		_lastRebuildMs = now;

		var creepGrid = RTS.Core.SimManager.Instance?.World?.CreepGrid;
		var allData = creepGrid?.GetAllCellData();
		if (FogOfWar.Instance == null || allData == null || allData.Count == 0)
		{
			HideAll();
			return;
		}

		var visible = FogOfWar.Instance.CurrentVisibleCells;
		var friendlyCells = new List<Vector2I>();
		var enemyCells = new List<Vector2I>();
		var plantCells = new List<Vector2I>();
		int myTeam = RTS.Core.Main.Instance?.LocalPlayerID ?? 0;
		bool revealAll = FogOfWar.Instance.RevealAll;

		// 纳米虫：地毯即视野，全部显示（按归属分色）
		bool nanoView = RTS.World.Game.GetPlayerByTeam(RTS.Core.Main.Instance?.LocalPlayerID ?? 0)?.Race?.RaceName == "Nano";

		if (nanoView)
		{
			foreach (var kvp in allData)
			{
				if (kvp.Value.Type != RTS.Data.CreepType.NanoCreep)
					continue;

				var cell = new Vector2I(kvp.Key.X, kvp.Key.Y);
				if (RTS.Core.SimManager.Instance == null ||
					!RTS.Core.SimManager.Instance.AreTeamsHostile(myTeam, kvp.Value.OwnerTeam))
					friendlyCells.Add(cell);
				else
					enemyCells.Add(cell);
			}
		}
		else
		{
			if (revealAll)
			{
				// 全局视野（旁观者/压测）：不依赖可见格缓存，直接画全图菌毯
				foreach (var kvp in allData)
				{
					var cell = new Vector2I(kvp.Key.X, kvp.Key.Y);
					if (kvp.Value.Type == RTS.Data.CreepType.PlantCreep)
					{
						if (RTS.Core.SimManager.Instance == null ||
							!RTS.Core.SimManager.Instance.AreTeamsHostile(myTeam, kvp.Value.OwnerTeam))
							plantCells.Add(cell);
						continue;
					}
					if (kvp.Value.Type != RTS.Data.CreepType.NanoCreep)
						continue;
					if (RTS.Core.SimManager.Instance == null ||
						!RTS.Core.SimManager.Instance.AreTeamsHostile(myTeam, kvp.Value.OwnerTeam))
						friendlyCells.Add(cell);
					else
						enemyCells.Add(cell);
				}
			}
			else
			{
				// 1. 当前直接可见的格子：以实时菌毯状态更新记忆（记录归属）
				foreach (var cell in visible)
				{
					var key = new RTS.Simulation.SimVector2I(cell.X, cell.Y);
					if (allData.TryGetValue(key, out var data))
					{
						if (data.Type == RTS.Data.CreepType.PlantCreep)
						{
							_plantMemory[cell] = data.OwnerTeam;
							// 同格类型切换时清掉另一侧记忆，避免“植物毯上叠纳米方块”
							_memory.Remove(cell);
						}
						else
						{
							_memory[cell] = data.OwnerTeam;
							_plantMemory.Remove(cell);
						}
					}
					else
					{
						_memory.Remove(cell);
						_plantMemory.Remove(cell);
					}
				}

				// 2. 渲染记忆中的菌毯（不可见区域保留旧画面）；
				//    盟友（2v2 同组）菌毯按友方颜色显示
				foreach (var kvp in _memory)
				{
					if (RTS.Core.SimManager.Instance == null ||
						!RTS.Core.SimManager.Instance.AreTeamsHostile(myTeam, kvp.Value))
						friendlyCells.Add(kvp.Key);
					else
						enemyCells.Add(kvp.Key);
				}

				// 3. 植物菌毯记忆（不可见区域保留旧画面）
				foreach (var kvp in _plantMemory)
				{
					if (RTS.Core.SimManager.Instance == null ||
						!RTS.Core.SimManager.Instance.AreTeamsHostile(myTeam, kvp.Value))
						plantCells.Add(kvp.Key);
				}
			}
		}

		// 注意：染色是**乘**在贴图上的，所以任何通道 >1.0 都会让该通道直接削顶，
		// 丢掉的正是原贴图的明暗层次（表现是菌毯"糊成一片亮色"）。
		// 之前的 1.05/1.35/1.45 超 1 了，这里压回 ≤1：靠**压另外两个通道**
		// 来得到同样的色调偏移，而不是靠抬高超 1 的通道。
		UpdateBatch(ref _friendlyInstance, ref _friendlyMultiMesh, ref _friendlyMaterial, friendlyCells, new Color(0.68f, 0.92f, 1.0f), "res://ArtRes/imgs3d/NanoCreep.png");
		UpdateBatch(ref _enemyInstance, ref _enemyMultiMesh, ref _enemyMaterial, enemyCells, new Color(1.0f, 0.52f, 0.45f), "res://ArtRes/imgs3d/NanoCreep.png");
		// 植物菌毯使用图集右上角裁出的真实贴图（保留原纹理，不再纯色染色）
		UpdateBatch(ref _plantInstance, ref _plantMultiMesh, ref _plantMaterial, plantCells, Colors.White, "res://ArtRes/imgs3d/PlantCreep.png");
	}

	private void HideAll()
	{
		if (_friendlyInstance != null)
			_friendlyInstance.Visible = false;
		if (_enemyInstance != null)
			_enemyInstance.Visible = false;
		if (_plantInstance != null)
			_plantInstance.Visible = false;
	}

	private void UpdateBatch(
		ref MultiMeshInstance3D instance,
		ref MultiMesh multiMesh,
		ref StandardMaterial3D material,
		List<Vector2I> cells,
		Color tint,
		string texturePath)
	{
		if (cells.Count == 0)
		{
			if (instance != null)
				instance.Visible = false;
			return;
		}

		if (material == null)
		{
			// 恢复成项目原本的实现（只用 <Name>.png，不接 PBR）。
			// 我一度接过 PBR 三件套 + 自造的 512px 贴图，已全部回调。
			material = new StandardMaterial3D
			{
				AlbedoTexture = RTS.Core.TextureLoader3D.LoadPng(texturePath),
				AlbedoColor = tint,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				Uv1Scale = Vector3.One,
				RenderPriority = 10
			};
		}

		if (multiMesh == null)
		{
			multiMesh = new MultiMesh
			{
				TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
				Mesh = RTS.Core.CubeMeshBuilder.Build(new Vector3(64f, 8f, 64f))
			};
		}

		multiMesh.InstanceCount = cells.Count;

		for (int i = 0; i < cells.Count; i++)
		{
			var cell = cells[i];
			multiMesh.SetInstanceTransform(
				i,
				new Transform3D(Basis.Identity, new Vector3(cell.X * 64 + 32, 4f, cell.Y * 64 + 32))
			);
		}

		if (instance == null)
		{
			instance = new MultiMeshInstance3D();
			AddChild(instance);
		}

		instance.Multimesh = multiMesh;
		instance.MaterialOverride = material;
		instance.Visible = true;
	}
}
