// File: res://Scripts/Simulation/SimUnit.cs
using System.Collections.Generic;
using FixMath.NET;
using FP = FixMath.NET.Fix64;

namespace RTS.Simulation
{
	public class SimUnit : SimEntity
	{
		public FPVector2 Velocity;
		public FP MaxSpeed = (FP)200;
		public FP SeekWeight = (FP)1;
		public FP SeparationWeight = (FP)1;

		public FPVector2 TargetPosition;
		public FPVector2 FinalTargetPosition;
		public bool HasTarget;

		// 幽灵模式：忽略单位与建筑碰撞
		public bool IsGhost = false;

		// 空中单位：寻路无视墙体/建筑，且不参与地形碰撞（飞行翻墙）
		public bool IsAir = false;
		// 单位类型 ID（供人口/效果查询，确定性数据）
		public string UnitTypeId = "";

		// 只能在纳米地毯上移动（纳米巨兽）
		public bool RequiresCarpet = false;

		// 机动科技：自身半径内持续生成地毯 / 地毯上移速加成
		public int SelfCreepRadius = 0;
		public FP SpeedOnCreepMultiplier = FP.One;
		// 占地面积（格）：电浆炮等“按长宽² 结算”的命中行为使用
		public FP FootprintTiles = FP.One;

		// 先进采集器：纳米巨兽可当采集器使用（采集逻辑后续接入）
		public bool BehemothCanHarvest = false;

		// 恶魔：单位自带立场半径（格）
		public int FieldRadius = 0;
		// 恶魔单位标记（立场离场惩罚等）
		public bool IsDemonUnit = false;
		// 植物单位标记（菌毯回血/防御）
		public bool IsPlantUnit = false;

		// 立场上移速倍率（急行军科技）
		public FP FieldSpeedMultiplier = FP.One;

		// 怨灵绑定地狱城 ID（用于 12 只上限）
		public int BoundStructureId = -1;

		// 英雄能量
		public FP HeroEnergy = FP.Zero;
		public FP HeroMaxEnergy = FP.Zero;
		public FP HeroEnergyRegenPerSecond = FP.Zero;

		// 死亡一次性处理标记（熔岩旗手自爆/留旗）
		public bool DeathProcessed = false;

		// 洞穴沙虫：火车式五节躯体（头 + 3 中段 + 尾，各为独立 SimUnit）
		// SegmentGroupId = 头部实体 ID（整条编组）；SegmentLeaderId = 前一节 ID（头 = -1）
		public int SegmentGroupId = -1;
		public int SegmentLeaderId = -1;
		// 节段：忽略外部移动指令，只由跟随 tick 驱动
		public bool IsSegmentBody = false;
		public bool IsBurrowed = false;
		public int DevourTargetId = -1;
		public FP DevourTimer = FP.Zero;

		public bool IsSandwormBody =>
			UnitTypeId == "CaveSandworm" ||
			UnitTypeId == "CaveSandwormMid" ||
			UnitTypeId == "CaveSandwormTail";

		// 冲锋冲刺：目标点与剩余时间
		public FPVector2 DashTarget;
		public FP DashTimer = FP.Zero;
		public FP DashDamage = FP.Zero;
		public readonly HashSet<int> DashHitIds = new();

		// 多足：无需控制 / 等离子炮 / 腾跃状态
		public bool NoControlNeeded = false;
		public bool PlasmaAutoFire = true;
		public FP PlasmaCooldown = FP.Zero;
		public int PlasmaCastState = 0; // 0=空闲 1=前摇 2=后摇
		public FPVector2 PlasmaTarget;
		public FP PlasmaCastTimer = FP.Zero;
		public FP LeapCooldown = FP.Zero;
		// 多足：建造者是否正在施工（单线程：施工/移动时不能自动采矿）
		public bool IsConstructing = false;
		// 电浆炮自动锁定目标实体 ID（-1 = 手动固定点）
		public int PlasmaTargetEntityId = -1;

		// 主动技能冷却（确定性、纳入哈希）
		public FP SkillCooldown = FP.Zero;

