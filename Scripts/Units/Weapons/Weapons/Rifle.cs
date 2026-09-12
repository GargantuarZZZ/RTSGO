using Godot;
using RTS.Data;

namespace RTS.Units
{
	[GlobalClass]
	public partial class Rifle : Weapon
	{
		[ExportGroup("Visuals")]
		[Export] public AudioStreamPlayer2D ShootSound;
		[Export] public Color TracerColor { get; set; } = Colors.Yellow;
		[Export] public float HitFxRadius { get; set; } = 36f;
		[Export] public float HitFxDuration { get; set; } = 0.3f;
		[Export] public Color HitFxColor { get; set; } = new Color(1f, 0.75f, 0.2f, 1f);
		[Export] public float TracerThickness { get; set; } = 3.5f;
		[Export] public float TracerDuration { get; set; } = 0.18f;
		[Export] public float MuzzleFlashRadius { get; set; } = 18f;
		[Export] public float MuzzleFlashDuration { get; set; } = 0.1f;

		protected override void OnFireVisuals(IEntity target)
		{
			// 枪口世界坐标/命中点计算属于视觉层，整体延迟到主线程
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				OnFireVisualsMain(target);
			});
		}

		private void OnFireVisualsMain(IEntity target)
		{
			// 目标可能在闭包执行前死亡释放
			if (target == null || !GodotObject.IsInstanceValid(target as GodotObject))
				return;
			if (ShootSound != null)
			{
				ShootSound.PitchScale = (float)GD.RandRange(0.95f, 1.05f);
				ShootSound.Play();
			}
			Vector3 muzzleWorld = GetFirePointWorld();
			Vector3 surfacePoint = GetTargetSurfacePoint(muzzleWorld, target);
			SpawnAttackVisuals(muzzleWorld, surfacePoint);
		}

		protected override void OnFireGroundVisuals(Vector3 groundPoint)
		{
			Vector3 gp = groundPoint;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				OnFireGroundVisualsMain(gp);
			});
		}

		private void OnFireGroundVisualsMain(Vector3 groundPoint)
		{
			if (!GodotObject.IsInstanceValid(this))
				return;
			if (ShootSound != null)
			{
				ShootSound.PitchScale = (float)GD.RandRange(0.95f, 1.05f);
				ShootSound.Play();
			}
			Vector3 muzzleWorld = GetFirePointWorld();
			SpawnAttackVisuals(muzzleWorld, groundPoint);
		}

		private void SpawnAttackVisuals(Vector3 muzzleWorld, Vector3 targetPoint)
		{
			// 视觉副作用延迟到主线程执行（模拟线程禁止碰 Godot）
			Vector3 m = muzzleWorld;
			Vector3 t = targetPoint;
			RTS.Core.SimEventQueue.EnqueueMain(() => SpawnAttackVisualsMain(m, t));
		}

		private void SpawnAttackVisualsMain(Vector3 muzzleWorld, Vector3 targetPoint)
		{
			if (ShootSound == null) RTS.Core.GameAudio.Instance?.PlayWeapon(this, muzzleWorld);
			// 电磁炮普通攻击：亮蓝曲线光束 + 命中爆闪（每条线一次调用）
			// 用 WeaponName 判断：多把同名武器被 Godot 自动改名（@Node3D@xxx），Name 不可靠
			if (WeaponName == "WandererPlasmaBeam")
			{
				var beamColor = new Color(0.25f, 0.75f, 1f);
				RTS.Core.HitFx.Spawn(GetTree().Root, targetPoint, HitFxRadius, HitFxDuration, beamColor, 0f);
				RTS.Units.CurvedBeamFX.Spawn(GetTree().Root, muzzleWorld, targetPoint, beamColor, 0.45f);
				return;
			}

			// 扇形喷吐：只画扇形闪光 + 枪口闪光
			if (ConeAngleDegrees > 0f)
			{
				RTS.Core.HitFx.SpawnConeSweep(GetTree().Root, muzzleWorld, targetPoint, AttackRange, ConeAngleDegrees, 0.25f, HitFxColor);
				RTS.Core.HitFx.SpawnMuzzleFlash(GetTree().Root, muzzleWorld, MuzzleFlashRadius, MuzzleFlashDuration, TracerColor);
				return;
			}

			// 近战：刀光弧线而不是弹道
			if (RangeType == WeaponRangeType.Melee)
			{
				// 刀光从模型前缘起笔、长度覆盖到目标，避免大体积单位（巨蝎 2×2）把特效埋在身体里
				Vector3 from = muzzleWorld;
				Vector3 to = targetPoint;
				Vector3 flatDir = to - from;
				flatDir.Y = 0f;
				float dist = flatDir.Length();
				if (dist < 1f)
					flatDir = Vector3.Forward;
				else
					flatDir /= dist;
				float ownerReach = _owner?.LogicEntity?.Radius != null
					? (float)_owner.LogicEntity.Radius
					: 0f;
				Vector3 slashStart = from + flatDir * Mathf.Min(ownerReach, Mathf.Max(0f, dist * 0.5f));
				float slashLen = Mathf.Max(96f, dist * 1.15f);
				RTS.Core.HitFx.SpawnConeSweep(
					GetTree().Root,
					slashStart,
					to,
					slashLen,
					55f,
					0.22f,
					new Color(1f, 0.97f, 0.8f),
					true);
				RTS.Core.HitFx.Spawn(GetTree().Root, targetPoint, HitFxRadius, HitFxDuration, HitFxColor, 0f);
				return;
			}

			// 即时武器：弹道光束 + 枪口闪光 + 命中爆闪，保证开火一眼可见
			// 光束类武器：统一专用直线光束特效（点名“光束线”走上面的曲线特效）
			if (DmgType == DamageType.Beam)
			{
				RTS.Core.HitFx.SpawnMuzzleFlash(GetTree().Root, muzzleWorld, MuzzleFlashRadius, MuzzleFlashDuration, TracerColor);
				RTS.Units.BeamFX.Spawn(GetTree().Root, muzzleWorld, targetPoint, TracerColor, TracerDuration, Mathf.Max(3f, TracerThickness));
				RTS.Core.HitFx.Spawn(GetTree().Root, targetPoint, HitFxRadius, HitFxDuration, HitFxColor, HasAreaDamage ? AreaRadius : 0f);
				return;
			}

			RTS.Core.HitFx.SpawnTracer(GetTree().Root, muzzleWorld, targetPoint, TracerThickness, TracerDuration, TracerColor);
			RTS.Core.HitFx.SpawnMuzzleFlash(GetTree().Root, muzzleWorld, MuzzleFlashRadius, MuzzleFlashDuration, TracerColor);
			RTS.Core.HitFx.Spawn(GetTree().Root, targetPoint, HitFxRadius, HitFxDuration, HitFxColor, HasAreaDamage ? AreaRadius : 0f);
		}

		// 弹道终点 = 敌人立方体的表面（射线进入包围盒的最近点），而不是中心/地面
		private Vector3 GetTargetSurfacePoint(Vector3 muzzleWorld, IEntity target)
		{
			Vector3 center;
			Vector3 half;

			if (target.VisualsModule != null && target.VisualsModule.TryGetModelBounds(out center, out half))
			{
				// 使用实际 GLB 模型的世界包围盒
			}
			else
			{
				float size;

				if (target is Structure structure)
					size = structure.GridSize * 64f;
				else
					size = target.DisplayName == "NanoBehemoth" ? 200f : 40f;

				center = new Vector3(target.GlobalPosition.X, size * 0.5f, target.GlobalPosition.Y);
				half = Vector3.One * (size * 0.5f);
			}

			Vector3 bmin = center - half;
			Vector3 bmax = center + half;
			Vector3 dir = center - muzzleWorld;
			float dist = dir.Length();

			if (dist < 1f)
				return center;

			dir /= dist;

			// 射线-包围盒 slab 求交：取进入点（靠近枪口的那一面）
			float tmin = 0f;
			float tmax = float.MaxValue;

			for (int axis = 0; axis < 3; axis++)
			{
				float o = axis == 0 ? muzzleWorld.X : axis == 1 ? muzzleWorld.Y : muzzleWorld.Z;
				float d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
				float mn = axis == 0 ? bmin.X : axis == 1 ? bmin.Y : bmin.Z;
				float mx = axis == 0 ? bmax.X : axis == 1 ? bmax.Y : bmax.Z;

				if (Mathf.Abs(d) < 0.0001f)
				{
					if (o < mn || o > mx)
						return center;

					continue;
				}

				float t1 = (mn - o) / d;
				float t2 = (mx - o) / d;

				if (t1 > t2)
				{
					float tmp = t1;
					t1 = t2;
					t2 = tmp;
				}

				tmin = Mathf.Max(tmin, t1);
				tmax = Mathf.Min(tmax, t2);

				if (tmin > tmax)
					return center;
			}

			if (tmin <= 0f || tmin > dist)
				return center;

			return muzzleWorld + dir * tmin;
		}
	}
}
