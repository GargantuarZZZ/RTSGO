using Godot;
using System.Collections.Generic;
using System.Linq;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Network;
using RTS.Simulation;
using RTS.World;
using FP = FixMath.NET.Fix64;

namespace RTS.Core
{
// =========================================================
// 分族对战 AI —— 战斗 / 波次 / 扩张 / 种族特化
// =========================================================
public partial class SimManager
{
	private const int BotWaveGatherTicks = 180;  // 集结等待：9 秒
	private const int BotWavePushTicks = 600;    // 一波推进上限：30 秒后重新集结
	private const int BotWaveMinGroup = 3;

	// =========================================================
	// 战斗：军事单位自动索敌（防守）；兵力达标后按波次进攻
	// =========================================================
	private void TickBotCombat(int team, int currentTick)
	{
		var player = RTS.World.Game.GetPlayerByTeam(team);
		var raceCfg = player?.Race != null ? ConfigDatabase.GetRace(player.Race.RaceName) : null;
		string race = raceCfg?.RaceId ?? "";
		var strategy = GetBotStrategy(team);
		if (strategy == null) return;

		// 攻击目标一律用"最近敌人"（单位/建筑），不用质心
		FPVector2 enemyCentroid = GetNearestEnemyPos(team);
		int enemyCount = enemyCentroid.X != FP.Zero || enemyCentroid.Y != FP.Zero ? 1 : 0;

		int armyCount = CountUnits(team, IsMilitaryUnit);
		float enemyPower = GetEnemyArmyPower(team, currentTick);
		float myPower = GetBotArmyPower(team, currentTick);
		int armyTarget = raceCfg?.AiArmyTarget ?? 12;

		bool towerRaidActive = IsTowerRaidActive(team);
		bool focusEnemy = IsBaseUnderAttack(team) ||
			(!towerRaidActive && armyCount >= armyTarget);
		bool shouldAdvance = enemyCount > 0 &&
			!(towerRaidActive && !focusEnemy) &&
			(armyCount >= armyTarget ||
			 IsBaseUnderAttack(team) ||
			 (enemyPower > 0f && myPower >= enemyPower * 1.2f) ||
			 armyCount >= System.Math.Max(3, armyTarget / 3));
		shouldAdvance = strategy.ShouldAdvance(this, team, shouldAdvance);

		var brain = GetBrain(team);
		// 波次主攻目标：
		// 1) 军队附近（15 格内）有敌人 → 目标切到“离军队最近的敌人”，贴脸打前线，
		//    避免两军相遇后还死盯远处旧目标（被截击 → 谁也不推进）；
		// 2) 否则沿用“离老家最近的敌人”作稳定战略目标（朝敌军方向推进），
		//    不追着单个工人/斥候满图跑（那会看起来“谁都不打架”）。
		FPVector2 armyFront = GetBotMilitaryCentroid(team);
		FPVector2 nearestToArmy = GetNearestEnemyPos(team, armyFront);
		bool enemyNearArmy = (nearestToArmy.X != FP.Zero || nearestToArmy.Y != FP.Zero) &&
			FPVector2.DistanceSquared(armyFront, nearestToArmy) <= (FP)(15 * 64) * (FP)(15 * 64);
		bool stickToWave = HasHostileNear(team, brain.WaveTarget) &&
			FPVector2.DistanceSquared(armyFront, brain.WaveTarget) <= (FP)(24 * 64) * (FP)(24 * 64);
		FPVector2 advanceTarget = enemyNearArmy ? nearestToArmy
			: stickToWave ? brain.WaveTarget
			: enemyCentroid;
		advanceTarget = strategy.AdvanceTarget(this, team, advanceTarget);

		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team ||
				!IsMilitaryUnit(simUnit))
				continue;
			var node = FindEntityById(simUnit.ID);
			if (node == null)
				continue;

			if (strategy.HandleUnit(this, team, node, simUnit, shouldAdvance))
			{
				brain.ReservedOrders.Add(simUnit.ID);
				continue;
			}
			if (simUnit.CombatTargetId >= 0) continue;

