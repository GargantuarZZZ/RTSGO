using Godot;
using RTS.Data.Maps;
using RTS.World;

namespace RTS.Tools
{
	// =========================================================
	// 重新导出一张地图，使 .rtsmap 与**运行时真实地形**一致。
	//
	// ---- 为什么需要这个工具 ----
	//
	// `Scripts/Tools/ExportMaps.cs` 导出时是直接读场景资源里的 TileMapLayer：
	//     var packed = GD.Load<PackedScene>(path);
	//     Node root = packed.Instantiate();     // ← _Ready 不会执行
	//     var layer = FindTileMapLayer(root);
	//     MapLoader.ExportFromTileMap(layer, id);
	//
	// 但 1v1 的地形是**代码生成**的（MapGrid.BuildSmallMap1v1，在 MapGrid._Ready 里跑），
	// 场景文件里几乎没有瓦片。于是导出出来的是一张**全地板空图**。
	//
	// 后果：Game.LoadActiveMap 发现 1v1.rtsmap 存在且 HasTerrain，
	// 就走 ApplyDataMap → BaseMapLayer.Clear() + 写入空地板，
	// **把刚生成的墙全冲掉** —— 从菜单选 1v1 会得到一张没有墙的地图。
	//
	// 本工具的做法：把场景**真正加进树**，等一帧让 MapGrid._Ready 跑完
	// （地形已生成到 BaseMapLayer），再导出。这样导出的就是运行时看到的地形。
	//
	// 用法：
	//   godot --headless --path . res://Scenes/Tools/ReexportScenarioMap.tscn -- \
	//       --scene=res://Scenes/main_1v1.tscn --id=1v1 --name=1v1 断墙小图
	// =========================================================

	public partial class ReexportScenarioMap : Node
	{
		private static int _instances;
		private string _scenePath;
		private string _mapId;
		private string _displayName;
		private string _tag;

		public override void _Ready()
		{
			_instances++;
			_tag = $"#{_instances}";
			GD.Print($"[Reexport]{_tag} _Ready (路径={GetPath()})");
			// 判据用 ArgValue 而不是 HasFlag：
			// 调用方传的是 `--scene=xxx`（等号形式），
			// HasFlag("--scene") 要求参数是**独立**的一个 `--scene`，两者不匹配。
			// ArgValue 同时支持 `--key=value` 与 `--key value`，不会踩这个坑。
			_scenePath = ArgValue("--scene");
			_mapId = ArgValue("--id");
			_displayName = ArgValue("--name");

			if (string.IsNullOrEmpty(_scenePath))
			{
				GD.Print($"[Reexport]{_tag} 未指定 --scene，跳过。实际收到的参数：");
				foreach (string a in AllArgs()) GD.Print("   [" + a + "]");
				GetTree().Quit(0);
				return;
			}

			GD.Print($"[Reexport]{_tag} 解析结果：scene='{_scenePath}' id='{_mapId}'");
			if (string.IsNullOrEmpty(_mapId)) _mapId = "1v1";
			if (string.IsNullOrEmpty(_displayName)) _displayName = _mapId;

			// 必须延迟：等场景进树并跑完 _Ready（含代码生成地形）
			Callable.From(Run).CallDeferred();
		}

