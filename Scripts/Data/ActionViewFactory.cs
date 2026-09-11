using Godot;
using RTS.Actions;
using RTS.Actions.Implementation;

namespace RTS.Data
{
	// 动作按钮视图统一工厂：单位/建筑命令卡共用同一套“动作 → EntityAction”转换
	public static class ActionViewFactory
	{
		public static EntityAction Build(UnitAction a)
		{
			var act = new EntityAction
			{
				ActionId = a.ActionName,
				DisplayName = a.ActionName,
				Icon = a.Icon,
				IsEnabled = a.CanExecute(),
				SlotIndex = a.SlotIndex,
				CooldownRemaining = a.GetCooldownRemaining(),
				CooldownMax = a.GetCooldownMax()
			};

			switch (a)
			{
				case TrainUnitAction tu:
					act.DisplayName = RTS.Settings.Localization.TrName(tu.UnitName);
					break;
				case BuildAction ba:
					act.DisplayName = RTS.Settings.Localization.TrName(ba.StructureName);
					break;
				case ResourceExchangeAction ex:
					if (!string.IsNullOrEmpty(ex.DisplayNameText)) act.DisplayName = ex.DisplayNameText;
					break;
				case StimAction st:
					if (!string.IsNullOrEmpty(st.DisplayNameText)) act.DisplayName = st.DisplayNameText;
					break;
				case HeroSkillAction hs:
					{
						string unitId = (hs.Unit as RTS.Units.Unit)?.UnitName ?? "";
						string skillKey = $"skill.{unitId}.{hs.SkillIndex}";
						if (unitId.Length > 0 && RTS.Settings.Localization.Has(skillKey))
							act.DisplayName = RTS.Settings.Localization.Tr(skillKey);
						else if (!string.IsNullOrEmpty(hs.DisplayNameText))
							act.DisplayName = hs.DisplayNameText;
						else
							act.DisplayName = RTS.Settings.Localization.Tr(hs.SkillIndex == 1 ? "action.HeroSkill1" : "action.HeroSkill2");
					}
					break;
				case PlasmaStrikeAction ps:
					if (!string.IsNullOrEmpty(ps.DisplayNameText)) act.DisplayName = ps.DisplayNameText;
					break;
				case PlasmaModeAction pm:
					if (!string.IsNullOrEmpty(pm.DisplayNameText)) act.DisplayName = pm.DisplayNameText;
					break;
				case CruiserMobilityAction cm:
					if (!string.IsNullOrEmpty(cm.DisplayNameText)) act.DisplayName = cm.DisplayNameText;
					break;
				case ResearchAction ra:
					act.DisplayName = RTS.Settings.Localization.TrName(ra.TechId);
					break;
				case RadarAction rad:
					if (!string.IsNullOrEmpty(rad.DisplayNameText)) act.DisplayName = rad.DisplayNameText;
					break;
				case OrbitalStrikeAction os:
					if (!string.IsNullOrEmpty(os.DisplayNameText)) act.DisplayName = os.DisplayNameText;
					break;
				case AutoProduceModeAction ap:
					if (!string.IsNullOrEmpty(ap.DisplayNameText)) act.DisplayName = ap.DisplayNameText;
					break;
				case DeployAction dep:
					if (!string.IsNullOrEmpty(dep.DisplayNameText)) act.DisplayName = dep.DisplayNameText;
					break;
				case SwitchAmmoAction sa:
					if (!string.IsNullOrEmpty(sa.DisplayNameText)) act.DisplayName = sa.DisplayNameText;
					break;
				case GarrisonAction ga:
					if (!string.IsNullOrEmpty(ga.DisplayNameText)) act.DisplayName = ga.DisplayNameText;
					break;
				case ReleaseWorkersAction rw:
					if (!string.IsNullOrEmpty(rw.DisplayNameText)) act.DisplayName = rw.DisplayNameText;
					break;
				case RebuildWreckageAction rw2:
					if (!string.IsNullOrEmpty(rw2.DisplayNameText)) act.DisplayName = rw2.DisplayNameText;
					break;
				case ResonanceWaveAction rwa:
					if (!string.IsNullOrEmpty(rwa.DisplayNameText)) act.DisplayName = rwa.DisplayNameText;
					break;
				case SeismicWaveAction swa:
					if (!string.IsNullOrEmpty(swa.DisplayNameText)) act.DisplayName = swa.DisplayNameText;
					break;
				case WormholeCreateAction wca:
					if (!string.IsNullOrEmpty(wca.DisplayNameText)) act.DisplayName = wca.DisplayNameText;
					break;
				case BurrowAction ba2:
					if (!string.IsNullOrEmpty(ba2.DisplayNameText)) act.DisplayName = ba2.DisplayNameText;
					break;
				case DevourAction da:
					if (!string.IsNullOrEmpty(da.DisplayNameText)) act.DisplayName = da.DisplayNameText;
					break;
				case OmniLeafDogAction olda:
					if (!string.IsNullOrEmpty(olda.DisplayNameText)) act.DisplayName = olda.DisplayNameText;
					break;
				case PlantForestSpreadAction pfs:
					if (!string.IsNullOrEmpty(pfs.DisplayNameText)) act.DisplayName = pfs.DisplayNameText;
					break;
				default:
					// 基础指令槽位统一（移动/停止/攻击/驻守）
					act.SlotIndex = a.ActionName switch
					{
						"Move" => 0,
						"Stop" => 1,
						"Attack" => 2,
						"Hold" => 3,
						_ => a.SlotIndex
					};
					break;
			}

			// P2-6：动作按钮本地化（有 action.<name> 键才覆盖）
			string locKey = "action." + a.ActionName;
			// 英雄技能已按单位技能键名处理，避免被通用键覆盖
			if (RTS.Settings.Localization.Has(locKey) && a is not HeroSkillAction)
				act.DisplayName = RTS.Settings.Localization.Tr(locKey);

			string State(string key) => RTS.Settings.Localization.Tr("action_state." + key);
			if (a.Unit?.LogicEntity is RTS.Simulation.SimUnit unit)
			{
				act.StatusText = a switch
				{
					PlasmaModeAction => State(unit.PlasmaAutoFire ? "auto_on" : "auto_off"),
					BurrowAction => State(unit.IsBurrowed ? "burrowed" : "surface"),
					DeployAction => State(unit.DeployState switch { 1 => "deploying", 2 => "deployed", 3 => "packing", _ => "mobile" }),
					SwitchAmmoAction => RTS.Settings.Localization.TrName(a.Unit.CombatModule?.ActiveWeapon?.WeaponName ?? ""),
					_ => ""
				};
			}
			else if (a is AutoProduceModeAction && a.Unit?.LogicEntity is RTS.Simulation.SimStructure structure)
			{
				var config = RTS.Data.Configs.ConfigDatabase.GetStructure(structure.StructureTypeId);
				var ids = structure.AutoProduceMode == 0 ? config?.AutoProduceUnitIds : config?.AutoProduceModeUnitIds;
				if (ids != null && ids.Count > 0)
					act.StatusText = RTS.Settings.Localization.TrName(ids[0]);
			}
			return act;
		}
	}
}
