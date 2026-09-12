using Godot;
using System.Collections.Generic;
using RTS.Core;
using RTS.Data;
using RTS.Network;
using RTS.Simulation;
using RTS.World; // 访问 Game

namespace RTS.Core
{
	public partial class UserUI : CanvasLayer
	{
		/// <summary>聊天输入框是否打开（UserController 据此屏蔽暂停/投降等快捷键）。</summary>
		public static bool ChatOpen { get; private set; } = false;

		[Export] public Container ResourceContainer;

		private Dictionary<ResourceType, Label> _resourceLabels = new();
		private Label _supplyLabel;
		private Player _localPlayer;
		private bool _isInitialized = false;
		private float _resourceLabelTimer = 0f;
		private Panel _chatPanel;
		private RichTextLabel _chatHistory;
		private LineEdit _chatInput;
		private double _chatRefreshTimer = 0.0;
		private double _pingRefreshTimer = 0.0;
		private HashSet<string> _spawnedPings = new();
		private Label _observerBoard;
		private double _observerTimer = 0.0;
		private Label _timeLabel;
		private double _timeTimer = 0.0;

		public override void _Process(double delta)
		{
			RefreshHudVisibility();
			RefreshGameTime(delta);
			// 旁观者：全图视野（FogOfWar.RevealAll 已开）、不可操控、
			// 显示所有队伍的资源和人口
			if (RTS.Network.NetworkManager.Instance?.LocalIsObserver == true)
			{
				RefreshObserverBoard(delta);
				return;
			}

			if (!_isInitialized)
				FindAndInitLocalPlayer();

			// 聊天/信号刷新不依赖玩家初始化，模拟线程一启动就要显示
			RefreshChatAndPings(delta);

			if (!_isInitialized)
				return;

			// 人口/资源 UI 4Hz 刷新：避免每帧遍历世界阻塞模拟线程
			_resourceLabelTimer += (float)delta;
			if (_resourceLabelTimer < 0.25f)
				return;
			_resourceLabelTimer = 0f;

			if (_localPlayer != null && _localPlayer.PlayerData != null)
			{
				UpdateResourceLabels();
			}
		}

		private void RefreshObserverBoard(double delta)
		{
			if (ResourceContainer != null)
				ResourceContainer.Visible = false;

			if (_observerBoard == null)
			{
				_observerBoard = new Label
				{
					LabelSettings = new LabelSettings
					{
						FontSize = 16,
						FontColor = new Color(1f, 1f, 0.7f),
						OutlineSize = 2,
						OutlineColor = Colors.Black
					}
				};
				_observerBoard.SetAnchorsPreset(Control.LayoutPreset.TopRight);
				_observerBoard.Position = new Vector2(-460f, 10f);
				_observerBoard.Size = new Vector2(440f, 320f);
				AddChild(_observerBoard);
			}

			_observerTimer += delta;
			if (_observerTimer < 0.5)
				return;
			_observerTimer = 0.0;

			var sb = new System.Text.StringBuilder();
			sb.AppendLine(RTS.Settings.Localization.Tr("observer.title"));
			foreach (var p in RTS.World.Game.GetAllPlayers())
			{
				if (p?.PlayerData == null || p.Race == null)
					continue;
				int used = p.PlayerData.GetUsedSupply();
				int max = p.PlayerData.GetMaxSupply();
				var parts = new List<string>();
				foreach (var t in p.Race.GetVisibleResources())
					parts.Add($"{RTS.Settings.Localization.ResourceName(t)}:{(int)p.PlayerData.GetResource(t)}");
				string raceKey = "race." + p.Race.RaceName.ToLowerInvariant();
				sb.AppendLine($"{RTS.Settings.Localization.Tr("lobby.team_slot", p.TeamId)} " +
					$"{RTS.Settings.Localization.Tr(raceKey)} | {string.Join("  ", parts)} | " +
					$"{RTS.Settings.Localization.Tr("hud.supply", used, max)}");
			}
			_observerBoard.Text = sb.ToString();
		}

		public override void _Ready()
		{
			BuildChatPanel();
			BuildGameTimeLabel();
			BuildHudDesign();
		}

