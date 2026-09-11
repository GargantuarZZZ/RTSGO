using Godot;
using RTS.Data;
using RTS.Data.Maps;
using RTS.MapEditing;
using RTS.World;

namespace RTS.Tools
{
	// =========================================================
	// 地图导出/批量生成工具（编辑器与 CI 共用）
	//
	// 用途：
	//   1. 把程序化生成的地图写成 res://Maps/*.tres，作为作者二次编辑的起点；
	//   2. 把场景内置地图（main / main_1v1 / main_debug 的 TileMapLayer）
	//      导出成 .tres，从此不必再改场景；
	//   3. 导出后立刻跑一遍校验，避免把"打不开的图"提交进仓库。
	//
	// 用法（无头）：
	//   godot --headless --path . res://Scenes/Tools/ExportMaps.tscn -- --export-maps [--gen-presets]
	//
	// 注意：res:// 在**导出后的游戏**里是只读的；本工具只在编辑器/CI 环境用。
	// =========================================================

	public partial class ExportMaps : Node
	{
		public override void _Ready()
		{
			if (!HasExportFlag())
			{
				GD.Print("[ExportMaps] 未指定 --export-maps，跳过。");
				GetTree().Quit(0);
				return;
			}

			int written = 0;
			int failed = 0;

			bool generatePresets = HasFlag("--gen-presets");
			if (generatePresets)
				GeneratePresetMaps(ref written, ref failed);

			ExportScenarioMaps(ref written, ref failed);

			GD.Print($"[ExportMaps] 完成：写出 {written} 张，失败 {failed} 张。");
			GetTree().Quit(failed == 0 ? 0 : 1);
		}

		private static bool HasExportFlag() => HasFlag("--export-maps");

		private static bool HasFlag(string flag)
		{
			foreach (string a in OS.GetCmdlineUserArgs())
				if (a == flag) return true;
			foreach (string a in OS.GetCmdlineArgs())
				if (a == flag) return true;
			return false;
		}

		// =========================================================
		// 1. 程序化生成预设
		// =========================================================

		/// <summary>
		/// 用生成器产出几张"开箱可用"的地图。
		/// 全部固定种子，保证任何人/任何机器跑出完全一样的结果（可复现）。
		/// </summary>
		private void GeneratePresetMaps(ref int written, ref int failed)
		{
			var presets = new (string Id, string Name, MapStyle Style, MapSymmetry Sym, int Size, int Spawns, int Seed)[]
			{
				("Gen_Open2",      "开阔战场 1v1", MapStyle.Open,        MapSymmetry.Rotational180, 120, 2, 11001),
				("Gen_Open4",      "开阔战场 2v2", MapStyle.Open,        MapSymmetry.QuadMirror,    140, 4, 11002),
				("Gen_Choke2",     "隘口 1v1",     MapStyle.Chokepoints, MapSymmetry.HorizontalMirror, 120, 2, 11003),
				("Gen_Rooms4",     "走廊 2v2",     MapStyle.Rooms,       MapSymmetry.QuadMirror,    120, 4, 11004),
				("Gen_Caves2",     "洞穴 1v1",     MapStyle.Caves,       MapSymmetry.Rotational180, 110, 2, 11005),
			};

			foreach (var p in presets)
			{
				var settings = new MapGenSettings
				{
					Width = p.Size,
					Height = p.Size,
					Seed = p.Seed,
					Style = p.Style,
					Symmetry = p.Sym,
					SpawnPointCount = p.Spawns,
					Richness = 0.6f,
					NeutralTowerCount = 4,
					ShrineCount = 2,
				};

				var map = MapGenerator.Generate(settings, out var report);
				map.MapId = p.Id;
				map.DisplayName = p.Name;
				map.Author = "generator";

				// 生成器不产出触发器：补一条"开局提示"作为作者可参考的样例，
				// 同时保证触发器链路在新地图上是活的（不是一段没人跑的死代码）。
				AddWelcomeTrigger(map, p.Name);

				if (ValidateAndSave(map, ref failed))
				{
					written++;
					GD.Print($"[ExportMaps] {p.Id}: {report.WallCells} 墙 / {report.ResourcesPlaced} 资源 / {p.Spawns} 出生点");
				}
			}
		}

		/// <summary>
		/// 给地图加一条示范触发器：第 2 秒广播一条消息。
		/// 作者可以照着改，也顺便验证触发器的数据能被正确序列化。
		/// </summary>
		private static void AddWelcomeTrigger(RtsMapData map, string mapName)
		{
			var trigger = new TriggerDefinition
			{
				TriggerId = "welcome",
				DisplayName = "开局提示",
				Comment = "样例触发器：第 2 秒（tick 40）广播一条消息。可删可改。",
				EnabledAtStart = true,
				Phase = TriggerPhase.Scheduled,
				StartTick = 40,
				Repeatable = false,
				EvalMode = TriggerEvalMode.Lenient,
			};

			trigger.Conditions.Add(new TriggerCondition("tick() >= 40"));
			trigger.Actions.Add(new TriggerAction
			{
				Kind = TriggerActionKind.ShowMessage,
				Id = $"{mapName} · 战斗开始！",
			});

			map.Triggers.Add(trigger);

			// 声明一个作者可用的变量，演示变量系统
			map.Variables.Add(new TriggerVariable { Name = "var_score", Value = 0f });
		}

