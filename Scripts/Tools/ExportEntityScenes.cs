using Godot;
using System.Collections.Generic;

namespace RTS.Core
{
	// 预制体导出器：把 EntityFactory3D 生成的实体打包成 .tscn，
	// 供编辑器编辑；游戏加载时优先使用场景文件。
	// 运行方式: godot --headless --path . res://Scenes/Tools/ExportEntityScenes.tscn
	public partial class ExportEntityScenes : Node3D
	{
		private readonly Dictionary<string, string> _targets = new()
		{
			// ================= 联盟 =================
			{ "SCV", "res://Scenes/Races/Union/Units" },
			{ "RifleMan", "res://Scenes/Races/Union/Units" },
			{ "RocketMan", "res://Scenes/Races/Union/Units" },
			{ "Medic", "res://Scenes/Races/Union/Units" },
			{ "Biped", "res://Scenes/Races/Union/Units" },
			{ "Hp", "res://Scenes/Races/Union/Units" },
			{ "BattleCruiser", "res://Scenes/Races/Union/Units" },
			{ "Artillery", "res://Scenes/Races/Terran/Units" },
			{ "CommandCenter", "res://Scenes/Races/Union/Structures" },
			{ "BB", "res://Scenes/Races/Union/Structures" },
			{ "VF", "res://Scenes/Races/Union/Structures" },
			{ "SupplyDepot", "res://Scenes/Races/Union/Structures" },
			{ "Academy", "res://Scenes/Races/Union/Structures" },
			{ "Starport", "res://Scenes/Races/Union/Structures" },
			{ "OrbitalControl", "res://Scenes/Races/Union/Structures" },

			// ================= 泰伦 =================
			{ "Engineer", "res://Scenes/Races/Terran/Units" },
			{ "Marine", "res://Scenes/Races/Terran/Units" },
			{ "HeavyInfantry", "res://Scenes/Races/Terran/Units" },
			{ "CommandVehicle", "res://Scenes/Races/Terran/Units" },
			{ "Liberator", "res://Scenes/Races/Terran/Units" },
			{ "Fighter", "res://Scenes/Races/Terran/Units" },
			{ "MissileVehicle", "res://Scenes/Races/Terran/Units" },
			{ "FortressCore", "res://Scenes/Races/Terran/Structures" },
			{ "FrontlineCamp", "res://Scenes/Races/Terran/Structures" },
			{ "FieldCamp", "res://Scenes/Races/Terran/Structures" },
			{ "ArmorFactory", "res://Scenes/Races/Terran/Structures" },
			{ "Airfield", "res://Scenes/Races/Terran/Structures" },
			{ "Armory", "res://Scenes/Races/Terran/Structures" },
			{ "AlgaeFactory", "res://Scenes/Races/Terran/Structures" },

			// ================= 恶魔 =================
			{ "DemonWorker", "res://Scenes/Races/Demon/Units" },
			{ "DemonDog", "res://Scenes/Races/Demon/Units" },
			{ "DemonFlyer", "res://Scenes/Races/Demon/Units" },
			{ "HeavyTank", "res://Scenes/Races/Demon/Units" },
			{ "FireDragon", "res://Scenes/Races/Demon/Units" },
			{ "LavaBanner", "res://Scenes/Races/Demon/Units" },
			{ "FortGuard", "res://Scenes/Races/Demon/Units" },
			{ "HellLord", "res://Scenes/Races/Demon/Units" },
			{ "FireTitan", "res://Scenes/Races/Demon/Units" },
			{ "HellCity", "res://Scenes/Races/Demon/Structures" },
			{ "GreatRift", "res://Scenes/Races/Demon/Structures" },
			{ "HeavyWorkshop", "res://Scenes/Races/Demon/Structures" },
			{ "DragonNest", "res://Scenes/Races/Demon/Structures" },
			{ "ManaTower", "res://Scenes/Races/Demon/Structures" },
			{ "SoulStone", "res://Scenes/Races/Demon/Structures" },
			{ "LavaAltar", "res://Scenes/Races/Demon/Structures" },
			{ "CurseFortress", "res://Scenes/Races/Demon/Structures" },
			{ "DemonTower", "res://Scenes/Races/Demon/Structures" },
			{ "Banner", "res://Scenes/Races/Demon/Structures" },

			// ================= 多足机械 =================
			{ "Builder", "res://Scenes/Races/Wanderer/Units" },
			{ "Hunter", "res://Scenes/Races/Wanderer/Units" },
			{ "Harvester", "res://Scenes/Races/Wanderer/Units" },
			{ "Shepherd", "res://Scenes/Races/Wanderer/Units" },
			{ "Tank", "res://Scenes/Races/Wanderer/Units" },
			{ "PlasmaCannon", "res://Scenes/Races/Wanderer/Units" },
			{ "Blueprint_Builder", "res://Scenes/Races/Wanderer/Structures" },
			{ "Blueprint_Hunter", "res://Scenes/Races/Wanderer/Structures" },
			{ "Blueprint_Harvester", "res://Scenes/Races/Wanderer/Structures" },
			{ "Blueprint_Shepherd", "res://Scenes/Races/Wanderer/Structures" },
			{ "Blueprint_Tank", "res://Scenes/Races/Wanderer/Structures" },
			{ "Blueprint_PlasmaCannon", "res://Scenes/Races/Wanderer/Structures" },

			// ================= 纳米虫 =================
			{ "NanoBehemoth", "res://Scenes/Races/Nano/Units" },
			{ "NanoHarvester", "res://Scenes/Races/Nano/Units" },
			{ "NanoTurret", "res://Scenes/Races/Nano/Units" },
			{ "NanoSniper", "res://Scenes/Races/Nano/Units" },
			{ "NanoAA", "res://Scenes/Races/Nano/Units" },
			{ "NanoActiveTower", "res://Scenes/Races/Nano/Units" },
			{ "NanoSmokeTower", "res://Scenes/Races/Nano/Units" },
			{ "NanoCore", "res://Scenes/Races/Nano/Structures" },

			// ================= 中立 / 蓝图 =================
			{ "Tower", "res://Scenes/Races/Neutral/Structures" },
			{ "Shrine", "res://Scenes/Races/Neutral/Structures" },

			// ================= 资源 =================
			{ "IronOre", "res://Scenes/Races/Neutral/Resources" },
			{ "GasSpring", "res://Scenes/Races/Neutral/Resources" },
			{ "MetalMine", "res://Scenes/Races/Neutral/Resources" },
			{ "WildFruit", "res://Scenes/Races/Neutral/Resources" }
		};

