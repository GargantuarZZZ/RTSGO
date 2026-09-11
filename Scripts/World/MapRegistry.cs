using System;
using System.Collections.Generic;
using Godot;
using RTS.Data.Maps;

namespace RTS.World
{
	// =========================================================
	// 地图注册表
	//
	// 目标：主菜单能**动态**列出可用地图，新增一张 .tres 地图不再需要改代码。
	// 现状是 MainMenuController 里写死 "Classic"/"1v1"/"Debug" 三个字符串 +
	// 三个 if/else 分支，加地图必须改 C#——这里把它换掉。
	//
	// 加载策略：
	//   1. 扫描 res://Maps/ 下的 .tres（导出后会出现 .remap，需要去掉后缀）；
	//   2. 结果按 MapId 排序，保证两端/两次运行的列表顺序一致；
	//   3. 同时保留内置的经典场景地图作为兜底（老地图还没有 .tres 版本）。
	// =========================================================

	public static class MapRegistry
	{
		public const string MapsDirectory = "res://Maps";

		private static readonly Dictionary<string, RtsMapData> _maps = new(StringComparer.Ordinal);
		private static bool _loaded;

		/// <summary>已注册的地图数量。</summary>
		public static int Count => _maps.Count;

		/// <summary>
		/// 扫描地图目录。可重复调用；已加载则直接返回。
		/// </summary>
		public static void LoadAll(bool forceReload = false)
		{
			if (_loaded && !forceReload) return;
			_loaded = true;
			_maps.Clear();

			using var dir = DirAccess.Open(MapsDirectory);
			if (dir == null)
			{
				GD.Print($"[MapRegistry] 地图目录不存在（{MapsDirectory}），跳过。");
				return;
			}

			var files = new List<string>();
			dir.ListDirBegin();
			string fileName = dir.GetNext();
			while (fileName != "")
			{
				if (!dir.CurrentIsDir())
				{
					// 导出包里资源带 .remap 后缀，先剥掉再判断
					string clean = fileName.EndsWith(".remap")
						? fileName.Substring(0, fileName.Length - ".remap".Length)
						: fileName;

					if (clean.EndsWith(MapSerializer.Extension))
						files.Add(clean);
				}
				fileName = dir.GetNext();
			}

			// 文件系统枚举顺序跨端不保证一致 → 先排序再加载
			files.Sort(StringComparer.Ordinal);

			foreach (string file in files)
			{
				string path = $"{MapsDirectory}/{file}";
				var data = LoadFromPath(path, out string error);

				if (data == null)
				{
					// 单张坏图不能拖垮整个列表：报错并跳过
					GD.PrintErr($"[MapRegistry] 跳过损坏的地图 {path}：{error}");
					continue;
				}

				// MapId 为空时用文件名兜底，保证一定能被主菜单引用
				if (string.IsNullOrWhiteSpace(data.MapId))
					data.MapId = file.GetBaseName();

				if (_maps.ContainsKey(data.MapId))
				{
					GD.PrintErr($"[MapRegistry] MapId 冲突：'{data.MapId}'（{path}），后者被忽略。");
					continue;
				}

				_maps[data.MapId] = data;
			}

			GD.Print($"[MapRegistry] 已加载 {_maps.Count} 张地图：{string.Join(", ", SortedIds())}");
		}

		/// <summary>按 MapId 升序返回全部 id（确定性顺序）。</summary>
		public static List<string> SortedIds()
		{
			var list = new List<string>(_maps.Keys);
			list.Sort(StringComparer.Ordinal);
			return list;
		}

		/// <summary>
		/// 解析这一局要用的地图数据。
		/// 优先级：教程自带地图 → 注册表里的 .rtsmap → null（调用方回退到场景内置地图）。
		/// </summary>
		public static RtsMapData Resolve(string mapId)
		{
			// 教程地图是**运行时现造**的，不依赖 res://Maps 里有没有文件，
			// 保证"点教程一定能开始"（玩家不该因为缺地图文件而进不去教程）。
			if (mapId == RTS.Tutorial.TutorialRegistry.TutorialMapId)
				return RTS.Tutorial.TutorialRegistry.BuildTutorialMap();

			LoadAll();
			return Get(mapId);
		}

