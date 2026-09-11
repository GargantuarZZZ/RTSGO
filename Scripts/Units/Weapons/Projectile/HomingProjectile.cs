// File: res://Scripts/Units/Weapons/Projectile/HomingProjectile.cs
using Godot;
using RTS.Data;
using RTS.Simulation;

namespace RTS.Units
{
	[GlobalClass]
	public partial class HomingProjectile : Node3D
	{
		[Export] public float Speed = 400.0f;
		[Export] public float HitRadius = 15.0f;
		[Export] public float VisualSize = 16f;
		[Export] public Color BallTint = Colors.White;
		[Export] public bool GlowBall = false;
		[Export] public float HitFxRadius = 36f;
		[Export] public float HitFxDuration = 0.3f;
		[Export] public Color HitFxColor = new Color(1f, 0.75f, 0.2f, 1f);
		public float AoeRadius = 0f;

		private SimProjectile _simData;
		private IEntity _target;
		private float _damage;
		private DamageType _damageType;

		public void Setup(SimProjectile simData, IEntity target, float damage, DamageType dmgType, float hitFxRadius, float hitFxDuration, Color hitFxColor, float aoeRadius = 0f)
		{
			_simData = simData;
			_target = target;
			_damage = damage;
			_damageType = dmgType;
			HitFxRadius = hitFxRadius;
			HitFxDuration = hitFxDuration;
			HitFxColor = hitFxColor;
			AoeRadius = aoeRadius;
			Visible = true;
		}

		public override void _Ready()
		{
			// 无场景文件时自动补一个贴图小球（伪 3D 建模）
			if (GetChildCount() == 0)
			{
				if (GlowBall)
				{
					var sphere = new MeshInstance3D
					{
						Mesh = new SphereMesh
						{
							Radius = VisualSize * 0.5f,
							Height = VisualSize,
							RadialSegments = 16,
							Rings = 10
						}
					};
					// Unshaded 下 Emission 被引擎忽略，亮度只能写在 AlbedoColor 上。
					sphere.MaterialOverride = new StandardMaterial3D
					{
						AlbedoColor = RTS.Units.GlowMaterialFactory.Boost(BallTint, 6f),
						ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
						Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
						CullMode = BaseMaterial3D.CullModeEnum.Disabled
					};
					AddChild(sphere);
					return;
				}

				var mi = new MeshInstance3D
				{
					Mesh = new BoxMesh { Size = new Vector3(VisualSize, VisualSize, VisualSize) }
				};
				mi.MaterialOverride = new StandardMaterial3D
				{
					AlbedoTexture = RTS.Core.TextureLoader3D.LoadPng("res://ArtRes/imgs3d/MagicBall.png"),
					AlbedoColor = BallTint
				};
				AddChild(mi);
			}
		}

		// 纯视觉：平滑追赶后台定点数坐标
		public override void _Process(double delta)
		{
			if (_simData == null)
			{
				QueueFree();
				return;
			}

			if (_simData.HasHit)
			{
				OnHit();
				_simData = null;
				return;
			}

			if (_simData.IsDead)
			{
				QueueFree();
				return;
			}

			// 抛物线曲射：按飞行进度画弧线高度
			float targetY = 0f;

			if (_simData.ParabolicLob && _simData.LobDuration > FixMath.NET.Fix64.Zero)
			{
				float progress = Mathf.Clamp((float)(_simData.LobTime / _simData.LobDuration), 0f, 1f);
				targetY = (float)_simData.LobHeight * Mathf.Sin(progress * Mathf.Pi);
			}
			// 目标可能在弹道飞行途中死亡并被释放：已释放节点不能再碰
			// GlobalPosition，否则原生层 AccessViolation 直接闪退
			// （8 倍速下击杀变多，命中前目标死亡的概率大增）
			else if (_target is Node3D targetNode && GodotObject.IsInstanceValid(targetNode))
			{
				targetY = targetNode.GlobalPosition.Y;
			}
			Vector3 logicPos = new Vector3((float)_simData.Position.X, targetY, (float)_simData.Position.Y);

			if (GlobalPosition.DistanceSquaredTo(logicPos) > 4.0f)
			{
				Vector3 dir = GlobalPosition.DirectionTo(logicPos);
				Rotation = new Vector3(0f, Mathf.Atan2(dir.X, dir.Z), 0f);
			}

			GlobalPosition = GlobalPosition.Lerp(logicPos, (float)delta * 30.0f);
		}

		private void OnHit()
		{
			// 扣血逻辑已由 SimProjectile 完成，这里只做命中特效 + 表现回收
			if (_simData != null)
			{
				RTS.Core.HitFx.Spawn(
					GetTree().Root,
					new Vector2((float)_simData.Position.X, (float)_simData.Position.Y),
					HitFxRadius,
					HitFxDuration,
					HitFxColor,
					AoeRadius
				);
			}

			QueueFree();
		}
	}
}
