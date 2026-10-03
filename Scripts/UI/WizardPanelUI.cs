using Godot;
using System.Collections.Generic;
using RTS.Data.Configs;
using RTS.Units;

namespace RTS.Core
{
	/// <summary>
	/// 巫师族顶部面板：常驻显示面板能量 + 四个 T3 建筑的"二选一"技能入口。
	///
	/// 参考对象：纳米顶部面板（RaceBuildPanelUI 子类）与联盟轨道面板（OrbitalPanelUI），
	/// 三者都由 RacePanelManager 按种族显示/隐藏。
	///
	/// 为什么需要它（而不是只靠建筑命令卡上的技能按钮）：
	///   · 命令卡上的技能只在**选中那座建筑**时可见，玩家看得到按钮却看不到
	///     "我现在有多少能量"，也没法一眼看到四个 T3 建筑各解锁了什么；
	///   · 联盟的轨道面板就是这么做的（常显能量 + 技能按钮），巫师保持一致。
	///
	/// 按钮由**代码动态生成**而不是写死在 user_ui.tscn 里：技能名、能量消耗、
	/// 解锁科技全部来自配置表（PanelSkillMode / SkillEnergyCost /
	/// SkillRequiredTechIds）。写死的话每加一个技能就要改场景 + 改代码两处。
	/// </summary>
	public partial class WizardPanelUI : PanelContainer
	{
		// 与 EntityFactory3D.AddWizardPanelSkills / SimManager.WizardSkillBit 同源。
		//
		// 第四项是显示名用的本地化 id：技能 id 本身大多不是配置表里的实体名
		// （SummonStoneGolem / FireRain …），直接 TrName(skillId) 会原样吐回英文 id。
		// 召唤类复用单位名（石魔像/土魔像），其余用 Excel 面板行的原文。
		private static readonly (string Id, long Bit, string CasterId, string NameId)[] Skills =
		{
			("SummonStoneGolem", 256,   "WizStoneCircle",  "WizStoneGolem"),
			("SummonEarthGolem", 512,   "WizStoneCircle",  "WizEarthGolem"),
			("TeleportField",    1024,  "WizSpaceLab",     "TeleportField"),
			("TimeFreeze",       2048,  "WizSpaceLab",     "TimeFreeze"),
			("FireRain",         4096,  "WizElementForge", "FireRain"),
			("WaterWall",        8192,  "WizElementForge", "WaterWall"),
			("InspireMelody",    16384, "WizConcertHall",  "InspireMelody"),
			("SolemnMelody",     32768, "WizConcertHall",  "SolemnMelody"),
		};

		private UserController _user;
		private Label _energyLabel;
		private readonly List<(Button Btn, string SkillId, string CasterId, string NameId)> _skillButtons = new();
		private (Button Btn, Key Key)[] _hotkeys = System.Array.Empty<(Button, Key)>();
		private bool _built;

		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Stop;
			_user = GetTree().Root.FindChild("UserController", true, false) as UserController;
			// 按钮**不能在这里建**：UI 场景的 _Ready 早于 ConfigDatabase.LoadAll()，
			// 此时 GetStructure 全是 null，八个技能一个都建不出来（实测面板只剩
			// 能量标签）。改成第一次 _Process 时懒构建，那时配置表已经就绪。
			RTS.Settings.Localization.LanguageChanged += RefreshTexts;
		}

		public override void _ExitTree()
		{
			RTS.Settings.Localization.LanguageChanged -= RefreshTexts;
		}

