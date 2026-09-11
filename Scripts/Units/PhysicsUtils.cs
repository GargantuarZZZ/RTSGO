
using Godot;
using System.Linq;

namespace RTS.Core
{
	public static class PhysicsUtils
	{
		// 提取碰撞体轮廓（用于导航障碍物）
		public static Vector2[] GetCollisionOutline(CollisionShape2D colShape)
		{
			if (colShape?.Shape is RectangleShape2D rect)
			{
				var s = rect.Size / 2;
				return new[] { new Vector2(-s.X, -s.Y), new Vector2(s.X, -s.Y), new Vector2(s.X, s.Y), new Vector2(-s.X, s.Y) };
			}
			if (colShape?.Shape is CircleShape2D circle)
			{
				int seg = 8;
				return Enumerable.Range(0, seg)
					.Select(i => Vector2.Right.Rotated(i * Mathf.Tau / seg) * circle.Radius)
					.ToArray();
			}
			return System.Array.Empty<Vector2>();
		}
	}
}
