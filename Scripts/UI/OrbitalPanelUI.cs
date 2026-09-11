using Godot;
using RTS.Data;
using RTS.Network;
using RTS.Units;

namespace RTS.Core
{
	// 联盟顶部面板：轨道控制中心的技能（雷达/轨道炮/资源交换）+ 能量显示
	// 结构上与纳米顶部面板平级，由 RacePanelManager 按种族显示
	public partial class OrbitalPanelUI : PanelContainer
	{
		private UserController _user;
		private Button _radarBtn;
		private Button _strikeBtn;
		private Button _exchangeMetalBtn;
		private Button _exchangeGasBtn;
		private Label _energyLabel;
		private (Button Btn, Key Key)[] _hotkeys = System.Array.Empty<(Button, Key)>();

		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Stop;
			_user = GetTree().Root.FindChild("UserController", true, false) as UserController;

			_energyLabel = GetNodeOrNull<Label>("ButtonRow/EnergyLabel");
			_radarBtn = GetNodeOrNull<Button>("ButtonRow/BtnRadar");
			_strikeBtn = GetNodeOrNull<Button>("ButtonRow/BtnStrike");
			_exchangeMetalBtn = GetNodeOrNull<Button>("ButtonRow/BtnExchangeMetal");
			_exchangeGasBtn = GetNodeOrNull<Button>("ButtonRow/BtnExchangeGas");

			if (_radarBtn != null)
				_radarBtn.Pressed += () => _user?.EnterRadarPending();
			if (_strikeBtn != null)
				_strikeBtn.Pressed += () => _user?.EnterOrbitalStrikePending();
			if (_exchangeMetalBtn != null)
				_exchangeMetalBtn.Pressed += () => SendExchange("ExchangeMetalToGas");
			if (_exchangeGasBtn != null)
				_exchangeGasBtn.Pressed += () => SendExchange("ExchangeGasToMetal");

			// 面板快捷键用 F1~F4，避开单位动作面板的 QWERT/ASDFG/ZXCVB
			_hotkeys = new (Button, Key)[]
			{
				(_radarBtn, Key.F1),
				(_strikeBtn, Key.F2),
				(_exchangeMetalBtn, Key.F3),
				(_exchangeGasBtn, Key.F4)
			};

			// 场景按钮文本按 tr_key 翻译后再补快捷键后缀
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
				if (pair.Btn != null && !pair.Btn.Text.Contains("["))
				pair.Btn.Text += $" [{UIKeyHelpers.KeyToText(pair.Key)}]";
			}
		}

		public override void _Process(double delta)
		{
			var player = RTS.World.Game.GetPlayerByTeam(Main.Instance?.LocalPlayerID ?? 0);
			if (player?.PlayerData == null || player.Race?.RaceName != "Union")
				return;

			var oc = WorldScanner.FindOwnedOrbitalControl(GetTree(), Main.Instance?.LocalPlayerID ?? 0);
			var pd = player.PlayerData;
			float energy = pd.GetResource(ResourceType.Energy);

			if (_energyLabel != null)
				_energyLabel.Text = RTS.Settings.Localization.Tr("orbital.energy", (int)energy);

			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure("OrbitalControl");
			float radarCost = cfg?.RadarEnergyCost ?? 20f;
			float strikeCost = cfg?.StrikeEnergyCost ?? 40f;
			float exchangeCost = cfg?.ExchangeEnergyCost ?? 20f;

			bool has = oc != null;

			if (_radarBtn != null)
				_radarBtn.Disabled = !has || energy < radarCost;
			if (_strikeBtn != null)
				_strikeBtn.Disabled = !has || energy < strikeCost;
			if (_exchangeMetalBtn != null)
				_exchangeMetalBtn.Disabled = !has || energy < exchangeCost;
			if (_exchangeGasBtn != null)
				_exchangeGasBtn.Disabled = !has || energy < exchangeCost;
		}

		public override void _UnhandledInput(InputEvent @event)
		{
			if (!IsVisibleInTree())
				return;

			if (@event is not InputEventKey key || !key.Pressed || key.Echo)
				return;

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

		private void SendExchange(string actionId)
		{
			var oc = WorldScanner.FindOwnedOrbitalControl(GetTree(), Main.Instance?.LocalPlayerID ?? 0);
			if (oc?.LogicEntity == null)
				return;

			var cmd = NetAction.GroundCommand(
				actionId, Main.Instance?.LocalPlayerID ?? 0, Vector2.Zero, new[] { oc.LogicEntity.ID });
			LockstepManager.Instance?.SendAction(cmd);
		}
	}
}
