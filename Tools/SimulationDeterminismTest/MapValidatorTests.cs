using System;
using RTS.Data;
using RTS.Data.Maps;
using RTS.Data;
using RTS.MapEditing;

namespace SimulationDeterminismTest
{
	// =========================================================
	// 地图校验器测试
	//
	// 思路：为每一条校验规则单独造一张"只坏在这一处"的地图，
	// 断言校验器确实报出对应错误 —— 校验器的价值全在于"能抓到问题"，
	// 只测"好地图通过"是不够的。
	// =========================================================
	internal static partial class Program
	{
		private static MapValidationReport ValidateMap(RtsMapData map) => MapValidator.Validate(map);

		private static bool HasIssue(MapValidationReport r, string code, MapIssueSeverity? severity = null)
		{
			foreach (var i in r.Issues)
				if (i.Code == code && (severity == null || i.Severity == severity))
					return true;
			return false;
		}

		/// <summary>造一张干净的小地图：全草地 + 封边，方便逐条注入故障。</summary>
		private static RtsMapData MakeCleanMap(int w = 64, int h = 64)
		{
			var map = new RtsMapData { MapId = "clean", DisplayName = "Clean" };
			map.Resize(w, h);
			map.Fill(RtsMapData.SourceGrass);
			MapDataOps.StampBorder(map, 1);
			return map;
		}

		private static void AddSpawn(RtsMapData map, int slot, int x, int y) =>
			map.SpawnPoints.Add(new MapSpawnPoint { TeamSlot = slot, GridX = x, GridY = y });

		private static void AddResource(RtsMapData map, string id, int x, int y) =>
			map.Entities.Add(new MapEntityPlacement
			{
				EntityId = id,
				Owner = MapEntityOwner.Neutral,
				GridX = x,
				GridY = y,
			});

		private static void RunMapValidatorTests()
		{
			// ---------- 1. 干净地图应当通过 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);

				// 两个出生点都要有就近资源，否则合理的开局警告会变成错误
				for (int i = 0; i < 6; i++)
				{
					AddResource(map, "IronOre", 12 + i, 12);
					AddResource(map, "GasSpring", 12 + i, 14);
					AddResource(map, "IronOre", 48 + i, 48);
					AddResource(map, "GasSpring", 48 + i, 46);
				}

				var r = ValidateMap(map);
				Check(r.IsValid, $"validator: clean map passes with no errors (errors={r.ErrorCount}: {FirstError(r)})");
			}

			// ---------- 2. 缺出生点 ----------
			{
				var map = MakeCleanMap();
				AddResource(map, "IronOre", 20, 20);
				var r = ValidateMap(map);
				Check(HasIssue(r, "no_spawn", MapIssueSeverity.Error),
					"validator: map without spawn points is an error");
			}

			// ---------- 3. 出生点重叠 / 槽位重复 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 20, 20);
				AddSpawn(map, 1, 20, 20);   // 同槽位 + 同格子
				var r = ValidateMap(map);
				Check(HasIssue(r, "dup_slot", MapIssueSeverity.Error), "validator: duplicate spawn slot is an error");
				Check(HasIssue(r, "dup_cell", MapIssueSeverity.Error), "validator: two spawns on one cell is an error");
			}

			// ---------- 4. 出生点在墙里 ----------
			{
				var map = MakeCleanMap();
				map.SetCell(20, 20, RtsMapData.SourceWall);
				AddSpawn(map, 1, 20, 20);
				AddSpawn(map, 2, 45, 45);
				var r = ValidateMap(map);
				Check(HasIssue(r, "spawn_in_wall", MapIssueSeverity.Error),
					"validator: spawn inside a wall is an error");
			}

			// ---------- 5. 出生点被墙围死（真实寻路判定）----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 20, 20);
				AddSpawn(map, 2, 45, 45);

				// 把 (20,20) 用一个 3x3 的封闭盒围起来（中心留空）
				for (int x = 19; x <= 21; x++)
					for (int y = 19; y <= 21; y++)
						if (!(x == 20 && y == 20))
							map.SetCell(x, y, RtsMapData.SourceWall);

