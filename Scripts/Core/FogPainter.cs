using Godot;
using RTS.Data; // 引用 IEntity
using System.Collections.Generic;

namespace RTS.World
{
	public partial class FogPainter : Node2D
	{
		[Export] public Color DrawColor = Colors.White;

		public override void _Draw()
		{
			if (FogOfWar.Instance == null) return;

			// 获取 IEntity 列表
			List<IEntity> sources = FogOfWar.Instance.GetVisionSources();

			foreach (var source in sources)
			{
				if (source == null || source.IsDeadOrNull()) continue;

				// 1. 坐标转换
				Vector2 fogPos = FogOfWar.Instance.WorldToFogPos(source.GlobalPosition);

				// 2. 获取该实体的视野半径
				float worldRadius = source.VisionRange;

				// 3. 转换为迷雾半径
				float fogRadius = FogOfWar.Instance.WorldToFogRadius(worldRadius);

				// 4. 绘制
				DrawCircle(fogPos, fogRadius, DrawColor);
			}
		}
	}
}