		/// <summary>
		/// 把场景里的 SpawnPoints/Spawn_N 标记导出成地图数据。
		///
		/// **坐标系必须与地形导出对齐**：`MapLoader.ExportFromTileMap` 会把 TileMap 的
		/// 负坐标平移到 0 起点，出生点如果直接用场景里的世界坐标，就会和地形对不上
		/// （实测过：Classic 场景的出生点导出后变成 (-90,-13) 这种地图外的坐标）。
		/// 因此这里接受 (offsetX, offsetY)，由调用方传入与地形相同的平移量。
		/// </summary>
		public static RtsMapData ExportSpawnPointsOnly(Node spawnContainer, string mapId,
			int offsetX = 0, int offsetY = 0)
		{
			var map = new RtsMapData { MapId = mapId, DisplayName = mapId };
			if (spawnContainer == null) return map;

			foreach (Node child in spawnContainer.GetChildren())
			{
				if (!child.Name.ToString().StartsWith("Spawn_", StringComparison.Ordinal)) continue;
				if (!int.TryParse(child.Name.ToString().Substring("Spawn_".Length), out int slot)) continue;
				if (child is not Node2D marker) continue;

				// 场景坐标（像素）→ 格 → 减去地形平移量
				int gx = (int)Mathf.Floor(marker.Position.X / RtsMapData.TileSize) - offsetX;
				int gy = (int)Mathf.Floor(marker.Position.Y / RtsMapData.TileSize) - offsetY;

				map.SpawnPoints.Add(new MapSpawnPoint
				{
					TeamSlot = slot,
					GridX = gx,
					GridY = gy,
					Comment = "从场景标记导出",
				});
			}

			return map;
		}

		public static RtsMapData Get(string mapId) =>
			!string.IsNullOrEmpty(mapId) && _maps.TryGetValue(mapId, out var m) ? m : null;

		public static bool Has(string mapId) => !string.IsNullOrEmpty(mapId) && _maps.ContainsKey(mapId);

		/// <summary>从磁盘加载一张指定路径的地图（编辑器用，不走缓存）。</summary>
		public static RtsMapData LoadFromPath(string resPath) => LoadFromPath(resPath, out _);

		/// <summary>从磁盘加载地图；失败返回 null 并给出可读原因（不抛异常）。</summary>
		public static RtsMapData LoadFromPath(string resPath, out string error)
		{
			error = null;
			if (string.IsNullOrEmpty(resPath)) { error = "路径为空"; return null; }

			using var f = FileAccess.Open(resPath, FileAccess.ModeFlags.Read);
			if (f == null)
			{
				error = $"无法打开文件（{FileAccess.GetOpenError()}）";
				return null;
			}

			string text = f.GetAsText();
			f.Close();
			return MapSerializer.Deserialize(text, out error);
		}

		/// <summary>
		/// 保存地图到 res://Maps/&lt;MapId&gt;.rtsmap。
		/// 注意：res:// 在**导出后的游戏**里是只读的，这个入口只给编辑器用。
		///
		/// 用自研纯文本格式而不是 .tres：Godot 的 ResourceSaver 保存"运行时构造 +
		/// 含嵌套自定义 Resource"的对象时会写出空的 CSharpScript 子资源，
		/// 重新加载直接失败（实测 8 张地图全部加载不出来）。
		/// </summary>
		public static bool SaveToMapsDirectory(RtsMapData map, out string error)
		{
			error = null;

			if (map == null) { error = "地图数据为空"; return false; }
			if (string.IsNullOrWhiteSpace(map.MapId)) { error = "地图没有 MapId"; return false; }
			if (map.MapId.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
			{
				error = $"MapId 含非法文件名字符：{map.MapId}";
				return false;
			}

			// 确保目录存在（编辑器首次保存时可能还没有）
			if (!DirAccess.DirExistsAbsolute(MapsDirectory))
			{
				var err = DirAccess.MakeDirRecursiveAbsolute(MapsDirectory);
				if (err != Error.Ok)
				{
					error = $"无法创建目录 {MapsDirectory}：{err}";
					return false;
				}
			}

			string path = $"{MapsDirectory}/{map.MapId}{MapSerializer.Extension}";

			using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
			if (f == null)
			{
				error = $"无法写入 {path}（{FileAccess.GetOpenError()}）";
				return false;
			}

			f.StoreString(MapSerializer.Serialize(map));
			f.Close();

			// 让主菜单下次读取时看到新地图
			_loaded = false;
			GD.Print($"[MapRegistry] 已保存地图：{path}");
			return true;
		}
	}
}