		private void BuildGameTimeLabel()
		{
			_timeLabel = new Label
			{
				LabelSettings = new LabelSettings
				{
					FontSize = 20,
					FontColor = Colors.White,
					OutlineSize = 2,
					OutlineColor = Colors.Black
				}
			};
			_timeLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
			_timeLabel.Position = new Vector2(12f, 8f);
			AddChild(_timeLabel);
		}

		// 对局内时间：锁步 tick × 0.05 秒 → mm:ss，4Hz 刷新
		private void RefreshGameTime(double delta)
		{
			if (_timeLabel == null || LockstepManager.Instance == null)
				return;
			_timeTimer += delta;
			if (_timeTimer < 0.25)
				return;
			_timeTimer = 0.0;
			int totalSec = (int)(LockstepManager.Instance.CurrentTick * 0.05);
			_timeLabel.Text = $"{totalSec / 60:D2}:{totalSec % 60:D2}";
		}

		public override void _UnhandledInput(InputEvent @event)
		{
			if (@event is not InputEventKey key || !key.Pressed || key.Echo)
				return;
			if (key.Keycode == Key.Enter)
			{
				if (_chatPanel != null && _chatPanel.Visible)
					SendChat();
				else
					ToggleChatInput();
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.Escape &&
				_chatPanel != null && _chatPanel.Visible)
			{
				ChatOpen = false;
				_chatPanel.Visible = false;
				GetViewport().SetInputAsHandled();
			}
		}

		/// <summary>打开/关闭聊天输入（T 键由 UserController 调用）。</summary>
		public void ToggleChatInput()
		{
			if (_chatPanel == null)
				return;
			ChatOpen = !_chatPanel.Visible;
			_chatPanel.Visible = ChatOpen;
			if (ChatOpen)
			{
				_chatInput?.GrabFocus();
				_chatInput?.SelectAll();
			}
		}

		private void SendChat()
		{
			if (_chatInput == null)
				return;
			string text = _chatInput.Text.Trim();
			_chatInput.Clear();
			if (text.Length > 0 && LockstepManager.Instance is { } ls)
			{
				ls.SendAction(new NetAction
				{
					ActionId = "Chat",
					PlayerID = ls.LocalPlayerID,
					ActionIdExtra = text,
					EntityIDs = new int[0]
				});
			}
			ChatOpen = false;
			if (_chatPanel != null)
				_chatPanel.Visible = false;
		}

		private void BuildChatPanel()
		{
			_chatPanel = new Panel
			{
				Name = "ChatPanel",
				Visible = false
			};
			_chatPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
			_chatPanel.Position = new Vector2(24, -300);
			_chatPanel.Size = new Vector2(560, 270);

			var margin = new MarginContainer();
			margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			margin.AddThemeConstantOverride("margin_left", 10);
			margin.AddThemeConstantOverride("margin_right", 10);
			margin.AddThemeConstantOverride("margin_top", 6);
			margin.AddThemeConstantOverride("margin_bottom", 10);
			_chatPanel.AddChild(margin);

			var vbox = new VBoxContainer();
			margin.AddChild(vbox);

			var title = new Label { Text = Tr("chat.title") };
			vbox.AddChild(title);

			_chatHistory = new RichTextLabel
			{
				SizeFlagsVertical = Control.SizeFlags.ExpandFill,
				ScrollFollowing = true,
				BbcodeEnabled = false
			};
			vbox.AddChild(_chatHistory);

			_chatInput = new LineEdit
			{
				PlaceholderText = Tr("chat.input_hint"),
				SizeFlagsVertical = Control.SizeFlags.ShrinkEnd
			};
			_chatInput.TextSubmitted += _ => SendChat();
			vbox.AddChild(_chatInput);

			AddChild(_chatPanel);
		}

		private void RefreshChatAndPings(double delta)
		{
			_chatRefreshTimer += delta;
			_pingRefreshTimer += delta;
			if (_chatRefreshTimer >= 0.25)
			{
				_chatRefreshTimer = 0.0;
				RefreshChat();
			}
			if (_pingRefreshTimer >= 0.15)
			{
				_pingRefreshTimer = 0.0;
				RefreshPings();
			}
		}