		// =========================================================
		// 2. 场景内置地图 → .tres
		// =========================================================

		/// <summary>
		/// 导出场景里的 TileMapLayer 地形。
		/// 场景必须已经实例化；本工具通过加载场景资源来拿到 TileMapLayer。
		/// </summary>
		private void ExportScenarioMaps(ref int written, ref int failed)
		{
			var scenarios = new (string Path, string Id, string Name)[]
			{
				("res://Scenes/main.tscn", "Classic", "经典大地图"),
				("res://Scenes/main_1v1.tscn", "1v1", "1v1 断墙小图"),
				("res://Scenes/main_debug.tscn", "Debug", "调试地图"),
			};

			foreach (var s in scenarios)
			{
				if (!ResourceLoader.Exists(s.Path))
				{
					GD.Print($"[ExportMaps] 场景不存在，跳过：{s.Path}");
					continue;
				}

				var packed = GD.Load<PackedScene>(s.Path);
				if (packed == null)
				{
					GD.PrintErr($"[ExportMaps] 无法加载场景：{s.Path}");
					failed++;
					continue;
				}

				Node root = packed.Instantiate();
				var layer = FindTileMapLayer(root);
				if (layer == null)
				{
					GD.PrintErr($"[ExportMaps] 场景里没有 TileMapLayer：{s.Path}");
					root.QueueFree();
					failed++;
					continue;
				}

				var map = MapLoader.ExportFromTileMap(layer, s.Id);
				map.DisplayName = s.Name;
				map.Author = "scenario-import";

				// 出生点：从场景的 SpawnPoints/Spawn_N 标记导出。
				// 必须用与地形相同的平移量，否则出生点会落在导出后地图的坐标系之外。
				var spawnContainer = root.GetNodeOrNull("SpawnPoints");
				if (spawnContainer != null)
				{
					var spawnMap = MapRegistry.ExportSpawnPointsOnly(
						spawnContainer, s.Id, map.ExportOffsetX, map.ExportOffsetY);

					int kept = 0;
					int dropped = 0;
					foreach (var sp in spawnMap.SpawnPoints)
					{
						// 1v1 / Debug 的地形是**代码生成**的（MapGrid.BuildSmallMap1v1 等），
						// 场景里几乎没有瓦片 → 导出的地形范围和运行时不是一回事，
						// 出生点很容易落到导出地图之外。这种情况直接丢掉出生点，
						// 让作者在编辑器里重新摆，而不是让整张图导出失败。
						if (map.InBounds(sp.GridX, sp.GridY))
						{
							map.SpawnPoints.Add(sp);
							kept++;
						}
						else
						{
							dropped++;
						}
					}

					if (dropped > 0)
					{
						GD.Print($"[ExportMaps] {s.Id}: {dropped} 个出生点落在导出范围外被丢弃" +
								 "（该地图地形由代码生成，请用编辑器重新摆放出生点）");
					}

					GD.Print($"[ExportMaps] {s.Id}: 导出 {kept} 个出生点" +
							 $"（地形平移 {map.ExportOffsetX},{map.ExportOffsetY}）");
				}

				root.QueueFree();

				if (ValidateAndSave(map, ref failed))
					written++;
			}
		}

		private static TileMapLayer FindTileMapLayer(Node root)
		{
			if (root is TileMapLayer direct) return direct;
			foreach (Node child in root.GetChildren())
			{
				var found = FindTileMapLayer(child);
				if (found != null) return found;
			}
			return null;
		}

		// =========================================================
		// 3. 校验 + 保存
		// =========================================================

		/// <summary>
		/// 保存前先校验：错误就拒绝写出（宁可少一张图，也不要往仓库里塞坏图）。
		/// 警告会打印出来但不阻止保存——那多半是作者有意的取舍。
		/// </summary>
		private static bool ValidateAndSave(RtsMapData map, ref int failed)
		{
			var report = MapValidator.Validate(map);

			if (report.WarningCount > 0)
				GD.Print($"[ExportMaps] {map.MapId} 校验警告：\n{report.ToText()}");

			if (!report.IsValid)
			{
				GD.PrintErr($"[ExportMaps] {map.MapId} 校验失败，拒绝写出：\n{report.ToText()}");
				failed++;
				return false;
			}

			map.LastValidationReport = report.ToText();

			if (!MapRegistry.SaveToMapsDirectory(map, out string error))
			{
				GD.PrintErr($"[ExportMaps] {map.MapId} 保存失败：{error}");
				failed++;
				return false;
			}

			return true;
		}
	}
}
