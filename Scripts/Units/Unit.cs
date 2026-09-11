// File: res://Scripts/Units/Unit.cs
using Godot;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Actions;
using RTS.World;
using RTS.Actions.Implementation;

// 引入模拟层与定点数结构
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Units
{
	[GlobalClass]
	// 使用 Node3D，不依赖 Godot 物理引擎
	public partial class Unit : Node3D, IEntity
	{
		[Export] public int TeamID { get; set; } = 0;
		[Export] public string UnitName { get; set; } = "Unit";
		[Export] public Texture2D IconProfile;

		[ExportGroup("Modules")]
		[Export] public UnitActionController Brain;
		[Export] public UnitCombat CombatModule;
		[Export] public UnitLife LifeModule;
		[Export] public UnitVisuals VisualsModule;
		[Export] public UnitHarvest HarvestModule;
		// 行为模块缓存：模拟线程禁止 GetNodeOrNull，_Ready 一次性缓存
		public AutoHarvestBehavior AutoHarvestCache;

		[Export] public float VisionRange { get; set; } = 350.0f;
		[Export] public bool RequiresCarpet { get; set; } = false;
		// 飞行单位悬停高度（世界单位，0 = 地面单位）
		[Export] public float FlyHeight { get; set; } = 0f;
		// 代码工厂已按配置表装好数值/武器时置 true，场景加载的单位走 _Ready 补配置，避免重复挂武器
		public bool FactoryConfigured = false;

		public SimUnit SimUnitData { get; private set; }
		private int _lastVisualSnapTick = -1;
		private Vector3 _lastSnapPos = new Vector3(float.MinValue, float.MinValue, float.MinValue);
		public SimEntity LogicEntity => SimUnitData;

		UnitActionController IEntity.Brain => Brain;
		UnitCombat IEntity.CombatModule => CombatModule;
		UnitLife IEntity.LifeModule => LifeModule;
		UnitVisuals IEntity.VisualsModule => VisualsModule;
		UnitHarvest IEntity.HarvestModule => HarvestModule;
		public bool IsStructure => false;
		public Vector2I GridPosition => MapGrid.Instance?.WorldToGrid(new Vector2(GlobalPosition.X, GlobalPosition.Z)) ?? Vector2I.Zero;
		public string DisplayName => UnitName;
		public Texture2D Icon => IconProfile;
		private FPVector2? _exactLogicStartPos = null;
		private bool _deathVisualStarted = false;
		// 索敌临时列表复用：400 个单位交战时每 Tick 都要索敌，避免每次分配 List
		private static readonly List<SimUnit> _enemyNeighborScratch = new();
		private static readonly List<(FP Dist, int Id, IEntity Entity)> _enemyCandidateScratch = new();

		// IEntity 显式实现：3D 节点的 GlobalPosition 是 Vector3，
		// 接口侧统一返回逻辑平面 XZ（伪 3D 的 Y 只用于渲染高度）
		Vector2 IEntity.GlobalPosition =>
			SimUnitData != null
				? new Vector2((float)SimUnitData.Position.X, (float)SimUnitData.Position.Y)
				: new Vector2(GlobalPosition.X, GlobalPosition.Z);

		// 暴露给生成器的注入接口（必须在 AddChild 前调用）
		public void InjectLogicPosition(FPVector2 logicPos)
		{
			_exactLogicStartPos = logicPos;
			// 入树前使用 Position（无父变换，与 GlobalPosition 等价且不触发告警）
			Position = new Vector3((float)logicPos.X, 0f, (float)logicPos.Y);
		}
		public override void _Ready()
		{
			AddToGroup("entities");
			AddToGroup("units");

			Brain ??= GetNodeOrNull<UnitActionController>("Modules/UnitActionController");
			CombatModule ??= GetNodeOrNull<UnitCombat>("Modules/UnitCombat");
			LifeModule ??= GetNodeOrNull<UnitLife>("Modules/UnitLife");
			VisualsModule ??= GetNodeOrNull<UnitVisuals>("Modules/UnitVisuals");
			HarvestModule ??= GetNodeOrNull<UnitHarvest>("Modules/UnitHarvest");
			AutoHarvestCache = GetNodeOrNull<AutoHarvestBehavior>("AutoHarvest");

			ApplyConfigFromTableIfNeeded();

			// 动作面板完全由配置表驱动：场景/工厂都不再手写动作树
			var workerCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(UnitName);
			if (workerCfg?.IsWorker == true && GetNodeOrNull("HarvesterMiningFX") == null)
				AddChild(new HarvesterMiningFX { Name = "HarvesterMiningFX" });
			if (workerCfg?.CanBuild == true && GetNodeOrNull<WorkerBuildBehavior>("BuildBehavior") == null)
				AddChild(new WorkerBuildBehavior { Name = "BuildBehavior", BuildRangeTiles = workerCfg.BuildRangeTiles });

			RTS.Core.EntityFactory3D.RebuildUnitActions(this);

			Brain?.Initialize(this);
			LifeModule?.Initialize(this);
			CombatModule?.Initialize(this);
			VisualsModule?.Initialize(this, this);
			HarvestModule?.Initialize(this);
			if (LifeModule != null) LifeModule.Died += OnUnitDied;

			// 提取安全坐标：如果是游戏运行中动态生成的兵，必须使用注入的定点数坐标。
			// 保留 GlobalPosition 降级方案，仅为兼容编辑器中手动拖入的测试单位。
			FPVector2 finalStartPos = _exactLogicStartPos ?? new FPVector2((FP)GlobalPosition.X, (FP)GlobalPosition.Y);

			SimUnitData = new SimUnit(
				id: RTS.Core.SimManager.Instance.World.GetNextEntityId(),
				teamId: TeamID,
				startPos: finalStartPos
			);
			SimUnitData.RequiresCarpet = RequiresCarpet;
			SimUnitData.IsAir = FlyHeight > 0f;
			SimUnitData.IsGhost = FlyHeight > 0f;
			SimUnitData.UnitTypeId = UnitName;
			SimUnitData.VisionRange = (FP)VisionRange;

			// 移速从配置表读取（1 格 = 64 世界单位，表里是格/秒）
			var unitCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(UnitName);
			if (unitCfg != null && unitCfg.MoveSpeed > 0)
				SimUnitData.MaxSpeed = (FP)unitCfg.MoveSpeed;
			// 背包容积必须从配置表注入：UnitHarvest 默认只有 10，
			// 不接线的话工人装 10 就回城，全游戏经济掉到 1/5
			if (unitCfg != null && unitCfg.CarryCapacity > 0 && HarvestModule != null)
				HarvestModule.MaxCapacity = unitCfg.CarryCapacity;
			if (unitCfg != null && unitCfg.FootprintTiles > 0f)
				SimUnitData.Radius = (FP)(unitCfg.FootprintTiles * 32f);
			if (unitCfg != null)
				SimUnitData.FootprintTiles = (FP)unitCfg.FootprintTiles;
			if (unitCfg != null)
			{
				SimUnitData.CannotBeHealed = unitCfg.CannotBeHealed;
				SimUnitData.FieldRadius = unitCfg.FieldRadius;
				SimUnitData.IsDemonUnit = unitCfg.Tags.Contains("Demon");
				SimUnitData.IsPlantUnit = unitCfg.Tags.Contains("Plant");
				SimUnitData.HeroMaxEnergy = (FP)unitCfg.HeroEnergyMax;
				SimUnitData.HeroEnergyRegenPerSecond = (FP)unitCfg.HeroEnergyRegenPerSecond;
				SimUnitData.LifespanTimer = (FP)unitCfg.LifespanSeconds;
				SimUnitData.LifespanMax = (FP)unitCfg.LifespanSeconds;
				SimUnitData.MaxAmmo = (FP)unitCfg.MaxAmmo;
				SimUnitData.BaseMaxAmmo = (FP)unitCfg.MaxAmmo;
				SimUnitData.Ammo = (FP)unitCfg.MaxAmmo;
				SimUnitData.DeployMaxHpMultiplier = (FP)unitCfg.DeployedMaxHpMultiplier;
				SimUnitData.DeployRangeBonus = (FP)(unitCfg.DeployedAttackRangeBonusTiles * 64f);
				SimUnitData.DeployAoeRadius = (FP)(unitCfg.DeployedAoeRadiusTiles * 64f);
				SimUnitData.DeployOnlyWeapon = unitCfg.DeployOnlyWeapon;
				SimUnitData.DeployRequiresTargetCircle = unitCfg.DeployRequiresTargetCircle;
				SimUnitData.DeployTargetRadius = (FP)(unitCfg.DeployTargetCircleTiles * 64f);
				SimUnitData.EnergyRegenInAmmoRange = unitCfg.EnergyRegenInAmmoRange;
			}

			RTS.Core.SimManager.Instance.World.AddUnit(SimUnitData);
			RTS.Core.SimManager.Instance.RegisterEntityNode(this);
			Brain?.StartAction("Idle", true);
		}

		// 场景预制体（可编辑）只保留结构，数值全部从配置表读取，保证与代码工厂一致
		private void ApplyConfigFromTableIfNeeded()
		{
			if (FactoryConfigured)
				return;

			var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(UnitName);
			if (cfg == null)
				return;

			// 数值统一走工厂的 ApplyUnitStats（预制体与代码工厂共用一份，避免两处漂移）
			RTS.Core.EntityFactory3D.ApplyUnitStats(this, UnitName);

			var mount = GetNodeOrNull<Node3D>("Visuals/WeaponMount");
			if (mount == null)
				return;

			RTS.Core.EntityFactory3D.EnsureWeaponsOnMount(mount, cfg.WeaponIds);
		}

		// 不使用 _PhysicsProcess；_Process 仅做视觉平滑
		public override void _Process(double delta)
		{
			if (LifeModule?.IsDead ?? false) return;
			if (SimUnitData == null) return;

			// --- 核心：只保留视觉平滑追赶逻辑（3D 位置 = XZ 逻辑平面） ---
			Vector3 targetVisualPos;
			if (RTS.Core.SimManager.Instance != null &&
				RTS.Core.SimManager.Instance.TryGetUnitVisualState(SimUnitData.ID, out float sx, out float sy, out _, out _))
				targetVisualPos = new Vector3(sx, FlyHeight, sy);
			else
				targetVisualPos = new Vector3((float)SimUnitData.Position.X, FlyHeight, (float)SimUnitData.Position.Y);
			if (VisualsModule != null && VisualsModule.IsBatched)
			{
				// 批渲染单位：模型平滑由批渲染器自己做，节点按 20Hz 逻辑位置直接吸附，
				// 只在模拟 tick 更新时写一次 Node3D 变换，避免每帧 2000 次引擎调用
				int curTick = RTS.Network.LockstepManager.Instance?.CurrentTick ?? -1;
				if (curTick != _lastVisualSnapTick)
				{
					_lastVisualSnapTick = curTick;
					if ((_lastSnapPos - targetVisualPos).LengthSquared() > 1f)
					{
						GlobalPosition = targetVisualPos;
						_lastSnapPos = targetVisualPos;
					}
				}
			}
			else
			{
				// 已到位就不再写变换：静止单位不触发子树变换传播
				if ((GlobalPosition - targetVisualPos).LengthSquared() > 0.0001f)
					GlobalPosition = GlobalPosition.Lerp(targetVisualPos, (float)delta * 15.0f);
			}
			VisualsModule?.Tick(delta);
		}

		// --- 核心逻辑：获取可用动作 ---
		public List<EntityAction> GetAvailableActions()
		{
			var list = new List<EntityAction>();
			if (IsDeadOrNull() || Brain == null) return list;

			foreach (Node child in Brain.GetChildren())
			{
				if (child is not UnitAction ua) continue;

				var act = RTS.Data.ActionViewFactory.Build(ua);

				if (ua is BuildAction ba)
				{
					act.DisplayName = RTS.Settings.Localization.TrName(ba.StructureName);
					act.Tooltip = ba.GetRequirementTooltip();
				}

				if (act.SlotIndex >= 0) list.Add(act);

				// 多足控制范围：出范围的单位按钮全部置灰（仍可选中看数据）
				if (!RTS.Core.WorldScanner.IsOperable(this, RTS.Core.Main.Instance?.LocalPlayerID ?? 0))
					act.IsEnabled = false;
			}

			// 自杀：摧毁自身（不走普通指令队列，由 SimManager 确定性处理）
			list.Add(new EntityAction
			{
				ActionId = "SelfDestruct",
				DisplayName = RTS.Settings.Localization.Tr("action.SelfDestruct"),
				SlotIndex = 13,
				Tooltip = RTS.Settings.Localization.Tr("action.SelfDestructTip"),
				IsEnabled = RTS.Core.WorldScanner.IsOperable(this, RTS.Core.Main.Instance?.LocalPlayerID ?? 0)
			});

			return list;
		}

		// 找最近的前 N 个不同敌人（多目标攻击用，确定性排序）
		public List<IEntity> FindClosestEnemies(float radius, System.Func<IEntity, bool> filter, int count)
		{
			var result = new List<IEntity>();
			if (count <= 0)
				return result;

			var world = RTS.Core.SimManager.Instance?.World;
			if (world == null)
				return result;

			FP radiusSqFP = (FP)radius * (FP)radius;
			_enemyCandidateScratch.Clear();
			world.SpatialGrid.QueryNeighbors(SimUnitData.Position, (FP)radius, _enemyNeighborScratch);

			foreach (var simUnit in _enemyNeighborScratch)
			{
				if (simUnit.ID == SimUnitData.ID || simUnit.TeamID == TeamID || simUnit.TeamID <= 0 || simUnit.IsDead)
					continue;

				FP distSq = FPVector2.DistanceSquared(SimUnitData.Position, simUnit.Position);
				if (distSq > radiusSqFP)
					continue;

				var entity = RTS.Core.SimManager.Instance.FindEntityById(simUnit.ID);
				if (entity == null || (filter != null && !filter(entity)))
					continue;

				_enemyCandidateScratch.Add((distSq, simUnit.ID, entity));
			}

			_enemyCandidateScratch.Sort((a, b) =>
				a.Dist == b.Dist ? a.Id.CompareTo(b.Id) : (a.Dist < b.Dist ? -1 : 1));

			foreach (var c in _enemyCandidateScratch)
			{
				if (result.Count >= count)
					break;
				result.Add(c.Entity);
			}

			return result;
		}

		// 多段齐射（电浆炮光束线 10×15）：一轮固定打满 N 发，优先不同目标，不足循环补打
		public void PerformMultiShotVolley(IEntity primaryTarget)
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(UnitName);
			if (cfg == null || CombatModule == null)
				return;

			if (cfg.MultiShotTargets <= 1)
			{
				CombatModule.TryAttack(primaryTarget);
				return;
			}

			float range = CombatModule.GetAttackRange();
			var enemies = FindClosestEnemies(Mathf.Max(300f, range), CombatModule.CanAnyWeaponTarget, cfg.MultiShotTargets);

			if (primaryTarget != null && !enemies.Contains(primaryTarget))
				enemies.Insert(0, primaryTarget);

			if (enemies.Count == 0)
				return;

			var weapons = CombatModule.Weapons;
			// 齐射必须等冷却：主武器没好就跳过本轮，否则 Idle 每帧驱动会无限开火
			if (weapons.Count == 0 || !weapons[0].CanFire())
				return;

			for (int i = 0; i < cfg.MultiShotTargets; i++)
			{
				var target = enemies[i % enemies.Count];
				var weapon = weapons.Count > 0 ? weapons[i % weapons.Count] : null;

				if (weapon != null && weapon.CanHit(target))
					weapon.FireVolley(target, i == 0);
				else
					CombatModule.TryVolley(target, i == 0);
			}
		}

		// --- 公开方法 ---

		public void SetSelected(bool selected) => VisualsModule?.SetSelected(selected);

		public bool IsDeadOrNull() => !GodotObject.IsInstanceValid(this) || (LifeModule?.IsDead ?? false);

		public List<Vector2> GetWaypointPositions() => Brain?.GetPreviewPathPoints() ?? new List<Vector2>();

		private void OnUnitDied()
		{
			StartDeathVisual();
		}

		// 强制触发死亡动画（献祭/自爆等直接标记死亡时也要有尸体下沉效果）
		public void StartDeathVisual()
		{
			if (_deathVisualStarted)
				return;

			_deathVisualStarted = true;

			// 逻辑死亡立即标记（纯模拟数据，模拟线程安全），SimWorld 停止计算
			if (SimUnitData != null)
			{
				SimUnitData.IsDead = true;
				RTS.Core.SimManager.Instance?.UnregisterEntityNode(SimUnitData.ID);
			}

			// 死亡动画统一走 DeathVisuals（与建筑共用同一实现）
			RTS.Core.DeathVisuals.PlaySinkAndFree(this, FlyHeight, () =>
			{
				if (!GodotObject.IsInstanceValid(this))
					return;
				Brain?.StopAll();
				VisualsModule?.OnUnitDied();
				if (GetNodeOrNull<CollisionShape2D>("CollisionShape2D") is { } col)
					col.SetDeferred(CollisionShape2D.PropertyName.Disabled, true);
			});
		}
	}
}
