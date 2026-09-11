using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using FP = FixMath.NET.Fix64;

namespace RTS.Data.Maps
{
	// =========================================================
	// 地图序列化（纯文本 .rtsmap）
	//
	// 为什么不用 .tres：
	//   Godot 的 ResourceSaver 在保存"运行时构造、含嵌套自定义 Resource"的对象时，
	//   会给嵌套项写出一个**空的 CSharpScript 子资源**，且不写回脚本路径 ——
	//   重新加载时报 "Cannot instantiate C# script because the associated class
	//   could not be found"。实测确认（8 张地图全部加载失败）。
	//   与其和序列化器较劲，不如用自己完全可控的纯文本格式。
	//
	// 格式设计目标：
	//   1. **确定性**：数字一律 InvariantCulture 定点输出；程序化生成的地图
	//      重复导出必须逐字节一致（可直接进 CI 做回归比对）。
	//   2. **可 diff**：每个字段一行，git 里能看出改了什么。
	//   3. **容错**：未知 key 跳过、缺失 key 用默认值，老版本地图仍能打开。
	//   4. **地形压缩**：地形用游程编码（RLE）+ base64，避免几十 KB 的十六进制。
	//
	// 格式：
	//   RTSMAP 1
	//   MapId=...
	//   Width=120
	//   Height=120
	//   TerrainRle=<base64>
	//   BuildBlockedRle=<base64>
	//   Spawn=<slot>,<x>,<y>,<facing>
	//   Entity=<id>,<owner>,<slot>,<x>,<y>,<count>,<scatter>,<spec>
	//   Region=<id>,<shape>,<x>,<y>,<hw>,<hh>,<r>
	//   Trigger=...
	//   TriggerCondition=<triggerIndex>,<expression>
	//   TriggerAction=<triggerIndex>,<kind>,<target>,<amount>,<x>,<y>,<r>,<id>,<count>
	//   Variable=<name>,<value>
	// =========================================================

	public static class MapSerializer
	{
		public const string Extension = ".rtsmap";
		public const string Magic = "RTSMAP";
		public const int Version = 1;

		// =========================================================
		// 保存
		// =========================================================