		// =========================================================
		// P0-1 战斗状态镜像：节点逻辑的确定性快照，先纳入哈希，
		// 后续把冷却/索敌本体彻底搬进 SimUnit
		// =========================================================
		public int CombatTargetId = -1;
		public FP ActiveWeaponCooldown = FP.Zero;
		public FP WeaponRotationCooldown = FP.Zero;
		// P0-1 里程碑 3：当前行为状态（动作名 + 目标实体），由动作控制器镜像，纳入哈希
		public string ActiveActionName = "";
		// 每把武器的冷却本体（与 UnitCombat.Weapons 顺序一致；P0-1 里程碑 2）
		public List<FP> WeaponCooldowns = new();

		public FP GetWeaponCooldown(int index)
		{
			if (index < 0 || index >= WeaponCooldowns.Count)
				return FP.Zero;
			return WeaponCooldowns[index];
		}

		public void SetWeaponCooldown(int index, FP value)
		{
			while (WeaponCooldowns.Count <= index)
				WeaponCooldowns.Add(FP.Zero);
			WeaponCooldowns[index] = value;
		}

		// =========================================================
		// 泰伦：弹药 / 架设
		// =========================================================
		public FP MaxAmmo = FP.Zero;
		public FP Ammo = FP.Zero;
		public FP BaseMaxAmmo = FP.Zero;

		// 0=未架设 1=架设中 2=已架设 3=收起中
		public int DeployState = 0;
		public FP DeployTimer = FP.Zero;
		public FP DeployMaxHpMultiplier = FP.One;
		public FP DeployedHpBonus = FP.Zero;
		public FP DeployRangeBonus = FP.Zero;
		public FP DeployAoeRadius = FP.Zero;
		public bool DeployOnlyWeapon = false;
		public bool DeployRequiresTargetCircle = false;
		public FPVector2 DeployTargetCenter;
		public FP DeployTargetRadius = FP.Zero;
		// 已架设时收到移动指令：收起后自动补执行
		public bool PendingUndeployMove = false;
		public FPVector2 PendingMoveTarget;

		// 泰伦科技旗标
		public bool HasKnockbackAttacks = false;
		public FP KnockbackTiles = FP.Zero;
		public bool AmmoRefundOnKill = false;
		public FP AoeRadiusBonus = FP.Zero;
		public bool CanHitStructuresOverride = false;
		public FP BonusDamageVsStructure = FP.Zero;
		public FP DeployDamageReduction = FP.Zero;
		public FP DeployTimeMultiplier = FP.One;
		public bool EnergyRegenInAmmoRange = false;

		// 恶魔存续时间：剩余寿命（秒），0 = 无寿命限制；LifespanMax 为出生时上限
		public FP LifespanTimer = FP.Zero;
		public FP LifespanMax = FP.Zero;

		public bool IsDeployed => DeployState == 2;
		public bool IsDeployBusy => DeployState == 1 || DeployState == 3;

		public List<FPVector2> Path = new();
		public int CurrentWaypointIndex = 0;
		// 寻路预算挂起：已接令但尚未分到寻路预算，原地待命，随后按 ID 顺序补算
		public bool PathPending = false;
		// 短路径重试冷却（tick）：路径被截断未达终点时，限制从当前位置重算的频率，
		// 避免对不可达目标每帧反复全量 A*（重建风暴/预算饥饿）
		public int ShortPathRetryTicks = 0;
		// 建路径时离目标的距离（平方）：截断后用它判断“是否还在推进”，
		// 有进展立即续算（长距离行军连续），原地卡住才冷却重试
		public FP PathBuildDistSq = FP.MaxValue;

		// 正在为之让路的蓝图 ID（0 = 无）。
		// 自己人站在蓝图里时会被强制插队一个"移出"指令（帝国时代 4 式）；
		// 该值非 0 期间不重复打断，离开蓝图后由 SimManager 对账清零。
		public int EvictStructureId = 0;

