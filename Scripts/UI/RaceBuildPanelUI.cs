using Godot;
using RTS.Data.Configs;

namespace RTS.Core
{
	/// <summary>
	/// 种族顶部面板公共基类：按钮绑定、F1~Fn 快捷键、本地化刷新、建造禁用、
	/// 两段式“扩散/技能”交互全部收敛到这里。
	/// 纳米（NanoPanelUI）与植物（PlantPanelUI）原来各自复制一份同样的 ~200 行，
	/// 子类现在只需提供按钮表与各自扩散确认逻辑；新种族面板直接继承即可。
	/// </summary>
	public abstract partial class RaceBuildPanelUI : PanelContainer
	{
		protected Button _spreadButton;
		protected UserController _user;
		protected readonly System.Collections.Generic.Dictionary<string, Button> _buildButtons = new();
		protected (Button Btn, Key Key)[] _hotkeys = System.Array.Empty<(Button, Key)>();
		protected bool _spreadPending = false;
		public bool IsSpreadPending => _spreadPending;

		/// <summary>子类提供“按钮节点名 → 建筑 ID”绑定表（快捷键从 F5 起自动分配）。</summary>
		protected abstract (string NodeName, string StructId)[] GetBuildBindings();

		/// <summary>
		/// 种族建造面板的快捷键**起点**。
		///
		/// 为什么从 F5 开始而不是 F1：
		///   F1/F2/F3 已被全局动作占用（选中空闲工人 / 全选作战单位 / 全选工人），
		///   而本面板的 `_UnhandledInput` 在 UserController **之后**执行 ——
		///   全局动作会先把 F1 吃掉，面板按 F1 永远不触发（技能和全选同时响应或都不响应）。
		///   F9/F10/F11 是调试作弊键，也避开。所以可用区是 F4~F8。
		///
		/// 超过 5 个建筑的种族（植物 10 个、洞穴 14 个）**拿不到全部快捷键**，
		/// 只能点按钮 —— 这是当前 UI 的真实上限，不是遗漏。
		/// 要支持更多，需要给面板做分页或第二排键位（见 ARCHITECTURE 的美术/UI 章节）。
		/// </summary>
		protected const Key HotkeyStart = Key.F4;

		/// <summary>本面板实际能分配的快捷键数量（F4~F8）。</summary>
		public const int MaxHotkeys = 5;

		/// <summary>扩散/技能确认：子类实现（纳米要校验菌毯，植物直接生成节点）。</summary>
		public abstract bool ConfirmSpreadAt(Vector2 worldPos);

		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Stop;
			FindUserController();

			var bindings = GetBuildBindings();
			_hotkeys = new (Button, Key)[bindings.Length + 1];
			_spreadButton = GetNodeOrNull<Button>("ButtonRow/BtnSpread");
			if (_spreadButton != null)
				_spreadButton.Pressed += ToggleSpreadPending;
			// 扩散技能占起点键（F4），建筑键依次 F5..F8
			_hotkeys[0] = (_spreadButton, HotkeyStart);
			for (int i = 0; i < bindings.Length; i++)
			{
				BindBuildButton(bindings[i].NodeName, bindings[i].StructId);
				// 超上限的按钮给 Key.None：_hotkeys 里仍保留条目，
				// 但因为永远匹配不上，等于"只提供点击"。
				Key k = i + 1 < MaxHotkeys
					? HotkeyStart + i + 1
					: Key.None;
				_hotkeys[i + 1] = (GetButtonOrNull(_buildButtons, bindings[i].StructId), k);
			}

			RefreshLocalizedTexts();
			RTS.Settings.Localization.LanguageChanged += RefreshLocalizedTexts;
		}

		public override void _ExitTree()
		{
			RTS.Settings.Localization.LanguageChanged -= RefreshLocalizedTexts;
		}

		private void RefreshLocalizedTexts()
		{
			RTS.Settings.Localization.ApplySceneTranslations(this);
			foreach (var pair in _hotkeys)
			{
				// 没有快捷键的按钮（超出 MaxHotkeys 的建筑）不加 `[...]` 后缀，
				// 否则会显示 [ - ] 这种没意义的标记
				if (pair.Btn == null || pair.Key == Key.None) continue;
				if (!pair.Btn.Text.Contains("["))
					pair.Btn.Text += $" [{UIKeyHelpers.KeyToText(pair.Key)}]";
			}
		}

		public override void _UnhandledInput(InputEvent @event)
		{
			if (!IsVisibleInTree())
				return;

			if (@event is InputEventKey key && key.Pressed && !key.Echo)
			{
				foreach (var pair in _hotkeys)
				{
					if (pair.Btn == null || key.Keycode != pair.Key)
						continue;
					if (pair.Btn.IsVisibleInTree() && !pair.Btn.Disabled)
					{
						pair.Btn.EmitSignal(Button.SignalName.Pressed);
						GetViewport().SetInputAsHandled();
						return;
					}
				}
			}

			if (!_spreadPending)
				return;

			if (@event.IsActionPressed("ui_cancel") ||
				(@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right && mb.Pressed))
			{
				CancelSpreadPending();
				GetViewport().SetInputAsHandled();
			}
		}

		protected void UpdateBuildButtons()
		{
			var player = RTS.World.Game.GetPlayerByTeam(Main.Instance?.LocalPlayerID ?? 0);
			var pd = player?.PlayerData;

			foreach (var kvp in _buildButtons)
			{
				var cfg = ConfigDatabase.GetStructure(kvp.Key);
				if (cfg == null || cfg.RequiredTechIds.Count == 0)
					continue;

				bool unlocked = pd != null;
				foreach (string req in cfg.RequiredTechIds)
				{
					if (pd == null || !pd.HasTech(req))
					{
						unlocked = false;
						break;
					}
				}
				kvp.Value.Disabled = !unlocked;
			}
		}

		public void ToggleSpreadPending()
		{
			_spreadPending = !_spreadPending;
		}

		public void CancelSpreadPending()
		{
			_spreadPending = false;
		}

		private void BindBuildButton(string nodeName, string structId)
		{
			var btn = GetNodeOrNull<Button>("ButtonRow/" + nodeName);
			if (btn == null)
				return;

			string id = structId;
			btn.Pressed += () => EnterBuild(id);
			_buildButtons[structId] = btn;
		}

		private void EnterBuild(string structId)
		{
			var cfg = ConfigDatabase.GetStructure(structId);
			if (cfg == null)
				return;

			FindUserController();
			if (_user == null)
				return;

			_user.EnterPanelBuildMode(structId, null, cfg.GridWidth, cfg.Costs, cfg.RequiredCreepType);
		}

		protected UserController FindUserController() =>
			_user ??= GetTree().Root.FindChild("UserController", true, false) as UserController;

		private static Button GetButtonOrNull(System.Collections.Generic.Dictionary<string, Button> map, string key) =>
			map.TryGetValue(key, out var btn) ? btn : null;
	}
}