		private async void Run()
		{
			if (!ResourceLoader.Exists(_scenePath))
			{
				GD.PrintErr($"[Reexport] 场景不存在：{_scenePath}");
				GetTree().Quit(1);
				return;
			}

			var packed = GD.Load<PackedScene>(_scenePath);
			Node root = packed.Instantiate();
			AddChild(root);           // ★ 关键：进树才会执行 _Ready → 生成地形

			// 等若干帧，确保 MapGrid._Ready 与它的 CallDeferred(InitSimGrid) 都跑完
			for (int i = 0; i < 5; i++)
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

			// ★ 关键：不能读 MapGrid.Instance（静态单例）。
			// 引擎启动时可能已经自动加载了主场景（main.tscn），
			// 把单例占成那张图 —— 那样导出出来的会是**另一张地图**
			// （实测：本想导 1v1，结果导出了 Classic 的 200x130）。
			// 必须从**本次实例化的这棵树**里找 MapGrid。
			var grid = FindGrid(root);
			if (grid?.BaseMapLayer == null)
			{
				GD.PrintErr("[Reexport] 实例化的场景里找不到 MapGrid/BaseMapLayer，无法导出。");
				root.QueueFree();
				GetTree().Quit(1);
				return;
			}

			var cells = grid.BaseMapLayer.GetUsedCells();
			int cx0 = int.MaxValue, cy0 = int.MaxValue, cx1 = int.MinValue, cy1 = int.MinValue;
			foreach (var c in cells)
			{
				if (c.X < cx0) cx0 = c.X;
				if (c.Y < cy0) cy0 = c.Y;
				if (c.X > cx1) cx1 = c.X;
				if (c.Y > cy1) cy1 = c.Y;
			}
			GD.Print($"[Reexport] 读到的 MapGrid 路径={grid.GetPath()} " +
				$"MapPresetId={grid.MapPresetId} 格数={cells.Count} " +
				$"范围 x∈[{cx0},{cx1}] y∈[{cy0},{cy1}]");
			GD.Print($"[Reexport] 实例化的 root 路径={root.GetPath()} 名字={root.Name}");


			// 从这里开始与 ExportMaps 一致的导出路径
			var map = MapLoader.ExportFromTileMap(grid.BaseMapLayer, _mapId);
			map.DisplayName = _displayName;
			map.Author = "scenario-import";

			// 出生点：同一次导出必须用同一个平移量
			var spawnContainer = root.GetNodeOrNull("SpawnPoints");
			if (spawnContainer != null)
			{
				var spawnMap = MapRegistry.ExportSpawnPointsOnly(
					spawnContainer, _mapId, map.ExportOffsetX, map.ExportOffsetY);

				int kept = 0, dropped = 0;
				foreach (var sp in spawnMap.SpawnPoints)
				{
					if (map.InBounds(sp.GridX, sp.GridY)) { map.SpawnPoints.Add(sp); kept++; }
					else dropped++;
				}
				GD.Print($"[Reexport] 出生点：保留 {kept}，范围外丢弃 {dropped}");
			}

			// MaxPlayers 与出生点数保持一致，避免"声明 8 人却只有 1 个出生点"
			int spawnCount = map.SpawnPoints.Count;
			if (spawnCount > 0)
			{
				map.MaxPlayers = spawnCount;
				map.RecommendedPlayers = System.Math.Min(spawnCount, 2);
			}

			// ---- 安全闸：默认只做试运行，不写盘 ----
			//
			// 本工具目前**会写错文件**：实测把 1v1 导出成了 Classic 的地形
			// （26000 格 / 范围 0..199 / (0,0) 是墙），说明读到的 BaseMapLayer
			// 不是 1v1 代码生成的那一份。
			// 在定位清楚之前禁止默认写盘 —— 必须显式 --write 才保存，
			// 避免"跑一下试试"就把 Maps/*.rtsmap 覆盖坏。
			if (HasFlag("--write"))
			{
				if (!MapRegistry.SaveToMapsDirectory(map, out string saveErr))
				{
					GD.PrintErr($"[Reexport] 保存失败：{saveErr}");
					root.QueueFree();
					GetTree().Quit(1);
					return;
				}
				GD.Print($"[Reexport] 已写出 {_mapId}.rtsmap");
			}
			else
			{
				GD.Print("[Reexport] 试运行（未写盘）。确认数据正确后再加 --write。");
			}

			GD.Print($"[Reexport] 结果：{map.Width}x{map.Height} 出生点={spawnCount} " +
				$"MaxPlayers={map.MaxPlayers} 偏移=({map.ExportOffsetX},{map.ExportOffsetY})");

			root.QueueFree();
			GetTree().Quit(0);
		}

		/// <summary>在实例化的场景树里找 MapGrid（避免用到被主场景占住的静态单例）。</summary>
		private static RTS.World.MapGrid FindGrid(Node root)
		{
			if (root is RTS.World.MapGrid g) return g;
			foreach (var c in root.GetChildren())
			{
				var found = FindGrid(c);
				if (found != null) return found;
			}
			return null;
		}

		// --- 参数解析 ---
		//
		// 注意：Godot 的 `OS.GetCmdlineUserArgs()` 只返回 `--` 之后的参数，
		// 而 `OS.GetCmdlineArgs()` 返回全部（含引擎参数）。
		// 两个都查，避免"调用方到底加不加 --"把工具搞哑。
		private static string[] AllArgs()
		{
			var merged = new System.Collections.Generic.List<string>();
			foreach (string a in OS.GetCmdlineUserArgs()) merged.Add(a);
			foreach (string a in OS.GetCmdlineArgs())
				if (!merged.Contains(a)) merged.Add(a);
			return merged.ToArray();
		}

		private static bool HasFlag(string flag)
		{
			foreach (string a in AllArgs())
				if (a == flag) return true;
			return false;
		}

		private static string ArgValue(string key)
		{
			var args = AllArgs();
			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == key && i + 1 < args.Length) return args[i + 1];
				string prefix = key + "=";
				if (args[i].StartsWith(prefix)) return args[i].Substring(prefix.Length);
			}
			return null;
		}
	}
}
