using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FixMath.NET;
using RTS.Data;
using FP = FixMath.NET.Fix64;


namespace RTS.Simulation
{
		public partial class SimWorld
	{
		public long GetWorldHash()
		{
			long hash = 1469598103934665603L;

			hash = Mix(hash, GetCountersHash());
			hash = Mix(hash, GetUnitsHash());
			hash = Mix(hash, GetStructuresHash());
			hash = Mix(hash, GetProjectilesHash());
			hash = Mix(hash, GetCreepHash());
			hash = Mix(hash, GetCarpetSpreadHash());
			hash = Mix(hash, GetRngHash());

			return hash;
		}

		// 区段哈希：每个子系统独立计算，脱同步时可以精确定位差异来源
		public long GetCountersHash()
		{
			long hash = 1469598103934665603L;

			hash = Mix(hash, _nextEntityId);
			hash = Mix(hash, (long)(CarpetSpreadCooldown * (FP)100m));
			hash = Mix(hash, CarpetSpreadCharges);
			hash = Mix(hash, Units.Count);
			hash = Mix(hash, Structures.Count);
			hash = Mix(hash, Projectiles.Count);
			hash = Mix(hash, EarthCoreBoostedNodes.Count);
			hash = Mix(hash, AppliedEarthCores.Count);

			return hash;
		}

		public long GetUnitsHash()
		{
			long hash = 1469598103934665603L;

			foreach (var kvp in Units)
			{
				hash = Mix(hash, kvp.Key);
				hash = Mix(hash, kvp.Value.GetStateHash());
			}

			return hash;
		}

		public long GetStructuresHash()
		{
			long hash = 1469598103934665603L;

			foreach (var kvp in Structures)
			{
				hash = Mix(hash, kvp.Key);
				hash = Mix(hash, kvp.Value.GetStateHash());
			}

			return hash;
		}

		public long GetProjectilesHash()
		{
			long hash = 1469598103934665603L;

			for (int i = 0; i < Projectiles.Count; i++)
			{
				SimProjectile p = Projectiles[i];

				if (p == null)
				{
					hash = Mix(hash, -9999);
					continue;
				}

				hash = Mix(hash, i);
				hash = Mix(hash, ToRaw1000(p.Position.X));
				hash = Mix(hash, ToRaw1000(p.Position.Y));
				hash = Mix(hash, ToRaw1000(p.Speed));
				hash = Mix(hash, ToRaw1000(p.HitRadius));
				hash = Mix(hash, ToRaw1000(p.Damage));
				hash = Mix(hash, p.DamageType);
				hash = Mix(hash, p.IsDead ? 1 : 0);
				hash = Mix(hash, p.HasHit ? 1 : 0);
				hash = Mix(hash, p.Target != null ? p.Target.ID : -1);
				hash = Mix(hash, (int)p.Motion);
				hash = Mix(hash, p.FootprintScaledDamage ? 1 : 0);
				hash = Mix(hash, ToRaw1000(p.ImpactRadius));
				hash = Mix(hash, ToRaw1000(p.LobTime));
			}

			return hash;
		}

		public long GetCreepHash()
		{
			return CreepGrid.GetStateHash();
		}

		public long GetCarpetSpreadHash()
		{
			long hash = 1469598103934665603L;
			hash = Mix(hash, CarpetSpreads.Count);

			foreach (var s in CarpetSpreads)
			{
				hash = Mix(hash, s.CenterX);
				hash = Mix(hash, s.CenterY);
				hash = Mix(hash, s.OwnerTeam);
				hash = Mix(hash, ToRaw1000(s.MaxRadius));
				hash = Mix(hash, s.TicksElapsed);
				hash = Mix(hash, s.DurationTicks);
			}

			return hash;
		}

		public long GetRngHash()
		{
			return (long)RNG.State;
		}

		private long Mix(long hash, long value)
		{
			unchecked
			{
				hash ^= value;
				hash *= 1099511628211L;
				return hash;
			}
		}

		private long ToRaw1000(FP value)
		{
			return (long)(value * (FP)1000m);
		}

		public string GetWorldDebugReport()
		{
			StringBuilder sb = new();

			sb.AppendLine("======== WORLD DEBUG REPORT ========");
			sb.AppendLine($"NextEntityId: {_nextEntityId}");
			sb.AppendLine($"Units: {Units.Count}");
			sb.AppendLine($"Structures: {Structures.Count}");
			sb.AppendLine($"Projectiles: {Projectiles.Count}");
			sb.AppendLine($"CarpetSpreads: {CarpetSpreads.Count}");
			sb.AppendLine($"WorldHash: {GetWorldHash()}");

			sb.AppendLine();
			sb.AppendLine("---- Units ----");

			foreach (var kvp in Units)
			{
				SimUnit unit = kvp.Value;

				if (unit == null)
				{
					sb.AppendLine($"{kvp.Key}: null");
					continue;
				}

				sb.AppendLine(unit.GetDebugState());
			}

			sb.AppendLine();
			sb.AppendLine("---- Structures ----");

			foreach (var kvp in Structures)
			{
				SimStructure structure = kvp.Value;

				if (structure == null)
				{
					sb.AppendLine($"{kvp.Key}: null");
					continue;
				}

				sb.AppendLine(structure.GetDebugState());
			}

			sb.AppendLine();
			sb.AppendLine("---- Projectiles ----");

			for (int i = 0; i < Projectiles.Count; i++)
			{
				SimProjectile p = Projectiles[i];

				if (p == null)
				{
					sb.AppendLine($"{i}: null");
					continue;
				}

				int targetId = p.Target != null ? p.Target.ID : -1;

				sb.AppendLine(
					$"{i}|Pos:({ToRaw1000(p.Position.X)},{ToRaw1000(p.Position.Y)})" +
					$"|Speed:{ToRaw1000(p.Speed)}" +
					$"|HitRadius:{ToRaw1000(p.HitRadius)}" +
					$"|Damage:{ToRaw1000(p.Damage)}" +
					$"|DamageType:{p.DamageType}" +
					$"|Target:{targetId}" +
					$"|Dead:{p.IsDead}" +
					$"|Hit:{p.HasHit}"
				);
			}

			return sb.ToString();
		}
	}
}