		public static string Serialize(RtsMapData map)
		{
			if (map == null) throw new ArgumentNullException(nameof(map));

			var sb = new StringBuilder(1 << 16);
			sb.Append(Magic).Append(' ').Append(Version).Append('\n');

			Line(sb, "MapId", map.MapId ?? "");
			Line(sb, "DisplayName", map.DisplayName ?? "");
			Line(sb, "DisplayNameKey", map.DisplayNameKey ?? "");
			Line(sb, "Author", map.Author ?? "");
			Line(sb, "Description", map.Description ?? "");
			Line(sb, "Width", map.Width);
			Line(sb, "Height", map.Height);
			Line(sb, "MaxPlayers", map.MaxPlayers);
			Line(sb, "RecommendedPlayers", map.RecommendedPlayers);
			Line(sb, "ExportOffsetX", map.ExportOffsetX);
			Line(sb, "ExportOffsetY", map.ExportOffsetY);

			// 地形：游程编码（source/variant 组合连续出现时只记 次数+值）
			sb.Append("TerrainRle=").Append(EncodeRle(map.Terrain)).Append('\n');
			sb.Append("BuildBlockedRle=").Append(EncodeRle(map.BuildBlocked)).Append('\n');

			foreach (var s in map.SpawnPoints)
			{
				if (s == null) continue;
				Line(sb, "Spawn", $"{s.TeamSlot},{s.GridX},{s.GridY},{F(s.FacingDegrees)},{s.ReservedForTeam},{Escape(s.Comment)}");
			}

			foreach (var e in map.Entities)
			{
				if (e == null) continue;
				Line(sb, "Entity",
					$"{Escape(e.EntityId)},{(int)e.Owner},{e.TeamSlot},{e.GridX},{e.GridY}," +
					$"{e.Count},{(e.ScatterResources ? 1 : 0)},{Escape(e.ScatterSpec)},{Escape(e.Comment)}");
			}

			foreach (var r in map.Regions)
			{
				if (r == null) continue;
				Line(sb, "Region",
					$"{Escape(r.RegionId)},{Escape(r.DisplayName)},{(int)r.Shape}," +
					$"{r.GridX},{r.GridY},{r.HalfWidth},{r.HalfHeight},{r.Radius}," +
					$"{F(r.EditorColor.R)},{F(r.EditorColor.G)},{F(r.EditorColor.B)},{F(r.EditorColor.A)}");
			}

			foreach (var v in map.Variables)
			{
				if (v == null || string.IsNullOrEmpty(v.Name)) continue;
				Line(sb, "Variable", $"{Escape(v.Name)},{F(v.Value)}");
			}

			// 触发器：主行 + 条件/动作分列（都按触发器下标关联）
			for (int i = 0; i < map.Triggers.Count; i++)
			{
				var t = map.Triggers[i];
				if (t == null) continue;

				Line(sb, "Trigger",
					$"{Escape(t.TriggerId)},{Escape(t.DisplayName)},{(t.EnabledAtStart ? 1 : 0)}," +
					$"{(int)t.Phase},{t.StartTick},{t.IntervalTicks},{(t.Repeatable ? 1 : 0)}," +
					$"{t.CooldownTicks},{(int)t.EvalMode},{Escape(t.Comment)}");

				foreach (var c in t.Conditions)
				{
					if (c == null) continue;
					Line(sb, "TriggerCondition", $"{i},{Escape(c.Expression)},{Escape(c.Comment)}");
				}

				foreach (var a in t.Assignments)
				{
					if (a == null) continue;
					Line(sb, "TriggerAssign", $"{i},{Escape(a.Variable)},{Escape(a.ValueExpression)},{F(a.Value)}");
				}

				foreach (var a in t.Actions)
				{
					if (a == null) continue;
					Line(sb, "TriggerAction",
						$"{i},{(int)a.Kind},{Escape(a.TargetExpression)},{Escape(a.AmountExpression)}," +
						$"{a.GridX},{a.GridY},{a.RadiusTiles},{Escape(a.Id)},{a.Count},{F(a.DelaySeconds)}");
				}
			}

			return sb.ToString();
		}

		private static void Line(StringBuilder sb, string key, object value) =>
			sb.Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');

		private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
		private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

		/// <summary>字段用逗号分隔，所以逗号/换行/反斜杠需要转义。</summary>
		private static string Escape(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Replace("\\", "\\\\").Replace(",", "\\c").Replace("\n", "\\n");
		}

		private static string Unescape(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			var sb = new StringBuilder(s.Length);
			for (int i = 0; i < s.Length; i++)
			{
				if (s[i] == '\\' && i + 1 < s.Length)
				{
					i++;
					sb.Append(s[i] switch { 'c' => ',', 'n' => '\n', '\\' => '\\', _ => s[i] });
				}
				else sb.Append(s[i]);
			}
			return sb.ToString();
		}

		// =========================================================
		// 地形游程编码： (count varint) (byte value) ...
		// 用 base64 包一层，保持纯文本且不被编辑器改动。
		// =========================================================

		private static string EncodeRle(byte[] data)
		{
			if (data == null || data.Length == 0) return "";

			var raw = new List<byte>(data.Length / 4 + 16);
			int i = 0;
			while (i < data.Length)
			{
				byte value = data[i];
				int run = 1;
				while (i + run < data.Length && data[i + run] == value && run < 0xFFFFFF) run++;

				// 变长计数（LEB128）
				int n = run;
				while (n >= 0x80) { raw.Add((byte)(n | 0x80)); n >>= 7; }
				raw.Add((byte)n);
				raw.Add(value);
				i += run;
			}
			return Convert.ToBase64String(raw.ToArray());
		}

