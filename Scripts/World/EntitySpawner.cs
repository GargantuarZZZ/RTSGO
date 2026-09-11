using Godot;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
	public partial class EntitySpawner : Node
	{
		public static EntitySpawner Instance { get; private set; }

		// 有正式场景的实体优先加载场景（目前：矿物），其余走代码工厂
		[Export] public string[] ScanDirs =
		{
			"res://Scenes/Races"
		};

		private Dictionary<string, string> _paths = new();
		private readonly List<KeyValuePair<string, string>> _foundPaths = new();

		public override void _Ready()
		{
			Instance = this;
			foreach (var dir in ScanDirs)
				Scan(dir);

			// 文件系统枚举顺序不保证跨端一致，先按名称排序再建字典
			foreach (var kv in _foundPaths.OrderBy(k => k.Key, System.StringComparer.Ordinal))
			{
				if (!_paths.ContainsKey(kv.Key))
					_paths[kv.Key] = kv.Value;
			}

		}

		private void Scan(string path)
		{
			using var dir = DirAccess.Open(path);
			if (dir == null)
			{
				GD.PrintErr($"[Spawner] 无法打开文件夹: {path}");
				return;
			}

			dir.ListDirBegin();
			string file = dir.GetNext();
			while (file != "")
			{
				if (dir.CurrentIsDir())
				{
					if (!file.StartsWith("."))
						Scan(path + "/" + file);
				}
				else if (file.EndsWith(".tscn") || file.EndsWith(".tscn.remap"))
				{
					string cleanName = file.Replace(".remap", "");
					string key = cleanName.GetBaseName();
					_foundPaths.Add(new KeyValuePair<string, string>(key, path + "/" + cleanName));
				}
				file = dir.GetNext();
			}
		}

		public IEntity SpawnEntity(string name, int teamId, FPVector2 exactPos)
		{
			// 模拟线程 tick 期间持有 WorldLock；主线程直接生成时也持锁，避免并发修改 World
			var worldLock = RTS.Core.SimManager.Instance?.WorldLock;
			if (worldLock != null)
			{
				lock (worldLock)
					return SpawnEntityInner(name, teamId, exactPos);
			}
			return SpawnEntityInner(name, teamId, exactPos);
		}

		private IEntity SpawnEntityInner(string name, int teamId, FPVector2 exactPos)
		{
			Node3D root = null;
			IEntity entity = null;

			// 优先正式场景文件（可在编辑器里编辑/保存）
			if (_paths.TryGetValue(name, out var scenePath))
			{
				try
				{
					var scene = GD.Load<PackedScene>(scenePath);

					if (scene != null)
					{
						Node instance = scene.Instantiate();

						if (instance is Node3D sceneRoot && sceneRoot is IEntity sceneEntity)
						{
							root = sceneRoot;
							entity = sceneEntity;
							entity.TeamID = teamId;

							if (root is RTS.Units.Unit unit)
								unit.InjectLogicPosition(exactPos);
							else
								root.Position = new Vector3((float)exactPos.X, 0f, (float)exactPos.Y);
						}
						else
						{
							instance?.QueueFree();
						}
					}
				}
				catch (System.Exception ex)
				{
					// 坏预制体（空内嵌脚本/缺类）不能炸游戏：回退代码工厂
					GD.PrintErr($"[Spawner] 预制体实例化失败 {name}: {ex.Message}，回退代码工厂");
				}
			}

			// 无场景时走代码工厂（贴图立方体）
			if (root == null && EntityFactory3D.TryCreate(name, teamId, exactPos, out entity, out root))
			{
				// 工厂已完成 TeamID / 位置注入
			}

			if (root == null || entity == null)
			{
				GD.PrintErr($"[Spawner] 失败：无法生成 '{name}'。");
				return null;
			}

			root.AddToGroup("entities");

			Node parent = this;
			if (Main.Instance != null)
			{
				var entitiesNode = Main.Instance.GetNodeOrNull("Entities");
				if (entitiesNode != null)
					parent = entitiesNode;
			}

			parent.AddChild(root);

			// 已研究的科技立即作用到新实体（先注入基础数值，再应用加成）
			if (entity is RTS.Units.Unit spawnedUnit)
				spawnedUnit.LifeModule?.ForceInjectStats();
			else if (entity is RTS.Units.Structure spawnedStructure)
				spawnedStructure.LifeModule?.ForceInjectStats();

			var owner = RTS.World.Game.GetPlayerByTeam(teamId);
			if (owner?.PlayerData != null)
				TechEffects.ApplyToEntity(entity, owner.PlayerData);

			// 沙虫头：生成火车式身体（3 中段 + 尾），建立节段链
			if (name == "CaveSandworm" && entity is RTS.Units.Unit headUnit)
				SpawnSandwormSegments(headUnit, teamId);

			return entity;
		}

		// 沙虫节段链：头 → 中段 ×3 → 尾，依次排开并记录 leader/group
		private void SpawnSandwormSegments(RTS.Units.Unit head, int teamId)
		{
			if (head?.LogicEntity is not SimUnit headSim || headSim.ID <= 0)
				return;

			// 头也属于本组（存活计数/吞噬回血需要）
			headSim.SegmentGroupId = headSim.ID;

			string[] order = { "CaveSandwormMid", "CaveSandwormMid", "CaveSandwormMid", "CaveSandwormTail" };
			FPVector2 pos = headSim.Position;
			int leaderId = headSim.ID;

			foreach (string segId in order)
			{
				pos = new FPVector2(pos.X - (FP)96m, pos.Y);
				var seg = SpawnEntityInner(segId, teamId, pos) as RTS.Units.Unit;
				if (seg?.LogicEntity is SimUnit segSim)
				{
					segSim.SegmentGroupId = headSim.ID;
					segSim.SegmentLeaderId = leaderId;
					segSim.IsSegmentBody = true;
					leaderId = segSim.ID;
				}
			}
		}
	}
}