				var r = ValidateMap(map);
				Check(HasIssue(r, "spawn_unreachable", MapIssueSeverity.Error),
					"validator: sealed-off spawn is reported unreachable via real pathfinding");
			}

			// ---------- 6. 地面被分割 ----------
			{
				var map = MakeCleanMap();
				// 一道贯穿的竖墙把地图切成两半
				for (int y = 0; y < map.Height; y++)
					map.SetCell(32, y, RtsMapData.SourceWall);

				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				var r = ValidateMap(map);
				Check(HasIssue(r, "floor_disconnected", MapIssueSeverity.Error),
					"validator: split floor is detected as disconnected");
				Check(HasIssue(r, "spawn_unreachable", MapIssueSeverity.Error),
					"validator: spawns across the split are unreachable");
			}

			// ---------- 7. 资源在墙里 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				map.SetCell(30, 30, RtsMapData.SourceWall);
				AddResource(map, "IronOre", 30, 30);
				// 补足保底资源，避免触发"资源稀缺"警告干扰本用例
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				var r = ValidateMap(map);
				Check(HasIssue(r, "entity_in_wall", MapIssueSeverity.Error),
					"validator: resource buried in a wall is an error");
			}

			// ---------- 8. 实体重叠 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				AddResource(map, "IronOre", 25, 25);
				AddResource(map, "GasSpring", 25, 25);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				var r = ValidateMap(map);
				Check(HasIssue(r, "entity_overlap", MapIssueSeverity.Error),
					"validator: two entities on one cell is an error");
			}

			// ---------- 9. 出生点没有资源 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				// 只给出生点 1 附近放资源；出生点 2 附近什么都没有
				for (int i = 0; i < 6; i++) AddResource(map, "IronOre", 12 + i, 12);

				var r = ValidateMap(map);
				// 出生点 2 会同时触发两条：最近资源过远 + 附近资源一个都到不了
				Check(HasIssue(r, "resource_too_far", MapIssueSeverity.Error) ||
					  HasIssue(r, "spawn_no_resource", MapIssueSeverity.Error),
					"validator: spawn without nearby resources is an error");
			}

			// ---------- 10. 触发器：ID 重复与跨引用 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				var t1 = new TriggerDefinition { TriggerId = "dup" };
				var t2 = new TriggerDefinition { TriggerId = "dup" };
				map.Triggers.Add(t1);
				map.Triggers.Add(t2);

				var r = ValidateMap(map);
				Check(HasIssue(r, "trigger_dup_id", MapIssueSeverity.Error),
					"validator: duplicate trigger id is an error");
			}

			// ---------- 11. 触发器：条件语法错误 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				var t = new TriggerDefinition { TriggerId = "bad" };
				t.Conditions.Add(new TriggerCondition("1 + "));
				map.Triggers.Add(t);

				var r = ValidateMap(map);
				Check(HasIssue(r, "trigger_bad_expr", MapIssueSeverity.Error),
					"validator: trigger with bad expression syntax is an error");
			}

			// ---------- 12. 触发器：引用不存在的区域 / 触发器 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				var t = new TriggerDefinition { TriggerId = "refs" };
				t.Conditions.Add(new TriggerCondition("units_in('missing_region', 1) > 0"));
				t.Conditions.Add(new TriggerCondition("trigger_ghost_fired == 0"));
				map.Triggers.Add(t);

				var r = ValidateMap(map);
				Check(HasIssue(r, "expr_unknown_region", MapIssueSeverity.Error),
					"validator: reference to a missing region is an error");
				Check(HasIssue(r, "expr_unknown_trigger", MapIssueSeverity.Error),
					"validator: reference to a missing trigger state is an error");
			}

			// ---------- 13. 触发器：SetTriggerEnabled 指向不存在的触发器 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				var t = new TriggerDefinition { TriggerId = "enabler" };
				t.Actions.Add(new TriggerAction { Kind = TriggerActionKind.SetTriggerEnabled, Id = "nope", Count = 1 });
				map.Triggers.Add(t);

				var r = ValidateMap(map);
				Check(HasIssue(r, "action_unknown_trigger", MapIssueSeverity.Error),
					"validator: enabling a non-existent trigger is an error");
			}

			// ---------- 14. 触发器：刷兵点在地图外/墙里 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				var t = new TriggerDefinition { TriggerId = "spawner" };
				t.Actions.Add(new TriggerAction { Kind = TriggerActionKind.SpawnUnit, Id = "RifleMan", GridX = 999, GridY = 999 });
				map.Triggers.Add(t);

				var r = ValidateMap(map);
				Check(HasIssue(r, "action_spawn_oob", MapIssueSeverity.Error),
					"validator: out-of-bounds spawn action is an error");
			}

			// ---------- 15. 触发器：初始禁用但没有人会启用（警告）----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				map.Triggers.Add(new TriggerDefinition
				{
					TriggerId = "orphan",
					EnabledAtStart = false,
					Phase = TriggerPhase.EveryTick,
				});

				var r = ValidateMap(map);
				Check(HasIssue(r, "trigger_never_enabled", MapIssueSeverity.Warning),
					"validator: disabled trigger with no enabler is a warning");
				Check(r.IsValid, "validator: that warning alone does not fail the map");
			}

			// ---------- 16. 建造禁区封死出生点 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				// 整张地图全禁建，只在远处留一个 1x1 —— 放不下任何基地
				for (int x = 0; x < map.Width; x++)
					for (int y = 0; y < map.Height; y++)
						map.SetBuildBlocked(x, y, true);
				map.SetBuildBlocked(50, 50, false);

				var r = ValidateMap(map);
				Check(HasIssue(r, "spawn_no_build", MapIssueSeverity.Error) ||
					  HasIssue(r, "spawn_tight_build", MapIssueSeverity.Warning),
					"validator: spawn with no room for a base footprint is reported");
			}

			// ---------- 17. 生成器产出的地图应当通过校验 ----------
			{
				foreach (MapStyle style in Enum.GetValues(typeof(MapStyle)))
				{
					var settings = new MapGenSettings
					{
						Width = 96, Height = 96, Seed = 2024,
						Style = style, Symmetry = MapSymmetry.Rotational180,
						SpawnPointCount = 2, NeutralTowerCount = 2,
					};
					var map = MapGenerator.Generate(settings, out _);
					var r = ValidateMap(map);
					Check(r.IsValid,
						$"validator: generated {style} map passes (errors={r.ErrorCount}: {FirstError(r)})");
				}
			}

			// ---------- 18. 报告文本可用 ----------
			{
				var map = MakeCleanMap();
				var r = ValidateMap(map);
				string text = r.ToText();
				Check(!string.IsNullOrEmpty(text), "validator: report renders to text");
				Check(text.Contains("错误") || text.Contains("校验"),
					$"validator: report text is readable ({text.Split('\n')[0]})");
			}

			// ---------- 19. 校验不改动地图 ----------
			{
				var map = MakeCleanMap();
				AddSpawn(map, 1, 10, 10);
				AddSpawn(map, 2, 50, 50);
				for (int i = 0; i < 6; i++) { AddResource(map, "IronOre", 12 + i, 12); AddResource(map, "IronOre", 48 + i, 48); }

				var before = (byte[])map.Terrain.Clone();
				ValidateMap(map);
				bool unchanged = true;
				for (int i = 0; i < before.Length; i++)
					if (before[i] != map.Terrain[i]) { unchanged = false; break; }
				Check(unchanged, "validator: validation is read-only (terrain untouched)");
			}
		}

		private static string FirstError(MapValidationReport r)
		{
			foreach (var i in r.Issues)
				if (i.Severity == MapIssueSeverity.Error) return i.Code + ": " + i.Message;
			return "none";
		}
	}
}
