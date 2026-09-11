// =========================================================
// 仅供确定性测试编译使用的 Godot 最小替身。
//
// 为什么需要它：地图数据格式（RtsMapData）与触发器模型（TriggerDefinition）
// 是 Godot Resource，表达式/触发器测试必须能编译它们。真实游戏引用 GodotSharp，
// 测试工程刻意不引用（避免把引擎拖进无头测试）。
//
// 约束：这里只实现"被测代码实际用到的成员"。新增地图/触发器字段如果用到
// 新的 Godot 类型，必须在这里同步补上——否则测试工程会编译失败（这是刻意的：
// 宁可编译报错，也不要让测试静默跑在一个不完整的替身上）。
// =========================================================

namespace Godot
{
	public class Resource
	{
		public string ResourcePath { get; set; } = "";
	}

	[System.AttributeUsage(System.AttributeTargets.Class)]
	public sealed class GlobalClassAttribute : System.Attribute { }

	[System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field)]
	public sealed class ExportAttribute : System.Attribute
	{
		public ExportAttribute() { }
		public ExportAttribute(PropertyHint hint) { }
	}

	public enum PropertyHint { None = 0, Flags = 1 }

	public struct Vector2
	{
		public float X, Y;
		public Vector2(float x, float y) { X = x; Y = y; }
	}

	public struct Vector2I
	{
		public int X, Y;
		public Vector2I(int x, int y) { X = x; Y = y; }
		public static Vector2I Zero => new Vector2I(0, 0);
	}

	public struct Color
	{
		public float R, G, B, A;
		public Color(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
	}

	public static class Mathf
	{
		public static float Floor(float v) => (float)System.Math.Floor(v);
		public static int FloorToInt(float v) => (int)System.Math.Floor(v);
	}

	/// <summary>
	/// TileMapLayer 的最小替身：只覆盖 MapLoader 用到的成员。
	/// 真实工程里这是 Godot 节点；测试工程只关心"地图数据 ↔ 格子的映射"是否正确。
	/// </summary>
	public class TileMapLayer : Node
	{
		/// <summary>格子坐标 → (sourceId, atlasCoords)。</summary>
		public readonly System.Collections.Generic.Dictionary<Vector2I, (int Source, Vector2I Atlas)> Cells = new();

		public void Clear() => Cells.Clear();

		public void SetCell(Vector2I coords, int sourceId, Vector2I atlasCoords)
			=> Cells[coords] = (sourceId, atlasCoords);

		public System.Collections.Generic.List<Vector2I> GetUsedCells()
			=> new(Cells.Keys);

		public Vector2I GetCellAtlasCoords(Vector2I coords)
			=> Cells.TryGetValue(coords, out var v) ? v.Atlas : Vector2I.Zero;

		/// <summary>由测试注入：某格是否有物理碰撞多边形（= 是否是墙）。</summary>
		public System.Func<Vector2I, bool> CollisionPredicate = _ => false;

		public TileData GetCellTileData(Vector2I coords)
			=> Cells.ContainsKey(coords) && CollisionPredicate(coords) ? new TileData { Blocks = true } : new TileData();
	}

	/// <summary>TileData 的最小替身。</summary>
	public class TileData
	{
		public bool Blocks;
		public int GetCollisionPolygonsCount(int layer) => Blocks ? 1 : 0;
	}

	/// <summary>Node 的最小替身（TileMapLayer / Node2D 继承它）。</summary>
	public class Node
	{
		/// <summary>Godot 的 Node.Name 是 StringName；测试里只需要能当字符串比较。</summary>
		public string Name { get; set; } = "";

		private readonly System.Collections.Generic.List<Node> _children = new();

		public void AddChild(Node child)
		{
			if (child == null) return;
			_children.Add(child);
		}

		public System.Collections.Generic.List<Node> GetChildren() => new(_children);

		/// <summary>按名字查找直接子节点（不含路径语义，测试够用）。</summary>
		public T GetNodeOrNull<T>(string name) where T : Node
		{
			foreach (var c in _children)
				if (c.Name == name && c is T t) return t;
			return null;
		}
	}

	/// <summary>Node2D 的最小替身：出生点标记用它（Position 是世界像素坐标）。</summary>
	public class Node2D : Node
	{
		public Vector2 Position;
	}

	/// <summary>GD.Print 的最小替身，让被测试代码可以调用。</summary>
	public static class GD
	{
		public static void Print(object message) => System.Console.WriteLine(message);
		public static void PrintErr(object message) => System.Console.Error.WriteLine(message);
	}
}

namespace Godot.Collections
{
	/// <summary>Godot 的 Array&lt;T&gt; 替身：只实现地图/触发器代码用到的最小集合语义。</summary>
	public class Array<T> : System.Collections.Generic.List<T>
	{
		public Array() { }
		public Array(System.Collections.Generic.IEnumerable<T> items) : base(items) { }
	}
}