		private void RefreshChat()
		{
			if (_chatHistory == null || SimManager.Instance == null)
				return;
			var snapshot = SimManager.Instance.GetChatSnapshot();
			int start = System.Math.Max(0, snapshot.Count - 40);
			var sb = new System.Text.StringBuilder();
			for (int i = start; i < snapshot.Count; i++)
				sb.AppendLine(snapshot[i]);
			_chatHistory.Text = sb.ToString();
		}

		private void RefreshPings()
		{
			if (SimManager.Instance == null)
				return;
			int nowTick = LockstepManager.Instance?.CurrentTick ?? 0;
			var pings = SimManager.Instance.GetPingSnapshot(nowTick);
			var fx = GetTree().Root.FindChild("GameFX3D", true, false) as GameFX3D;
			if (fx == null)
				return;
			foreach (var p in pings)
			{
				string key = $"{p.PlayerID}:{p.ExpireTick}:{p.Position.X}:{p.Position.Y}";
				if (_spawnedPings.Add(key))
				{
					fx.SpawnClickMarker(
						new Vector2((float)p.Position.X, (float)p.Position.Y),
						GetPingColor(p.PlayerID));
				}
			}
			// 防泄漏：只保留最近一批已渲染标记的索引
			if (_spawnedPings.Count > 256)
				_spawnedPings.Clear();
		}

		private static Color GetPingColor(int playerId)
		{
			return playerId switch
			{
				1 => new Color(0.3f, 0.7f, 1f),
				2 => new Color(1f, 0.35f, 0.3f),
				3 => new Color(0.4f, 1f, 0.4f),
				_ => new Color(1f, 0.85f, 0.3f)
			};
		}

		private void FindAndInitLocalPlayer()
		{
			if (Main.Instance == null) return;

			int localID = Main.Instance.LocalPlayerID;
			if (localID <= 0) return; // 网络分配尚未完成

			// 从 Game 全局映射获取动态 Player 节点
			_localPlayer = Game.GetPlayerByTeam(localID);

			if (_localPlayer != null && _localPlayer.Race != null && _localPlayer.PlayerData != null)
			{
				GenerateLabels();
				_isInitialized = true;
				GD.Print($"[UserUI] UI 初始化完成，已绑定动态玩家: Team {localID}");
			}
		}

		private void GenerateLabels()
		{
			if (ResourceContainer == null)
			{
				GD.PrintErr("[UserUI Error] ResourceContainer 未赋值！");
				return;
			}
			if (_localPlayer == null || _localPlayer.Race == null) return;

			foreach (Node child in ResourceContainer.GetChildren())
				child.QueueFree();
			_resourceLabels.Clear();

			List<ResourceType> visibleRes = _localPlayer.Race.GetVisibleResources();

			LabelSettings highVisSettings = new LabelSettings
			{
				FontSize = 16,
				FontColor = new Color("dcebe8"),
				OutlineSize = 0,
				OutlineColor = Colors.Black,
				ShadowSize = 0,
				ShadowColor = new Color(0, 0, 0, 0.5f)
			};

			foreach (var type in visibleRes)
			{
				Label label = new Label
				{
					VerticalAlignment = VerticalAlignment.Center,
					LabelSettings = highVisSettings
				};
				label.AddThemeConstantOverride("margin_right", 15);

				ResourceContainer.AddChild(label);
				_resourceLabels.Add(type, label);
			}

			_supplyLabel = new Label
			{
				VerticalAlignment = VerticalAlignment.Center,
				LabelSettings = highVisSettings
			};
			_supplyLabel.AddThemeConstantOverride("margin_right", 15);
			ResourceContainer.AddChild(_supplyLabel);
		}

		private void UpdateResourceLabels()
		{
			foreach (var kvp in _resourceLabels)
			{
				ResourceType type = kvp.Key;
				Label label = kvp.Value;

				float amount = _localPlayer.PlayerData.GetResource(type);
				string resName = RTS.Settings.Localization.ResourceName(type);

				label.Text = $"{resName}: {(int)amount}  ";
			}

			if (_supplyLabel != null)
			{
				int used = _localPlayer.PlayerData.GetUsedSupply();
				int max = _localPlayer.PlayerData.GetMaxSupply();
				_supplyLabel.Text = RTS.Settings.Localization.Tr("hud.supply", used, max) + "  ";
			}
		}
	}
}
