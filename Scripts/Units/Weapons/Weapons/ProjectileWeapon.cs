// File: res://Scripts/Units/Weapons/Weapons/ProjectileWeapon.cs
using Godot;
using RTS.Data;
using RTS.Core;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	[GlobalClass]
	public partial class ProjectileWeapon : Weapon
	{
		[ExportGroup("Projectile Settings")]
		[Export] public PackedScene ProjectilePrefab;
		[Export] public AudioStreamPlayer2D ShootSound;
		[Export] public float HitFxRadius { get; set; } = 36f;
		[Export] public float HitFxDuration { get; set; } = 0.3f;
		[Export] public Color HitFxColor { get; set; } = new Color(1f, 0.75f, 0.2f, 1f);

		public override void Fire(IEntity target)
		{
			if (!CanFire()) return;
			if (!CanFireWithDeployAndAmmo()) return;

			CurrentCooldown = (FP)Cooldown;
			OnFireVisuals(target);
			SpawnProjectile(target);

			if (target.TeamID == Main.Instance.LocalPlayerID) RevealAttacker();
		}

		// 对地面点射击（清菌毯）：生成真正的延迟弹道，落地后统一结算溅射与菌毯
		public override bool FireAtGround(RTS.Simulation.FPVector2 groundPos)
		{
			if (!CanFire())
				return false;
			if (!CanFireWithDeployAndAmmo())
				return false;

			CurrentCooldown = (FP)Cooldown;
			SpawnGroundProjectile(groundPos);
			return true;
		}

		private void SpawnGroundProjectile(RTS.Simulation.FPVector2 groundPos)
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetWeapon(
				!string.IsNullOrEmpty(WeaponName) && WeaponName != "Standard Weapon" ? WeaponName : Name);
			if (cfg == null)
				return;

			var simProj = RTS.Simulation.SimProjectileFactory.Create(
				cfg.ToProjectileSpec(),
				_owner?.LogicEntity as RTS.Simulation.SimEntity,
				null,
				groundPos,
				true,
				(FP)Mathf.RoundToInt(Damage),
				(int)DmgType,
				(FP)Mathf.RoundToInt(BonusDamage),
				(int)BonusDamageVsArmorType,
				(FP)Mathf.RoundToInt(AreaRadius),
				AreaEdgeDamagePercent);

			if (simProj == null)
				return;

			RTS.Core.SimManager.Instance.World.AddProjectile(simProj);

			// 视觉弹体延迟到主线程创建（模拟线程禁止碰 Godot 节点）
			var visualCfg = cfg;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				var projectile = new HomingProjectile
				{
					Name = "GroundShot",
					Speed = visualCfg.ProjectileSpeed,
					HitRadius = visualCfg.HitRadius,
					TopLevel = true
				};
				Vector3 visualSpawnPos = GetFirePointWorld();
				GetTree().Root.AddChild(projectile);
				projectile.GlobalPosition = visualSpawnPos;
				projectile.Setup(simProj, null, Damage, DmgType, HitFxRadius, HitFxDuration, HitFxColor, HasAreaDamage ? AreaRadius : 0f);
			});
		}

		// 对地齐射：每次真正生成一发弹体
		public override void FireGroundVolley(RTS.Simulation.FPVector2 groundPos, bool consumeCooldown)
		{
			if (consumeCooldown)
			CurrentCooldown = (FP)Cooldown;

			SpawnGroundProjectile(groundPos);
		}

		protected override void OnFireVisuals(IEntity target)
		{
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				if (ShootSound != null)
				{
					ShootSound.PitchScale = (float)GD.RandRange(0.95f, 1.05f);
					ShootSound.Play();
				}
			});
		}

		protected override void OnFireGroundVisuals(Vector3 groundPoint)
		{
			Vector3 gp = groundPoint;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				if (ShootSound != null)
				{
					ShootSound.PitchScale = (float)GD.RandRange(0.95f, 1.05f);
					ShootSound.Play();
				}

				Vector3 muzzleWorld = GetFirePointWorld();
				RTS.Core.HitFx.SpawnMuzzleFlash(GetTree().Root, muzzleWorld, 18f, 0.1f, HitFxColor);
				RTS.Core.HitFx.SpawnTracer(GetTree().Root, muzzleWorld, gp, 3f, 0.18f, HitFxColor);
				RTS.Core.HitFx.Spawn(GetTree().Root, gp, HitFxRadius, HitFxDuration, HitFxColor, HasAreaDamage ? AreaRadius : 0f);
			});
		}

		private void SpawnProjectile(IEntity target)
		{
			// 控制台探针 3：检查子弹预制体和逻辑对象
			if (target.LogicEntity == null)
			{
				return;
			}

			var cfg = RTS.Data.Configs.ConfigDatabase.GetWeapon(
				!string.IsNullOrEmpty(WeaponName) && WeaponName != "Standard Weapon" ? WeaponName : Name);
			if (cfg == null)
				return;

			// 命中目标带 BonusDamageTags 中任一 tag 时追加伤害（对空/对工人/对重甲）
			float bonusDamage = HasTagBonus(target) ? BonusDamage : 0f;

			// 1. 逻辑起点：严禁读取开火点（FirePoint）！必须直接使用发射者的纯逻辑原点！
			var simProj = RTS.Simulation.SimProjectileFactory.Create(
				cfg.ToProjectileSpec(),
				_owner?.LogicEntity as RTS.Simulation.SimEntity,
				target.LogicEntity,
				RTS.Simulation.FPVector2.Zero,
				false,
				(FP)Mathf.RoundToInt(Damage),
				(int)DmgType,
				(FP)Mathf.RoundToInt(bonusDamage),
				(int)BonusDamageVsArmorType,
				(FP)Mathf.RoundToInt(AreaRadius),
				AreaEdgeDamagePercent);

			if (simProj == null)
				return;

			RTS.Core.SimManager.Instance.World.AddProjectile(simProj);

			// 2. 视觉起点：延迟到主线程创建，贴图从枪口生成，追赶后台 simPos
			var visualCfg = cfg;
			PackedScene prefab = ProjectilePrefab;
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				HomingProjectile projectile;
				if (prefab != null)
				{
					Node instance = prefab.Instantiate();
					if (instance is not HomingProjectile hp)
					{
						instance?.QueueFree();
						return;
					}
					projectile = hp;
				}
				else
				{
					projectile = new HomingProjectile();
				}

				projectile.Speed = visualCfg.ProjectileSpeed;
				projectile.HitRadius = visualCfg.HitRadius;
				Vector3 visualSpawnPos = GetFirePointWorld();
				GetTree().Root.AddChild(projectile);
				projectile.TopLevel = true;
				projectile.GlobalPosition = visualSpawnPos;
				projectile.Setup(simProj, target, Damage, DmgType, HitFxRadius, HitFxDuration, HitFxColor, HasAreaDamage ? AreaRadius : 0f);
			});
		}

		private void RevealAttacker()
		{
			if (_owner is Node ownerNode)
			{
				RTS.Core.SimEventQueue.EnqueueMain(() =>
				{
					if (GodotObject.IsInstanceValid(ownerNode))
					{
						ulong revealTime = Time.GetTicksMsec() + 2500;
						ownerNode.SetMeta("RevealUntil", revealTime);
					}
				});
			}
		}
	}
}