		/// <summary>
		/// 撤销当前移动意图（蓝图让路结束后清理残留的"移出"目标）。
		/// 只清移动态，不动动作层——动作层会在自己的 OnUpdate 里重新下指令。
		/// </summary>
		public void CancelMoveIntent()
		{
			HasTarget = false;
			Velocity = FPVector2.Zero;
			PathPending = false;
			Path?.Clear();
			Path = null;
			CurrentWaypointIndex = 0;
			ShortPathRetryTicks = 0;
			PathBuildDistSq = FP.MaxValue;
		}

		// 调试日志过滤：工人单位的路径指令（采矿/施工往返）噪音大，不打印
		private bool IsWorkerPathLogNoise =>
			UnitTypeId == "SCV" || UnitTypeId == "Builder" || UnitTypeId == "Engineer" ||
			UnitTypeId == "DemonWorker" || UnitTypeId == "CaveWorker";

		public SimUnit(int id, int teamId, FPVector2 startPos) : base(id, teamId, startPos)
		{
			Radius = (FP)15;
		}
		public override string GetDebugState()
		{
			string targetX = HasTarget ? ((long)(TargetPosition.X * (FP)1000m)).ToString() : "0";
			return base.GetDebugState() + $"|Vel:({(long)(Velocity.X * (FP)1000m)},{(long)(Velocity.Y * (FP)1000m)})|HasTarget:{HasTarget}|TargetX:{targetX}|Cargo:{(long)(CargoAmount * (FP)10m)}";
		}
		// 纯逻辑入口：由外部显式传入 SimWorld，保证核心层可脱离 Godot 独立测试
		public void CommandMove(SimWorld world, FPVector2 target, int ignoreStructureId = -1)
		{
			if (world == null) throw new System.ArgumentNullException(nameof(world));
			lock (world.SyncRoot)
				CommandMoveInner(world, target, ignoreStructureId);
		}