		private void EnsureBuilt()
		{
			if (_built)
				return;
			// 配置表没加载完就再等一帧
			if (ConfigDatabase.GetStructure("WizStoneCircle") == null)
				return;

			var row = GetNodeOrNull<HBoxContainer>("ButtonRow");
			if (row == null)
			{
				GD.PrintErr("[WizardPanel] 缺少 ButtonRow 节点，面板无法构建");
				_built = true;
				return;
			}

			if (GetNodeOrNull<Label>("ButtonRow/EnergyLabel") is { } existing)
				_energyLabel = existing;
			else
			{
				_energyLabel = new Label
				{
					Name = "EnergyLabel",
					CustomMinimumSize = new Vector2(120, 26),
					VerticalAlignment = VerticalAlignment.Center,
				};
				row.AddChild(_energyLabel);
			}

			BuildSkillButtons(row);

			// 面板快捷键 F1~F8，与联盟面板同段位（避开单位命令卡的 QWERT/ASDFG）
			_hotkeys = new (Button, Key)[_skillButtons.Count];
			for (int i = 0; i < _skillButtons.Count; i++)
				_hotkeys[i] = (_skillButtons[i].Btn, Key.F1 + i);

			RefreshTexts();
			_built = true;
		}

		private void BuildSkillButtons(HBoxContainer row)
		{
			var btnScene = GD.Load<PackedScene>("res://Scenes/UI/button.tscn");
			foreach (var s in Skills)
			{
				var cfg = ConfigDatabase.GetStructure(s.CasterId);
				if (cfg == null || (cfg.PanelSkillMode & s.Bit) == 0)
					continue;   // 该建筑没配这个技能

				Button btn;
				if (btnScene != null)
				{
					btn = btnScene.Instantiate<Button>();
					btn.CustomMinimumSize = new Vector2(104, 26);
				}
				else
				{
					btn = new Button { CustomMinimumSize = new Vector2(104, 26) };
				}
				btn.Name = "Btn" + s.Id;
				btn.FocusMode = FocusModeEnum.None;

				string skillId = s.Id;
				btn.Pressed += () => OnSkillPressed(skillId);
				row.AddChild(btn);
				_skillButtons.Add((btn, s.Id, s.CasterId, s.NameId));
			}
		}

		private void OnSkillPressed(string skillId)
		{
			_user ??= GetTree().Root.FindChild("UserController", true, false) as UserController;
			if (_user == null)
				return;

			var caster = FindAvailableCaster(skillId);
			if (caster == null)
				return;
			_user.EnterPanelSkillPending(skillId, caster);
		}

		/// <summary>本地玩家已完工、且配了这个技能的那座建筑。</summary>
		private Structure FindAvailableCaster(string skillId)
		{
			int team = Main.Instance?.LocalPlayerID ?? 0;
			var sim = SimManager.Instance;
			if (sim?.World == null)
				return null;

			var def = SkillOf(skillId);
			if (def == null)
				return null;

			foreach (var s in sim.World.Structures.Values)
			{
				if (s == null || s.IsDead || s.TeamID != team)
					continue;
				if (s.CurrentState != Simulation.SimStructure.StructureState.Active)
					continue;
				if (s.StructureTypeId != def.Value.CasterId)
					continue;
				var cfg = ConfigDatabase.GetStructure(s.StructureTypeId);
				if (cfg == null || (cfg.PanelSkillMode & def.Value.Bit) == 0)
					continue;
				if (sim.FindEntityById(s.ID) is Structure node)
					return node;
			}
			return null;
		}

		private static (string Id, long Bit, string CasterId, string NameId)? SkillOf(string skillId)
		{
			foreach (var s in Skills)
				if (s.Id == skillId)
					return s;
			return null;
		}

		/// <summary>待选阶段：以施法者为中心显示"射程圈 + 落点效果圈"。</summary>
		public void DrawPendingRange(Vector2 mouseWorld)
		{
			var ind = SkillRangeIndicator.GetOrCreate(GetTree());
			var def = SkillOf(_user?.PendingPanelSkillId ?? "");
			if (def == null)
			{
				ind.HideRanges();
				return;
			}
			var cfg = ConfigDatabase.GetStructure(def.Value.CasterId);
			if (cfg == null || cfg.SkillRadiusTiles <= 0f)
			{
				ind.HideRanges();
				return;
			}
			ind.HideRanges();
			ind.ShowEffectRange(new Vector3(mouseWorld.X, 0f, mouseWorld.Y),
				cfg.SkillRadiusTiles * 64f, new Color(0.7f, 0.6f, 1f, 0.5f));
		}