		public override void _Ready()
		{
			CallDeferred(nameof(DoExport));
		}

		private void DoExport()
		{
			try
			{
				RTS.Data.Configs.ConfigDatabase.LoadAll();

				// 只导出指定单位（逗号分隔，例如 EXPORT_ONLY=DemonDog,HellLord）；空 = 全部
				string only = OS.GetEnvironment("EXPORT_ONLY") ?? "";
				HashSet<string> onlySet = null;
				if (!string.IsNullOrEmpty(only))
				{
					onlySet = new HashSet<string>(
						only.Split(',', System.StringSplitOptions.RemoveEmptyEntries),
						System.StringComparer.Ordinal);
				}

				int ok = 0;

				foreach (var kv in _targets)
				{
					if (onlySet != null && !onlySet.Contains(kv.Key))
						continue;

					GD.Print($"[Export] 开始: {kv.Key}");

					if (!EntityFactory3D.TryCreate(kv.Key, 1, RTS.Simulation.FPVector2.Zero, out var entity, out var root))
					{
						GD.PrintErr($"[Export] 无法创建 {kv.Key}");
						continue;
					}

					root.Name = kv.Key;
					root.Position = Vector3.Zero;
					FixEmptyScripts(root);
					SetOwnerRecursive(root, root);

					var packed = new PackedScene();
					packed.Pack(root);

					DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(kv.Value));
					string path = kv.Value + "/" + kv.Key + ".tscn";
					Error err = ResourceSaver.Save(packed, path);
					GD.Print($"[Export] {kv.Key} -> {path} ({err})");

					if (err == Error.Ok)
						ok++;

					root.QueueFree();
				}

				GD.Print($"[Export] 完成: {ok}/{_targets.Count}");
			}
			catch (System.Exception e)
			{
				GD.PrintErr($"[Export] 异常: {e}");
			}

			GetTree().Quit();
		}

		// PackedScene.Pack 只保存 owner 为根节点的子树，必须递归设置 Owner
		private static void SetOwnerRecursive(Node node, Node owner)
		{
			foreach (Node child in node.GetChildren())
			{
				child.Owner = owner;
				SetOwnerRecursive(child, owner);
			}
		}

		private static void FixEmptyScripts(Node node)
		{
			var cs = node.GetScript().As<CSharpScript>();
			if (cs != null && string.IsNullOrEmpty(cs.ResourcePath))
			{
				string cls = cs.GetClass();
				if (cls == "AutoBuildBehavior" || cls == "WorkerBuildBehavior" ||
					cls == "AutoHarvestBehavior")
				{
					cs.ResourcePath = cls switch
					{
						"AutoBuildBehavior" => "res://Scripts/Units/Modules/Behaviors/AutoBuildBehavior.cs",
						"WorkerBuildBehavior" => "res://Scripts/Units/Modules/Behaviors/WorkerBuildBehavior.cs",
						_ => "res://Scripts/Units/Modules/Behaviors/AutoHarvestBehavior.cs"
					};
				}
			}

			foreach (Node child in node.GetChildren())
				FixEmptyScripts(child);
		}
	}
}