		private void CommandMoveInner(SimWorld world, FPVector2 target, int ignoreStructureId = -1)
		{
			// 沙虫节段：身体不响应任何移动指令，只跟随前一节
			if (IsSegmentBody)
				return;

			// 已架设/架设中：先收起，收起完成后自动补执行该移动
			if (IsDeployed || IsDeployBusy)
			{
#if DEBUG
				world.DiagnosticSink?.Invoke($"[MoveDebug] CommandMove被架设拦截 id={ID} {UnitTypeId} deploy={DeployState}");
#endif
				if (!PendingUndeployMove)
				{
					PendingUndeployMove = true;
					PendingMoveTarget = target;
					HasTarget = false;
					if (DeployState == 2)
					{
						DeployState = 3;
						DeployTimer = (FP)1m * DeployTimeMultiplier;
					}
				}
				// 已在收起流程：重复指令不再清 HasTarget/重触发（否则活动 MoveAction 提前结束→走一步停住）
				Velocity = FPVector2.Zero;
				return;
			}

			// 同目标且路径已走完：只有真正到达终点（1.5 格内）才算完成。
			// 未到达时按“是否还在推进”决定：
			// - 有进展（比建路径时更接近目标）→ 立即从当前位置续算，保证长距离行军连续；
			// - 原地没动（目标不可达/被堵死）→ 冷却后再试，避免每帧空转 A*。
			bool sameTarget = Path != null && Path.Count > 0 &&
				FPVector2.DistanceSquared(FinalTargetPosition, target) <= (FP)1024m;
			if (sameTarget && CurrentWaypointIndex >= Path.Count)
			{
				bool arrived = FPVector2.DistanceSquared(Position, FinalTargetPosition) <= (FP)(96 * 96);
				if (arrived)
				{
					HasTarget = false;
					Velocity = FPVector2.Zero;
					return;
				}
				FP nowDistSq = FPVector2.DistanceSquared(Position, FinalTargetPosition);
				bool progressed = nowDistSq + (FP)(64 * 64) < PathBuildDistSq;
				if (!progressed)
				{
					// 原地卡住：停止并冷却，稍后再试（目标可能被清障）
					if (ShortPathRetryTicks > 0)
					{
						HasTarget = false;
						Velocity = FPVector2.Zero;
						return;
					}
					// 冷却结束必须实际重算；只重置计时并返回会永久卡在这里。
					ShortPathRetryTicks = 30; // 限制下一次无进展重试，20Hz 下为 1.5 秒
				}
				// 有进展或冷却已结束：清理旧路径，走同一个预算入口。
				Path?.Clear();
				Path = null;
			}

			HasTarget = true;

			if (Path == null || Path.Count == 0 || !sameTarget)
			{
				FinalTargetPosition = target;

				// 直线可达：直接给两点路径，不占寻路预算。
				// 开阔地形下 1000 个单位同帧下令也能全部立刻移动，预算只留给真正需要 A* 的绕行。
				if (ignoreStructureId < 0)
				{
					FP losHalf = Radius > FP.Zero ? Radius / (FP)world.Grid.TileSize - (FP)0.5m : FP.Zero;
					int losRadius = losHalf > FP.Zero ? (int)FP.Ceiling(losHalf) : 0;
					if (world.Grid.HasLineOfSight(Position, target, world.Structures, ignoreStructureId, losRadius, TeamID))
					{
#if DEBUG
						if (!IsWorkerPathLogNoise) world.DiagnosticSink?.Invoke($"[Path] {UnitTypeId} id={ID} t={TeamID} 直线直达 目标=({(long)(target.X * (FP)1000m)},{(long)(target.Y * (FP)1000m)}) 半径={losRadius}");
#endif
						PathPending = false;
						Path = new List<FPVector2> { Position, target };
						PathBuildDistSq = FPVector2.DistanceSquared(Position, target);
						CurrentWaypointIndex = 0;
						TargetPosition = Path[0];
						if (MaxSpeed == FP.Zero) MaxSpeed = (FP)200;
						return;
					}
				}

				// 采集/建造等带 ignoreId 的寻路量小且不能等，直接走（不占预算）；
				// 普通移动指令在爆发时按预算分摊，避免一帧算 100 条路径卡死
				if (ignoreStructureId < 0 && !world.TryReservePathBudget())
				{
#if DEBUG
					if (!IsWorkerPathLogNoise) world.DiagnosticSink?.Invoke($"[Path] {UnitTypeId} id={ID} t={TeamID} 预算挂起 目标=({(long)(target.X * (FP)1000m)},{(long)(target.Y * (FP)1000m)})");
#endif
					PathPending = true;
					Path?.Clear();
					Path = null;
					TargetPosition = Position;

					if (MaxSpeed == FP.Zero) MaxSpeed = (FP)200;
					return;
				}

				PathPending = false;
				PathBuildDistSq = FPVector2.DistanceSquared(Position, FinalTargetPosition);
				Path = SimPathfinder.FindPath(world.Grid, world.Structures, Position, target, ignoreStructureId, IsAir, Radius, skipInitialLos: true, radiusTilesOverride: -1, teamId: TeamID);
				// 半径膨胀下 A* 可能退化（大型单位在窄通道/墙角找不到 3×3 净空 → 单点路径 → 原地卡死）：
				// 退化为点单位寻路兜底，让单位至少能朝目标移动
				if (Path == null || Path.Count <= 1)
				{
#if DEBUG
					if (!IsWorkerPathLogNoise) world.DiagnosticSink?.Invoke($"[Path] {UnitTypeId} id={ID} t={TeamID} A*半径寻路退化→点寻路 目标=({(long)(target.X * (FP)1000m)},{(long)(target.Y * (FP)1000m)})");
#endif
					Path = SimPathfinder.FindPath(world.Grid, world.Structures, Position, target, ignoreStructureId, IsAir, FP.Zero, skipInitialLos: true, radiusTilesOverride: -1, teamId: TeamID);
				}
				CurrentWaypointIndex = 0;
#if DEBUG
				if (!IsWorkerPathLogNoise) world.DiagnosticSink?.Invoke($"[Path] {UnitTypeId} id={ID} t={TeamID} 重建路径 path={Path?.Count ?? 0} 目标=({(long)(target.X * (FP)1000m)},{(long)(target.Y * (FP)1000m)})");
				// 过短路径检测：路径终点离目标超过 1.5 格 = 寻路被截断（目标不可达/半径过大），
				// 单位会走到路径尽头就停住（走一步停住的直接来源）
				if (Path != null && Path.Count > 0)
				{
					FPVector2 lastWp = Path[Path.Count - 1];
					if (FPVector2.DistanceSquared(lastWp, target) > (FP)9216m)
						if (!IsWorkerPathLogNoise) world.DiagnosticSink?.Invoke($"[Path] {UnitTypeId} id={ID} t={TeamID} 短路径 path={Path.Count} 终点=({(long)(lastWp.X * (FP)1000m)},{(long)(lastWp.Y * (FP)1000m)}) 距目标={FP.Sqrt(FPVector2.DistanceSquared(lastWp, target)) / (FP)64m:F1}格");
				}
#endif

				if (Path.Count > 0) TargetPosition = Path[0];
				else TargetPosition = target;
			}

			if (MaxSpeed == FP.Zero) MaxSpeed = (FP)200;
		}