		private static byte[] DecodeRle(string text, int expectedLength)
		{
			if (string.IsNullOrEmpty(text)) return expectedLength > 0 ? new byte[expectedLength] : Array.Empty<byte>();

			byte[] raw;
			try { raw = Convert.FromBase64String(text); }
			catch (FormatException) { return new byte[expectedLength]; }

			var outBytes = new List<byte>(expectedLength > 0 ? expectedLength : raw.Length * 4);
			int p = 0;
			while (p < raw.Length && outBytes.Count < (expectedLength > 0 ? expectedLength : int.MaxValue))
			{
				int run = 0;
				int shift = 0;
				while (p < raw.Length)
				{
					byte b = raw[p++];
					run |= (b & 0x7F) << shift;
					if ((b & 0x80) == 0) break;
					shift += 7;
				}
				if (p >= raw.Length) break;
				byte value = raw[p++];
				for (int k = 0; k < run; k++) outBytes.Add(value);
			}

			if (expectedLength > 0 && outBytes.Count != expectedLength)
			{
				// 长度不符时补齐/截断，保证后续不会下标越界
				while (outBytes.Count < expectedLength) outBytes.Add(0);
				if (outBytes.Count > expectedLength) outBytes.RemoveRange(expectedLength, outBytes.Count - expectedLength);
			}

			return outBytes.ToArray();
		}

		// =========================================================
		// 读取
		// =========================================================