			// 波次推进中不逐个索敌：AttackMove 自带索敌，逐单位 TryAutoAttack 会每 tick
			// 把正在推进的单位改成攻击最近敌人（走一步停住/原地打架）。基地被摸仍防守。
			if (brain.WaveMode == 2 && !IsBaseUnderAttack(team))
				continue;

			float range = node.CombatModule.GetAttackRange();
			if (RTS.Core.EntityExtensions.TryAutoAttack(node, Mathf.Max(320f, range)))
				continue;
		}

		// 多足的推进完全由编队系统（攻击队）指挥，不走全局波次推进，
		// 否则 BotPushWithWave 会给所有军事单位 attack-move，和攻击队抢指挥
		if (shouldAdvance && enemyCount > 0 && strategy.GlobalWaves)
			BotPushWithWave(team, advanceTarget, race, currentTick, IsBaseUnderAttack(team));
	}

	// 波次目标是否还活着：目标点附近 8 格内仍有敌对单位或建筑
	private bool HasHostileNear(int team, FPVector2 pos)
	{
		if (pos.X == FP.Zero && pos.Y == FP.Zero)
			return false;
		FP rSq = (FP)(8 * 64) * (FP)(8 * 64);
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || !AreTeamsHostile(team, u.TeamID))
				continue;
			if (FPVector2.DistanceSquared(u.Position, pos) <= rSq)
				return true;
		}
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || !AreTeamsHostile(team, s.TeamID))
				continue;
			if (FPVector2.DistanceSquared(s.Position, pos) <= rSq)
				return true;
		}
		return false;
	}

	// 控制圈内朝目标方向的最远点（多足部队不出圈）
	private FPVector2 GetWandererSafeAdvance(int team, FPVector2 target)
	{
		if (!GetTeamControlAnchor(team, out var anchor, out var reach) || reach <= FP.Zero)
			return target;
		FPVector2 dir = (target - anchor).Normalized();
		if (dir.X == FP.Zero && dir.Y == FP.Zero)
			return anchor;
		FP dist = reach - (FP)96;
		if (dist < (FP)64)
			dist = (FP)64;
		return anchor + dir * dist;
	}

	private bool IsBaseUnderAttack(int team)
	{
		FP basePosX = FP.Zero, basePosY = FP.Zero;
		int count = 0;
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID != team)
				continue;
			basePosX += sim.Position.X;
			basePosY += sim.Position.Y;
			count++;
		}
		if (count == 0)
			return false;
		basePosX /= (FP)count;
		basePosY /= (FP)count;
		FP rangeSq = (FP)(12 * World.Grid.TileSize);
		rangeSq *= rangeSq;
		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || !AreTeamsHostile(team, simUnit.TeamID))
				continue;
			FP dx = simUnit.Position.X - basePosX;
			FP dy = simUnit.Position.Y - basePosY;
			if (dx * dx + dy * dy <= rangeSq)
				return true;
		}
		return false;
	}

	// =========================================================
	// 波次推进：集结 → 一起 attack-move，新造兵尾随
	// =========================================================
	// 军队（军事单位）质心：前线集结点与波次目标重定向用
	private FPVector2 GetBotMilitaryCentroid(int team)
	{
		FP x = FP.Zero, y = FP.Zero;
		int n = 0;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team || !IsMilitaryUnit(u))
				continue;
			x += u.Position.X;
			y += u.Position.Y;
			n++;
		}
		if (n == 0)
			return GetTeamMainBasePos(team);
		return new FPVector2(x / (FP)n, y / (FP)n);
	}
	
	private void SendBotRaid(int team, FPVector2 target, string race, int currentTick)
	{
		// 拆塔不走波次集结机器：直接对全军 attack-move 并锁定推进态。
		// 否则每拆一座塔，状态机都会先切“重新集结”→ 全军原地停/往质心走一步，
		// 再等集结完成才去下一座塔（“塔爆瞬间停→走一步停→几秒后才出发”）。
		var brain = GetBrain(team);
		brain.WaveMode = 2;
		brain.WaveTarget = target;
		brain.WaveTick = currentTick;
		brain.WaveRallyFront = false;

		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team ||
				!IsMilitaryUnit(simUnit))
				continue;
			var node = FindEntityById(simUnit.ID);
			if (node == null || simUnit.CombatTargetId >= 0)
				continue;
			// 泰伦架设中/已架设的单位不能被拉走（CommandMove 会触发自动收起）
			if (!CanIssueBotArmyOrder(team, simUnit, target))
				continue;
			RTS.Core.EntityExtensions.CommandAttackMove(node, target);
		}
	}

	private void BotPushWithWave(int team, FPVector2 target, string race, int currentTick, bool urgent)
	{
		var brain = GetBrain(team);
		// 接战判定：任一军事单位正在攻击（有战斗目标）即视为已接战。
		// 接战时集结统计永远凑不齐（兵力满编、已集结=0），继续等集结只会让全军
		// 停在原地“两军对垒谁都不一股脑打上去”——此时直接维持推进/战斗，
		// 不再拉扯回集结点。
		bool inContact = false;
		foreach (var su in World.Units.Values)
		{
			if (su == null || su.IsDead || su.TeamID != team || !IsMilitaryUnit(su))
				continue;
			if (su.CombatTargetId >= 0)
			{
				inContact = true;
				break;
			}
		}

		int mode = brain.WaveMode;
		if (mode == 0)
		{
			mode = 1;
			brain.WaveMode = 1;
			brain.WaveTarget = target;
			brain.WaveTick = currentTick;
			brain.WaveRallyFront = false;
		}
		else if (mode == 1 && (inContact || currentTick - brain.WaveTick >= BotWaveGatherTicks))
		{
			mode = 2;
			brain.WaveMode = 2;
			brain.WaveTick = currentTick;
		}
		else if (mode == 2 && !inContact &&
			currentTick - brain.WaveTick >= BotWavePushTicks &&
			// 目标区域已清空且军队已贴近目标时才重新集结（打完一片收拢队形）。
			// 否则（目标附近仍有敌人/下一座塔，或目标已换到远处）保持推进，
			// 避免“塔爆瞬间全军停住、走一步停一下、过几秒才去下一座塔”。
			!HasHostileNear(team, brain.WaveTarget) &&
			FPVector2.DistanceSquared(GetBotMilitaryCentroid(team), brain.WaveTarget) <= (FP)(16 * 64) * (FP)(16 * 64))
		{
			// 重新集结：在军队当前位置原地整队，不把全军拉回老家/敌人门口。
			mode = 1;
			brain.WaveMode = 1;
			brain.WaveTick = currentTick;
			brain.WaveRallyFront = true;
		}
		// 目标位置变化只更新目标，绝不重置回集结模式：
		// 最近敌人位置动一下就重置，全军会反复掉头往回走
		brain.WaveTarget = target;

		FPVector2 basePos = GetTeamMainBasePos(team);
		if (basePos.X == FP.Zero && basePos.Y == FP.Zero)
			basePos = GetTeamCentroid(team);
		FPVector2 dir = (target - basePos).Normalized();
		if (dir.X == FP.Zero && dir.Y == FP.Zero)
			dir = new FPVector2(FP.One, FP.Zero);
		// 首波集结：老家前 8 格；重新集结（前一波已推到前线）：原地整队——
		// 集结点取军队当前质心，不再放到目标前 6 格（敌人门口），
		// 否则部队被半路敌人截击，“已集结”永远凑不齐，波次反复超时强推。
		FPVector2 rallyPos;
		if (brain.WaveRallyFront)
		{
			rallyPos = GetBotMilitaryCentroid(team);
		}
		else
		{
			rallyPos = basePos + dir * (FP)(8 * 64);
		}

		int totalMilitary = 0;
		int gathered = 0;
		bool towerRaid = IsTowerRaidActive(team);
		var commandTargets = new List<(IEntity Node, SimUnit Sim)>();
		foreach (var simUnit in World.Units.Values)
		{
			if (simUnit == null || simUnit.IsDead || simUnit.TeamID != team ||
				!IsMilitaryUnit(simUnit))
				continue;
			var node = FindEntityById(simUnit.ID);
			if (node == null)
				continue;
			totalMilitary++;
			if (simUnit.CombatTargetId < 0 &&
				FPVector2.IsWithinRange(simUnit.Position, rallyPos, (FP)(6 * 64)))
				gathered++;
			// 拆塔期间多足允许出控制圈（否则永远摸不到塔）
			// 泰伦架设中/已架设的单位不能被波次下移动令：
			// CommandMove 会触发自动收起，架设直接被取消（架/收循环根源）
			if (simUnit.CombatTargetId < 0 &&
				CanIssueBotArmyOrder(team, simUnit, target))
				commandTargets.Add((node, simUnit));
		}

#if DEBUG
		GD.Print($"[AIWave] T{team} mode={mode} 目标=({(long)(target.X * (FP)1000m)},{(long)(target.Y * (FP)1000m)}) 集结=({(long)(rallyPos.X * (FP)1000m)},{(long)(rallyPos.Y * (FP)1000m)}) 兵力={totalMilitary} 已集结={gathered} urgent={urgent} front={brain.WaveRallyFront}");
#endif
		// 集结超时兜底：单位可能因寻路截断/交战永远凑不齐半数，强制转推进，
		// 否则全军一直停在集结点附近“两军对垒谁都不一股脑打上去”
		bool gatherTimedOut = mode == 1 &&
			currentTick - brain.WaveTick > BotWaveGatherTicks * 2;
		if (mode == 1 &&
			(urgent || inContact || gatherTimedOut || gathered >= System.Math.Max(BotWaveMinGroup, totalMilitary / 2)))
		{
			mode = 2;
			brain.WaveMode = 2;
			brain.WaveTick = currentTick;
		}
		foreach (var (node, simUnit) in commandTargets)
		{
			if (!CanIssueBotArmyOrder(team, simUnit, mode == 1 ? rallyPos : target))
				continue;
			if (mode == 1)
				RTS.Core.EntityExtensions.CommandMoveTo(node, rallyPos);
			else
				RTS.Core.EntityExtensions.CommandAttackMove(node, target);
		}
	}

	// =========================================================
	// 扩张：拆中立塔 → 资源簇旁拍分矿
	// =========================================================
	private void TickBotExpansion(int team, RaceConfig raceCfg, int currentTick)
	{
		if (raceCfg.AiExpansionBaseIds.Count == 0)
			return;
		var brain = GetBrain(team);

		// 塔已死（从活塔列表消失）→ 记下分矿位置并清拆塔状态
		if (brain.TowerRaidId >= 0 &&
			(brain.TowerRaidPos.X != FP.Zero || brain.TowerRaidPos.Y != FP.Zero))
		{
			var raidSim = World.FindSimEntity(brain.TowerRaidId) as SimStructure;
			bool raidTargetDead = raidSim == null || raidSim.IsDead ||
				raidSim.Hp <= FP.Zero || raidSim.TeamID != -2;
			if (raidTargetDead)
			{
				brain.TowerRaidId = -1;
				FPVector2 deadCluster = GetResourceClusterCenter(brain.TowerRaidPos);
				if (deadCluster.X == FP.Zero && deadCluster.Y == FP.Zero)
					deadCluster = brain.TowerRaidPos;
				brain.PendingExpansion = deadCluster;
			}
		}

		// 塔已拆但分矿没拍：优先补拍（不提前返回，继续往下找下一座塔派兵，
		// 否则拆塔后全军会停 1~2 个 AI tick 才去下一座塔）
		if (brain.PendingExpansion.X != FP.Zero || brain.PendingExpansion.Y != FP.Zero)
		{
			if (TryBuildExpansionAt(team, raceCfg, brain.PendingExpansion))
				brain.PendingExpansion = FPVector2.Zero;
		}

		// =========================================================
		// 阶段一【占】：所有族统一先占“塔已掉/无守卫”的无主矿点。
		// 打（拆塔）和占（开矿）是两条独立评估的线，不再互相绑架——
		// 恶魔不用死等英雄，其它族也不用等塔死。
		// =========================================================
		int myMines = GetTeamMineCount(team);
		bool deferToAlly = ShouldDeferExpansionToAlly(team, myMines);
		FPVector2 freeCluster = deferToAlly
			? FPVector2.Zero
			: GetUnownedResourceCluster(team);
		if (freeCluster.X != FP.Zero || freeCluster.Y != FP.Zero)
		{
			if (TryBuildExpansionAt(team, raceCfg, freeCluster))
				return; // 本 tick 已派分矿工人
		}

		// 找最近未占领的塔
		FPVector2 towerPos = FPVector2.Zero;
		string towerType = "";
		RTS.Simulation.SimStructure towerSim = null;
		FP bestDist = FP.MaxValue;
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID != -2)
				continue;
			if (sim.StructureTypeId != "Tower" && sim.StructureTypeId != "NanoCore")
				continue;
			if (ExpansionClaimed(team, sim.Position))
				continue;
			FP d = FPVector2.DistanceSquared(sim.Position, GetTeamMainBasePos(team));
			if (d < bestDist)
			{
				bestDist = d;
				towerPos = sim.Position;
				towerType = sim.StructureTypeId;
				towerSim = sim;
			}
		}
		if (towerPos.X == FP.Zero && towerPos.Y == FP.Zero)
		{
			// 没有可拆的活塔：占的活已在阶段一尝试过
			return;
		}

		string race = raceCfg.RaceId;
		bool continuing = towerSim != null && brain.TowerRaidId == towerSim.ID;
		bool shouldRaidTower = towerSim != null && !IsBaseUnderAttack(team) &&
			(GetBotStrategy(team)?.CanRaid(this, team, towerSim, currentTick, continuing) ?? false);

		bool towerAlive = false;
		foreach (var sim in World.Structures.Values)
		{
			if (sim != null && !sim.IsDead && sim.TeamID == -2 &&
				(sim.StructureTypeId == "Tower" || sim.StructureTypeId == "NanoCore") &&
				FPVector2.DistanceSquared(sim.Position, towerPos) <= (FP)(3 * 64) * (FP)(3 * 64))
			{
				towerAlive = true;
				break;
			}
		}
		if (towerAlive)
		{
			if (shouldRaidTower && towerSim != null)
			{
				brain.TowerRaidId = towerSim.ID;
				brain.TowerRaidPos = towerPos;
				SendBotRaid(team, towerPos, race, currentTick);
			}
			return;
		}

		// 塔已拆除 → 在资源簇旁拍分矿
		brain.TowerRaidId = -1;
		FPVector2 cluster = GetResourceClusterCenter(towerPos);
		if (cluster.X == FP.Zero && cluster.Y == FP.Zero)
			cluster = towerPos;
		brain.PendingExpansion = cluster;
		TryBuildExpansionAt(team, raceCfg, cluster);
	}

	private bool TryBuildExpansionAt(int team, RaceConfig raceCfg, FPVector2 cluster)
	{
		bool allExist = true;
		foreach (string baseId in raceCfg.AiExpansionBaseIds)
		{
			if (CountOwnStructuresNear(team, baseId, cluster, 14) <= 0)
			{
				allExist = false;
				break;
			}
		}
		if (allExist)
			return true;

		int slot = 0;
		foreach (string baseId in raceCfg.AiExpansionBaseIds)
		{
			if (CountOwnStructuresNear(team, baseId, cluster, 14) > 0)
			{
				slot++;
				continue;
			}
			FPVector2 pos = GetBuildPosAround(cluster, slot);
			bool issued = false;
			if (GetBotStrategy(team)?.PanelConstruction == true)
			{
				for (int attempt = 0; attempt < 3 && !issued; attempt++)
				issued = TryBotPanelBuild(team, baseId, GetBuildPosAround(cluster, slot + attempt * 4));
			}
			else
			{
				issued = TryPlaceStructureAt(team, baseId, pos, 4);
			}
			if (issued)
			{
								if (raceCfg.RaceId == "Wanderer")
					GetBrain(team).ExpansionPos = pos; // 采集编队留守矿点
				return true;
			}
			slot++;
		}
		return false;
	}

	private bool IsTowerRaidActive(int team)
	{
		var brain = GetBrain(team);
		if (brain.TowerRaidId < 0)
			return false;
		var sim = World.FindSimEntity(brain.TowerRaidId) as SimStructure;
		return sim != null && !sim.IsDead && sim.Hp > FP.Zero && sim.TeamID == -2;
	}

	private bool ExpansionClaimed(int team, FPVector2 pos)
	{
		var player = RTS.World.Game.GetPlayerByTeam(team);
		var raceCfg = player?.Race != null
			? ConfigDatabase.GetRace(player.Race.RaceName)
			: null;
		if (raceCfg == null || raceCfg.AiExpansionBaseIds.Count == 0)
			return false;

		// 开矿只按“有没有开矿建筑”算：附近拍下过分矿（如 HellCity/CommandCenter）
		// 才算已占；SoulStone、兵营、主基地都不算（主基地是自己的老家，不是分矿）。
		FP radius = (FP)(14 * World.Grid.TileSize);
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID != team ||
				sim.CurrentState != SimStructure.StructureState.Active)
				continue;
			if (!raceCfg.AiExpansionBaseIds.Contains(sim.StructureTypeId))
				continue;
			if (FPVector2.IsWithinRange(sim.Position, pos, radius))
				return true;
		}
		return false;
	}

	// 己方圈到的资源点数量（与诊断日志“矿N”一致）
	private int GetTeamMineCount(int team)
	{
		int n = 0;
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead)
				continue;
			if (FindEntityById(s.ID) is not RTS.Units.ResourceStructure res)
				continue;
			if (!IsResourceAccessibleToTeam(team, res))
				continue;
			n++;
		}
		return n;
	}

	// 盟友判定：同组盟友的矿点比自己少 ≥2 片时，本队搁置占新矿，
	// 把无主矿让给落后的盟友发育（落后者自己不会让，避免互相等待死锁）
	private bool ShouldDeferExpansionToAlly(int team, int myMines)
	{
		int myGroup = GetTeamGroup(team);
		foreach (var p in RTS.World.Game.GetAllPlayers())
		{
			if (p == null || p.TeamId == team)
				continue;
			if (GetTeamGroup(p.TeamId) != myGroup)
				continue;
			int allyMines = GetTeamMineCount(p.TeamId);
			if (allyMines <= myMines - 2)
				return true;
		}
		return false;
	}

	// 找最近的“无主资源簇”：中立资源点聚簇、附近无活塔守卫、未被己方占领。
	// 用途：中立塔已经被打掉（或本来就没塔）的矿点可以直接开分矿，
	// 不必等英雄/重战车去拆活塔。
	private FPVector2 GetUnownedResourceCluster(int team)
	{
		FPVector2 basePos = GetTeamMainBasePos(team);
		FPVector2 best = FPVector2.Zero;
		FP bestDist = FP.MaxValue;
		var seen = new HashSet<int>();

		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead || sim.TeamID > 0 || seen.Contains(sim.ID))
				continue;
				if (!SimWorld.IsResourceNodeType(sim.StructureTypeId) || sim.ResourceAmount <= FP.Zero)
				continue;

			// 以该点为种子聚 14 格内的资源点
			FP cx = FP.Zero, cy = FP.Zero;
			int n = 0;
			FP radiusSq = (FP)(14 * World.Grid.TileSize) * (FP)(14 * World.Grid.TileSize);
			foreach (var other in World.Structures.Values)
			{
				if (other == null || other.IsDead || other.TeamID > 0)
					continue;
				if (!SimWorld.IsResourceNodeType(other.StructureTypeId) || other.ResourceAmount <= FP.Zero)
				continue;
				if (FPVector2.DistanceSquared(sim.Position, other.Position) <= radiusSq)
				{
					seen.Add(other.ID);
					cx += other.Position.X;
					cy += other.Position.Y;
					n++;
				}
			}
			if (n == 0)
				continue;

			FPVector2 center = new FPVector2(cx / (FP)n, cy / (FP)n);
			if (ExpansionClaimed(team, center))
				continue;
			// 附近有敌人 → 不能开矿（所有种族通用）
			if (HasEnemyNear(team, center, 12))
				continue;
			// 附近还有活塔守卫 → 留给拆塔流程（塔被打掉后自然会开这簇矿）
			if (HasLiveTowerNear(center, 16))
				continue;

			FP d = FPVector2.DistanceSquared(center, basePos);
			if (d < bestDist)
			{
				bestDist = d;
				best = center;
			}
		}
		return best;
	}

	private bool HasLiveTowerNear(FPVector2 center, int tiles)
	{
		FP r = (FP)(tiles * World.Grid.TileSize);
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != -2)
				continue;
			if (s.StructureTypeId != "Tower" && s.StructureTypeId != "NanoCore")
				continue;
			if (FPVector2.IsWithinRange(s.Position, center, r))
				return true;
		}
		return false;
	}

	// 指定范围（格）内是否有敌对单位或建筑（找矿点/分矿用，防止开矿撞敌人）
	private bool HasEnemyNear(int team, FPVector2 center, int tiles)
	{
		FP r = (FP)(tiles * World.Grid.TileSize);
		FP rSq = r * r;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || !AreTeamsHostile(team, u.TeamID))
				continue;
			if (FPVector2.DistanceSquared(u.Position, center) <= rSq)
				return true;
		}
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || !AreTeamsHostile(team, s.TeamID))
				continue;
			if (FPVector2.DistanceSquared(s.Position, center) <= rSq)
				return true;
		}
		return false;
	}

	// 最近敌人位置（单位或建筑，按距离；不用质心），以己方老家为基准
	private FPVector2 GetNearestEnemyPos(int team)
	{
		return GetNearestEnemyPos(team, GetTeamMainBasePos(team));
	}
	
	// 以任意基准点找最近敌人（单位或建筑）
	private FPVector2 GetNearestEnemyPos(int team, FPVector2 from)
	{
		FP bestD = FP.MaxValue;
		FPVector2 best = FPVector2.Zero;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || !AreTeamsHostile(team, u.TeamID))
				continue;
			FP d = FPVector2.DistanceSquared(u.Position, from);
			if (d < bestD)
			{
				bestD = d;
				best = u.Position;
			}
		}
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || !AreTeamsHostile(team, s.TeamID))
				continue;
			FP d = FPVector2.DistanceSquared(s.Position, from);
			if (d < bestD)
			{
				bestD = d;
				best = s.Position;
			}
		}
		return best;
	}

	private FPVector2 GetResourceClusterCenter(FPVector2 towerPos)
	{
		FP x = FP.Zero, y = FP.Zero;
		int n = 0;
		FP radius = (FP)(14 * World.Grid.TileSize);
		foreach (var sim in World.Structures.Values)
		{
			if (sim == null || sim.IsDead)
				continue;
			if (sim.StructureTypeId != "IronOre" && sim.StructureTypeId != "GasSpring" &&
				sim.StructureTypeId != "MetalMine" && sim.StructureTypeId != "WildFruit")
				continue;
			if (sim.ResourceAmount <= FP.Zero)
				continue;
			if (!FPVector2.IsWithinRange(sim.Position, towerPos, radius))
				continue;
			x += sim.Position.X;
			y += sim.Position.Y;
			n++;
		}
		if (n == 0)
			return FPVector2.Zero;
		return new FPVector2(x / (FP)n, y / (FP)n);
	}

	private int CountOwnStructuresNear(int team, string structId, FPVector2 center, int tiles)
	{
		FP radius = (FP)(tiles * World.Grid.TileSize);
		return CountStructures(team, s =>
			s.StructureTypeId == structId &&
			FPVector2.IsWithinRange(s.Position, center, radius));
	}

	// =========================================================
	// 战力评估（60 tick 缓存，按配置表计算，不碰视觉节点）
	// =========================================================
	private float GetBotArmyPower(int team, int currentTick)
	{
		var brain = GetBrain(team);
		if (currentTick - brain.ArmyPowerTick < 60)
			return brain.ArmyPower;
		float power = 0f;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team || !IsMilitaryUnit(u))
				continue;
			var cfg = ConfigDatabase.GetUnit(u.UnitTypeId);
			if (cfg == null)
				continue;
			power += RTS.Data.CombatRating.ComputeUnitRating(cfg);
		}
		brain.ArmyPower = power;
		brain.ArmyPowerTick = currentTick;
		return power;
	}

	private float GetEnemyArmyPower(int team, int currentTick)
	{
		var brain = GetBrain(team);
		if (currentTick - brain.EnemyPowerTick < 60)
			return brain.EnemyPower;
		float power = 0f;
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || !AreTeamsHostile(team, u.TeamID))
				continue;
			var cfg = ConfigDatabase.GetUnit(u.UnitTypeId);
			if (!IsMilitaryConfig(cfg))
				continue;
			power += RTS.Data.CombatRating.ComputeUnitRating(cfg);
		}
		brain.EnemyPower = power;
		brain.EnemyPowerTick = currentTick;
		return power;
	}

	// 在中心点附近螺旋搜索合法位置并派工建造
	private bool TryPlaceStructureAt(int team, string structName, FPVector2 center, int maxRing, WandererSquad assignSquad = null)
	{
		for (int d = 0; d <= maxRing; d++)
		{
			for (int dx = -d; dx <= d; dx++)
			{
				for (int dy = -d; dy <= d; dy++)
				{
					if (d > 0 && System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != d)
						continue;
					FPVector2 pos = center + new FPVector2((FP)(dx * 64), (FP)(dy * 64));
					if (TryBotPlaceStructure(team, structName, pos, assignSquad))
						return true;
				}
			}
		}
		return false;
	}

	// =========================================================
	// 调试诊断（每 300 tick 一条）
	// =========================================================
	private void PrintBotDiagnostics(int team, int currentTick)
	{
		var player = RTS.World.Game.GetPlayerByTeam(team);
		if (player?.PlayerData == null)
			return;

		int units = 0, workers = 0, military = 0, structures = 0, production = 0;
		int metalNodes = 0, gasNodes = 0, miningMetal = 0, miningGas = 0;
		FP metalLeft = FP.Zero, gasLeft = FP.Zero, cargoSum = FP.Zero;
		var typeCounts = new Dictionary<string, int>();
		foreach (var u in World.Units.Values)
		{
			if (u == null || u.IsDead || u.TeamID != team)
				continue;
			units++;
			typeCounts[u.UnitTypeId] = typeCounts.GetValueOrDefault(u.UnitTypeId) + 1;
			var cfg = ConfigDatabase.GetUnit(u.UnitTypeId);
			if (cfg != null && cfg.IsWorker)
				workers++;
			var node = FindEntityById(u.ID);
			if (node != null && IsMilitaryUnit(u))
				military++;
			if (node?.Brain?.GetActiveHarvestSource() is RTS.Units.ResourceStructure hs)
			{
				var hc = ConfigDatabase.GetStructure(hs.StructureName);
				if (hc != null && hc.ResourceType == ResourceType.Gas)
					miningGas++;
				else if (hc != null)
					miningMetal++;
				cargoSum += u.CargoAmount;
			}
		}
		foreach (var simStruct in World.Structures.Values)
		{
			if (simStruct == null || simStruct.IsDead)
				continue;
			if (FindEntityById(simStruct.ID) is not RTS.Units.ResourceStructure res)
				continue;
			if (!IsResourceAccessibleToTeam(team, res))
				continue;
			var rc = ConfigDatabase.GetStructure(res.StructureName);
			if (rc != null && rc.ResourceType == ResourceType.Gas)
			{
				gasNodes++;
				gasLeft += res.LogicEntity.ResourceAmount;
			}
			else if (rc != null)
			{
				metalNodes++;
				metalLeft += res.LogicEntity.ResourceAmount;
			}
		}
		foreach (var s in World.Structures.Values)
		{
			if (s == null || s.IsDead || s.TeamID != team)
				continue;
			structures++;
			var node = FindEntityById(s.ID);
			if (node?.Brain != null && node.Brain.GetActiveProductionAction() != null)
				production++;
		}

		float metal = player.PlayerData.GetResource(ResourceType.Metal);
		float gas = player.PlayerData.GetResource(ResourceType.Gas);
		float biomass = player.PlayerData.GetResource(ResourceType.Biomass);
		int used = player.PlayerData.GetUsedSupply();
		int max = player.PlayerData.GetMaxSupply();
		float power = GetBotArmyPower(team, currentTick);
			}
}
}