		// 由 SimWorld 在后续 Tick 补算挂起的路径（预算内，按 ID 顺序）
		public void CompletePendingPath()
		{
			if (!PathPending || World == null)
				return;

			PathPending = false;
			PathBuildDistSq = FPVector2.DistanceSquared(Position, FinalTargetPosition);
			Path = SimPathfinder.FindPath(World.Grid, World.Structures, Position, FinalTargetPosition, -1, IsAir, Radius, skipInitialLos: false, radiusTilesOverride: -1, teamId: TeamID);
			CurrentWaypointIndex = 0;

			if (Path.Count > 0) TargetPosition = Path[0];
			else TargetPosition = FinalTargetPosition;
		}

		// Convenience entry point uses the owning world instead of a global singleton.
		public void CommandMove(FPVector2 target, int ignoreStructureId = -1)
		{
			CommandMove(World ?? throw new System.InvalidOperationException("Register the unit with a world before moving it."), target, ignoreStructureId);
		}

		public override void LogicTick(FP delta)
		{
			if (IsDead) return;
			Position += Velocity * delta;
		}
		public override long GetStateHash()
		{
			long hash = base.GetStateHash();
			hash = MixCoord(hash, (long)(Velocity.X * (FP)1000m), (long)(Velocity.Y * (FP)1000m));
			hash ^= (long)(LifespanTimer * (FP)1000m);
			hash ^= IsGhost ? 1 : 0;
			hash ^= IsPlantUnit ? 1 : 0;
			hash ^= IsAir ? 1 : 0;
			foreach (char c in UnitTypeId ?? "")
			{
				hash ^= c;
				hash *= 1099511628211L;
			}
			hash ^= RequiresCarpet ? 1 : 0;
			hash ^= SelfCreepRadius;
			hash ^= (long)(SpeedOnCreepMultiplier * (FP)1000m);
			hash ^= (long)(FootprintTiles * (FP)1000m);
			hash ^= BehemothCanHarvest ? 1 : 0;
			hash ^= FieldRadius;
			hash ^= IsDemonUnit ? 1 : 0;
			hash ^= (long)(FieldSpeedMultiplier * (FP)1000m);
			hash ^= BoundStructureId;
			hash ^= (long)(HeroEnergy * (FP)100m);
			hash ^= (long)(HeroMaxEnergy * (FP)100m);
			hash ^= (long)(HeroEnergyRegenPerSecond * (FP)1000m);
			hash ^= DeathProcessed ? 1 : 0;
			hash ^= SegmentGroupId;
			hash ^= SegmentLeaderId;
			hash ^= IsSegmentBody ? 1 : 0;
			hash ^= IsBurrowed ? 1 : 0;
			hash ^= DevourTargetId;
			hash ^= (long)(DevourTimer * (FP)1000m);
			hash = MixCoord(hash, (long)(DashTarget.X * (FP)1000m), (long)(DashTarget.Y * (FP)1000m));
			hash ^= (long)(DashTimer * (FP)1000m);
			hash ^= (long)(DashDamage * (FP)100m);
			if (DashHitIds.Count > 0)
			{
				var ids = new List<int>(DashHitIds);
				ids.Sort();
				foreach (int id in ids)
					hash ^= (long)id * 2654435761L;
			}
			hash ^= NoControlNeeded ? 1 : 0;
			hash ^= PlasmaAutoFire ? 1 : 0;
			hash ^= (long)(PlasmaCooldown * (FP)100m);
			hash ^= PlasmaCastState;
			hash = MixCoord(hash, (long)(PlasmaTarget.X * (FP)1000m), (long)(PlasmaTarget.Y * (FP)1000m));
			hash ^= (long)(PlasmaCastTimer * (FP)1000m);
			hash ^= (long)(LeapCooldown * (FP)100m);
			hash ^= IsConstructing ? 1 : 0;
			hash ^= PlasmaTargetEntityId;
			hash ^= (long)(SkillCooldown * (FP)100m);
			hash ^= CombatTargetId;
			hash ^= (long)(ActiveWeaponCooldown * (FP)1000m);
			hash ^= (long)(WeaponRotationCooldown * (FP)1000m);
			foreach (char c in ActiveActionName ?? "")
			{
				hash ^= c;
				hash *= 1099511628211L;
			}
			foreach (var cd in WeaponCooldowns)
			{
				hash ^= (long)(cd * (FP)1000m);
				hash *= 1099511628211L;
			}
			hash ^= (long)(MaxAmmo * (FP)100m);
			hash ^= (long)(Ammo * (FP)100m);
			hash ^= DeployState;
			hash ^= (long)(DeployTimer * (FP)1000m);
			hash ^= (long)(DeployedHpBonus * (FP)100m);
			hash ^= (long)(DeployRangeBonus * (FP)100m);
			hash ^= (long)(DeployAoeRadius * (FP)100m);
			hash ^= DeployOnlyWeapon ? 1 : 0;
			hash ^= DeployRequiresTargetCircle ? 1 : 0;
			hash = MixCoord(hash, (long)(DeployTargetCenter.X * (FP)1000m), (long)(DeployTargetCenter.Y * (FP)1000m));
			hash ^= (long)(DeployTargetRadius * (FP)100m);
			hash ^= PendingUndeployMove ? 1 : 0;
			hash ^= HasKnockbackAttacks ? 1 : 0;
			hash ^= (long)(KnockbackTiles * (FP)100m);
			hash ^= AmmoRefundOnKill ? 1 : 0;
			hash ^= (long)(AoeRadiusBonus * (FP)100m);
			hash ^= CanHitStructuresOverride ? 1 : 0;
			hash ^= (long)(BonusDamageVsStructure * (FP)100m);
			hash ^= (long)(DeployDamageReduction * (FP)100m);
			hash ^= (long)(DeployTimeMultiplier * (FP)1000m);
			hash ^= EnergyRegenInAmmoRange ? 1 : 0;
			hash ^= HasTarget ? 1 : 0;
			hash = MixCoord(hash, (long)(TargetPosition.X * (FP)1000m), (long)(TargetPosition.Y * (FP)1000m));
			hash = MixCoord(hash, (long)(FinalTargetPosition.X * (FP)1000m), (long)(FinalTargetPosition.Y * (FP)1000m));
			hash ^= CurrentWaypointIndex;
			hash ^= PathPending ? 1 : 0;
			hash ^= ShortPathRetryTicks;
			hash ^= (long)(PathBuildDistSq * (FP)1000m);
			return hash;
		}

		// 受 Buff 影响的最终移动速度
		public FP GetEffectiveMaxSpeed()
		{
			if (Buffs != null)
				return MaxSpeed * Buffs.GetMoveSpeedMultiplier();

			return MaxSpeed;
		}
	}
}
