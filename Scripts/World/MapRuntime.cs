using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using RTS.Data;
using RTS.Data.Maps;
using RTS.Simulation.Scripting;
using FP = FixMath.NET.Fix64;

namespace RTS.World
{
	// =========================================================
	// 运行时地图
	//
	// 为什么不让游戏直接吃 RtsMapData：
	//   RtsMapData 是**作者数据**（Godot Resource、可为空的 Array、编辑器态），
	//   而运行时需要的是"索引化、无 Godot 依赖、可安全被模拟线程读"的结构。
	//   两者混用会把"资源里恰好为空"这类编辑器态问题带进模拟线程。
	//
	// 另：Determinism 相关字段（触发器定义、区域）会被纳入世界哈希的
	//    TriggerRuntimeState 覆盖，这里只负责"装"，不负责"判"。
	// =========================================================

	public sealed class MapRuntime
	{
		public string MapId = "";
		public string DisplayName = "";
		public int Width;
		public int Height;

		/// <summary>
		/// 导出平移量：地图从"场景旧世界坐标"平移到"格坐标"时用的偏移。
		///
		/// 它不只是元数据 —— 旧版硬编码中立物（Game.SpawnLegacyHardcodedNeutrals）
		/// 用的就是旧世界坐标，必须靠这个偏移量精确换算到当前地图上，
		/// 否则中立单位会被放到错误的比例/位置上（实测过）。
		/// 换算：格 = 旧世界坐标 / 64 + ExportOffset；当前世界坐标 = 格 * 64 + 32。
		/// </summary>
		public int ExportOffsetX;
		public int ExportOffsetY;

		/// <summary>出生点，按 TeamSlot 升序（模拟层按固定顺序取用，保证两端一致）。</summary>
		public readonly List<MapSpawnPoint> Spawns = new();

		/// <summary>摆放的中立物/资源（按作者列表顺序，确定性）。</summary>
		public readonly List<MapEntityPlacement> Entities = new();

		/// <summary>命名区域索引。</summary>
		public readonly Dictionary<string, MapRegion> Regions = new(StringComparer.Ordinal);

		/// <summary>触发器定义（按作者数组顺序 = 执行顺序）。</summary>
		public readonly List<TriggerDefinition> Triggers = new();

		public bool HasTriggers => Triggers.Count > 0;

		public MapSpawnPoint GetSpawn(int teamSlot)
		{
			foreach (var s in Spawns)
				if (s.TeamSlot == teamSlot) return s;
			return null;
		}

		public MapRegion FindRegion(string id) =>
			!string.IsNullOrEmpty(id) && Regions.TryGetValue(id, out var r) ? r : null;

		/// <summary>
		/// 从作者数据构建运行时地图。
		/// 会做**保序**处理：出生点按 TeamSlot 排序、区域按 ID 建索引——
		/// 遍历顺序在模拟层必须确定，不能依赖 Godot 容器的枚举顺序。
		/// </summary>
		public static MapRuntime FromData(RtsMapData data)
		{
			var rt = new MapRuntime();
			if (data == null) return rt;

			rt.MapId = data.MapId ?? "";
			rt.DisplayName = string.IsNullOrEmpty(data.DisplayName) ? rt.MapId : data.DisplayName;
			rt.Width = data.Width;
			rt.Height = data.Height;
			rt.ExportOffsetX = data.ExportOffsetX;
			rt.ExportOffsetY = data.ExportOffsetY;

			if (data.SpawnPoints != null)
			{
				foreach (var s in data.SpawnPoints)
					if (s != null) rt.Spawns.Add(s);
				rt.Spawns.Sort((a, b) => a.TeamSlot.CompareTo(b.TeamSlot));
			}

			if (data.Entities != null)
				foreach (var e in data.Entities)
					if (e != null) rt.Entities.Add(e);

			if (data.Regions != null)
			{
				foreach (var r in data.Regions)
				{
					if (r == null || string.IsNullOrEmpty(r.RegionId)) continue;
					rt.Regions[r.RegionId] = r;
				}
			}

			if (data.Triggers != null)
				foreach (var t in data.Triggers)
					if (t != null) rt.Triggers.Add(t);

			return rt;
		}

		/// <summary>诊断用摘要（不参与哈希）。</summary>
		public string Describe() =>
			$"{DisplayName} [{MapId}] {Width}x{Height} 出生点={Spawns.Count} 实体={Entities.Count} 区域={Regions.Count} 触发器={Triggers.Count}";
	}
}