		private void RefreshTexts()
		{
			foreach (var (btn, skillId, casterId, nameId) in _skillButtons)
			{
				var cfg = ConfigDatabase.GetStructure(casterId);
				int cost = (int)(cfg?.SkillEnergyCost ?? 0f);
				// 技能名沿用实体名本地化表：这几个技能 id 本身就是"石魔像/火雨"等表格原文
				string name = RTS.Settings.Localization.TrName(nameId);
				btn.Text = cost > 0 ? $"{name} {cost}e" : name;
			}

			for (int i = 0; i < _hotkeys.Length; i++)
			{
				var (btn, key) = _hotkeys[i];
				if (btn == null || key == Key.None)
					continue;
				if (!btn.Text.Contains("["))
					btn.Text += $" [{UIKeyHelpers.KeyToText(key)}]";
			}
		}

		public override void _Process(double delta)
		{
			EnsureBuilt();
			if (!_built)
				return;

			var player = RTS.World.Game.GetPlayerByTeam(Main.Instance?.LocalPlayerID ?? 0);
			var pd = player?.PlayerData;
			if (pd == null)
				return;

			// 能量上限来自种族配置表（RaceConfig.MaxEnergy = 400），
			// RaceData 是场景数据类，拿不到这个字段。
			float energy = pd.GetResource(ResourceType.Energy);
			float max = ConfigDatabase.GetRace(player.Race?.RaceName ?? "")?.MaxEnergy ?? 0f;
			if (_energyLabel != null)
			{
				_energyLabel.Text = max > 0f
					? RTS.Settings.Localization.Tr("info.energy", energy, max)
					: RTS.Settings.Localization.Tr("orbital.energy", (int)energy);
			}

			bool pending = _user != null && _user.IsPanelSkillPending;

			foreach (var (btn, skillId, casterId, nameId) in _skillButtons)
			{
				var cfg = ConfigDatabase.GetStructure(casterId);
				if (cfg == null)
					continue;

				// 未解锁（二选一没研究）→ 置灰；能量不够 → 置灰。
				// 置灰而不是隐藏：隐藏了玩家不知道有这个技能、也不知道该研究什么。
				bool unlocked = true;
				if (cfg.SkillRequiredTechIds != null &&
					cfg.SkillRequiredTechIds.TryGetValue(skillId, out string needTech) &&
					!string.IsNullOrEmpty(needTech))
				{
					unlocked = pd.HasTech(needTech);
				}

				bool hasCaster = FindAvailableCaster(skillId) != null;
				btn.Disabled = !unlocked || !hasCaster || energy < cfg.SkillEnergyCost;

				// 待选目标时高亮，提示"再点一下地面"
				btn.Modulate = pending ? new Color(1f, 0.92f, 0.6f) : Colors.White;
			}
		}

		public override void _UnhandledInput(InputEvent @event)
		{
			if (RTS.UI.MatchMenu.BlocksGameInput || !IsVisibleInTree())
				return;

			// 右键/ESC 取消待选（与纳米面板一致）
			if (_user != null && _user.IsPanelSkillPending &&
				(@event.IsActionPressed("ui_cancel") ||
				 (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right && mb.Pressed)))
			{
				_user.CancelPanelSkillPending();
				GetViewport().SetInputAsHandled();
				return;
			}

			if (@event is not InputEventKey key || !key.Pressed || key.Echo)
				return;

			foreach (var (btn, hotkey) in _hotkeys)
			{
				if (btn == null || key.Keycode != hotkey)
					continue;
				if (btn.IsVisibleInTree() && !btn.Disabled)
				{
					btn.EmitSignal(Button.SignalName.Pressed);
					GetViewport().SetInputAsHandled();
					return;
				}
			}
		}
	}
}