		/// <summary>解析失败返回 null 并给出 error（不抛异常，便于批量加载时跳过坏文件）。</summary>
		public static RtsMapData Deserialize(string text, out string error)
		{
			error = null;
			if (string.IsNullOrWhiteSpace(text)) { error = "文件为空"; return null; }

			var map = new RtsMapData();
			var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

			if (lines.Length == 0 || !lines[0].StartsWith(Magic, StringComparison.Ordinal))
			{
				error = $"不是 {Magic} 格式（首行：{lines[0]}）";
				return null;
			}

			string terrainB64 = "";
			string blockedB64 = "";

			// 触发器按下标暂存（先收主行，再收条件/动作）
			var triggers = new List<TriggerDefinition>();

			foreach (string rawLine in lines)
			{
				if (string.IsNullOrEmpty(rawLine)) continue;
				if (rawLine[0] == '#') continue;

				int eq = rawLine.IndexOf('=');
				if (eq <= 0) continue;

				string key = rawLine.Substring(0, eq);
				string val = rawLine.Substring(eq + 1);

				switch (key)
				{
					case "MapId": map.MapId = val; break;
					case "DisplayName": map.DisplayName = val; break;
					case "DisplayNameKey": map.DisplayNameKey = val; break;
					case "Author": map.Author = val; break;
					case "Description": map.Description = val; break;
					case "Width": map.Width = Int(val); break;
					case "Height": map.Height = Int(val); break;
					case "MaxPlayers": map.MaxPlayers = Int(val); break;
					case "RecommendedPlayers": map.RecommendedPlayers = Int(val); break;
					case "ExportOffsetX": map.ExportOffsetX = Int(val); break;
					case "ExportOffsetY": map.ExportOffsetY = Int(val); break;
					case "TerrainRle": terrainB64 = val; break;
					case "BuildBlockedRle": blockedB64 = val; break;

					case "Spawn":
					{
						var p = Parts(val, 6);
						map.SpawnPoints.Add(new MapSpawnPoint
						{
							TeamSlot = Int(p[0]),
							GridX = Int(p[1]),
							GridY = Int(p[2]),
							FacingDegrees = Float(p[3]),
							ReservedForTeam = Int(p[4]),
							Comment = Unescape(p[5]),
						});
						break;
					}

					case "Entity":
					{
						var p = Parts(val, 9);
						map.Entities.Add(new MapEntityPlacement
						{
							EntityId = Unescape(p[0]),
							Owner = (MapEntityOwner)Int(p[1]),
							TeamSlot = Int(p[2]),
							GridX = Int(p[3]),
							GridY = Int(p[4]),
							Count = Int(p[5]),
							ScatterResources = Int(p[6]) != 0,
							ScatterSpec = Unescape(p[7]),
							Comment = Unescape(p[8]),
						});
						break;
					}

					case "Region":
					{
						var p = Parts(val, 12);
						map.Regions.Add(new MapRegion
						{
							RegionId = Unescape(p[0]),
							DisplayName = Unescape(p[1]),
							Shape = (MapRegionShape)Int(p[2]),
							GridX = Int(p[3]),
							GridY = Int(p[4]),
							HalfWidth = Int(p[5]),
							HalfHeight = Int(p[6]),
							Radius = Int(p[7]),
							EditorColor = new Color(Float(p[8]), Float(p[9]), Float(p[10]), Float(p[11])),
						});
						break;
					}

					case "Variable":
					{
						var p = Parts(val, 2);
						map.Variables.Add(new TriggerVariable { Name = Unescape(p[0]), Value = Float(p[1]) });
						break;
					}

					case "Trigger":
					{
						var p = Parts(val, 10);
						triggers.Add(new TriggerDefinition
						{
							TriggerId = Unescape(p[0]),
							DisplayName = Unescape(p[1]),
							EnabledAtStart = Int(p[2]) != 0,
							Phase = (TriggerPhase)Int(p[3]),
							StartTick = Int(p[4]),
							IntervalTicks = Int(p[5]),
							Repeatable = Int(p[6]) != 0,
							CooldownTicks = Int(p[7]),
							EvalMode = (TriggerEvalMode)Int(p[8]),
							Comment = Unescape(p[9]),
						});
						break;
					}

					case "TriggerCondition":
					{
						var p = Parts(val, 3);
						var t = At(triggers, Int(p[0]));
						t?.Conditions.Add(new TriggerCondition(Unescape(p[1])) { Comment = Unescape(p[2]) });
						break;
					}

					case "TriggerAssign":
					{
						var p = Parts(val, 4);
						var t = At(triggers, Int(p[0]));
						t?.Assignments.Add(new TriggerAssignment(Unescape(p[1]), Unescape(p[2])) { Value = Float(p[3]) });
						break;
					}

					case "TriggerAction":
					{
						var p = Parts(val, 10);
						var t = At(triggers, Int(p[0]));
						t?.Actions.Add(new TriggerAction
						{
							Kind = (TriggerActionKind)Int(p[1]),
							TargetExpression = Unescape(p[2]),
							AmountExpression = Unescape(p[3]),
							GridX = Int(p[4]),
							GridY = Int(p[5]),
							RadiusTiles = Int(p[6]),
							Id = Unescape(p[7]),
							Count = Int(p[8]),
							DelaySeconds = Float(p[9]),
						});
						break;
					}
				}
			}

			if (map.Width <= 0 || map.Height <= 0)
			{
				error = $"尺寸非法：{map.Width}x{map.Height}";
				return null;
			}

			int expected = map.Width * map.Height;
			map.Terrain = DecodeRle(terrainB64, expected);
			map.BuildBlocked = string.IsNullOrEmpty(blockedB64) ? new byte[expected] : DecodeRle(blockedB64, expected);

			foreach (var t in triggers) map.Triggers.Add(t);

			if (!map.HasTerrain)
			{
				error = $"地形长度不符：得到 {map.Terrain?.Length ?? 0}，期望 {expected}";
				return null;
			}

			return map;
		}

		// ---- 小工具：字段切分（不 Trim 值，只 Trim 各段两侧空白）----

		private static string[] Parts(string val, int expected)
		{
			var raw = val.Split(',');
			// 保证至少 expected 段，缺失补空串，避免下标越界
			var result = new string[Math.Max(expected, raw.Length)];
			for (int i = 0; i < result.Length; i++)
				result[i] = i < raw.Length ? raw[i].Trim() : "";
			return result;
		}

		private static TriggerDefinition At(List<TriggerDefinition> list, int index) =>
			index >= 0 && index < list.Count ? list[index] : null;

		private static int Int(string s) =>
			int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

		private static float Float(string s) =>
			float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
	}
}
