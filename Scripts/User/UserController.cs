using Godot;
using System.Collections.Generic;
using System.Linq;
using RTS.Actions;
using RTS.Actions.Implementation;
using RTS.Data;
using RTS.Units;
using RTS.World;
using RTS.Simulation;
using RTS.Network;

namespace RTS.Core
{
	public partial class UserController : Node
	{
		[Export] public PlayerController TargetController;
		[ExportGroup("Settings")]
		[Export] public float DragThreshold = 10.0f;
		[Export] public float FormationSpacing = 160.0f;

		private GameFX3D _gameFX;

		private List<IEntity> _selectedEntities = new();
		private bool _isDragging;
		private Vector2 _dragStart, _dragEnd;

		private BuildPreview _activePreview;
		private string _pendingName;
		private int _pendingSize;
		private Godot.Collections.Dictionary<ResourceType, float> _pendingCosts;
		private CreepType _pendingReqCreep;
	private bool _panelBuildMode = false;
	private bool _radarPending = false;
	private bool _orbitalStrikePending = false;
	private string _pendingHeroSkill = "";
	private bool _pendingHeroSkillGround = false;
	// 植物树墙：拖拽连续生成一排
	private bool _wallDragActive = false;
	private Vector2I _wallLastCell;
		private bool _middleWasDown = false;

		public override void _Ready()
		{
			_gameFX = GetParent().GetNodeOrNull<GameFX3D>("GameFX3D");

			if (_gameFX == null)
			{
				_gameFX = new GameFX3D { Name = "GameFX3D" };
				// 父节点可能还在构建子节点，延迟到下一帧挂载
				GetParent().CallDeferred(Node.MethodName.AddChild, _gameFX);
			}

			// P1-5 总线化：动作面板/信息面板/生产队列/路径线改为订阅 GameEventBus，
			// 控制器只发布选中变化，不再直接调用各面板
			RTS.Core.Events.GameEventBus.ActionTriggered += OnUITriggered;

			// 右下角帧率小字（画面 FPS / 逻辑 TPS）——场景挂载中禁止直接 AddChild 到 root，延迟一帧
			Callable.From(() => GetTree().Root.AddChild(new RTS.UI.FpsOverlay { Name = "FpsOverlay" })).CallDeferred();
			// P1-4 协商/投票 HUD（Ctrl+H 发起重开投票，N/M 表决）
			Callable.From(() => GetTree().Root.AddChild(new RTS.UI.VoteUI { Name = "VoteUI" })).CallDeferred();
		}

		public override void _ExitTree()
		{
			RTS.Core.Events.GameEventBus.ActionTriggered -= OnUITriggered;
			base._ExitTree();
		}

		public override void _Process(double delta)
		{
			// 旁观者：不可操控（相机由 RTSCamera 独立控制）
			if (RTS.Network.NetworkManager.Instance?.LocalIsObserver == true)
				return;
			// TargetController 可能是已释放的节点（玩家死亡/场景切换后仍被引用），
			// 直接访问会抛 ObjectDisposedException（纳米建造面板点击闪退即此路径）。
			if (TargetController == null || !GodotObject.IsInstanceValid(TargetController))
				return;

			// P1-5：聊天输入打开时屏蔽暂停/投降/信号等快捷键（T 仍可关闭）
			bool chatOpen = RTS.Core.UserUI.ChatOpen;

			// P2-5：P/K/T 改走 InputMap 动作（可在设置菜单改键）
			if (!chatOpen &&
				Input.IsActionJustPressed("game_pause") && RTS.Core.SimManager.Instance is { } sim)
			{
				bool next = !sim.IsPaused;
				sim.SetPaused(next);
				GD.Print(next ? "[Pause] 模拟已暂停" : "[Pause] 模拟已继续");
			}
			if (!chatOpen &&
				Input.IsActionJustPressed("game_surrender") &&
				RTS.Network.LockstepManager.Instance is { } lockstep)
			{
				lockstep.SendAction(NetAction.GroundCommand("Surrender", lockstep.LocalPlayerID, Vector2.Zero));
				GD.Print("[Surrender] 已发送投降指令");
			}
			// P1-5：T 打开/关闭聊天输入框（发送由聊天面板的 Enter 处理）
			if (Input.IsActionJustPressed("game_chat"))
			{
				(GetTree().Root.FindChild("UserUI", true, false) as RTS.Core.UserUI)?.ToggleChatInput();
			}

			// 一键选中所有闲置工人（矿挖完挂机时按 F1 快速拉出来重新派活）
			// 注意：F1/F2/F3 与编队、镜头回中都放在 _UnhandledInput 里处理，
			// 不放这里轮询 —— _Process 的轮询**绕过 UI 的输入消费**，
			// 会导致"种族建造面板开着时按 F1 同时触发面板技能和全选农民"。
			// 放在 _UnhandledInput 才能让先消费的 UI 把它吃掉。

			// 中键地图信号：**改成 Ctrl+中键**。
			// 中键本身让给"拖拽平移镜头"（RTS 标准手感：中键拖屏），
			// 两者都用裸中键会互相打断 —— 想 ping 一下就变成平移了。
			bool middleDown = Input.IsMouseButtonPressed(MouseButton.Middle);
			bool ctrlHeld = Input.IsKeyPressed(Key.Ctrl);
			if (!chatOpen && middleDown && !_middleWasDown && ctrlHeld &&
				RTS.Network.LockstepManager.Instance is { } pingLockstep &&
				RTS.Core.Main.Instance != null)
			{
				Vector2 pingPos = GetMousePos();
				pingLockstep.SendAction(NetAction.GroundCommand("Ping", pingLockstep.LocalPlayerID, pingPos));
			}
			_middleWasDown = middleDown;

			// 建造模式下：预览跟随 3D 鼠标射线
			if (_activePreview != null)
				_activePreview.SetTargetPosition(GetMousePos());

			// 建造模式下：人口不足时预览变红、禁止放置（与服务器判定一致）
		if (_activePreview != null && _pendingName.Length > 0 && !_panelBuildMode)
			{
				var popCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(_pendingName);
				bool popOk = true;
				var buildPlayer = GetTargetPlayer();

				// 蓝图建筑豁免人口检查（多足牧羊人蓝图满人口死锁）
				if (popCfg != null && popCfg.SupplyUsed > 0 &&
					!_pendingName.StartsWith("Blueprint_") && buildPlayer?.PlayerData != null)
				{
					popOk = buildPlayer.PlayerData.GetUsedSupply() + popCfg.SupplyUsed <=
						buildPlayer.PlayerData.GetMaxSupply();
				}

				_activePreview.ExtraBlocked = !popOk;
			}

			UpdateSkillRangeIndicators();
		}

		public override void _UnhandledInput(InputEvent @event)
		{			// P1-1：时间加速通用快捷键（1/2/3/4，任何地图/单机/旁观者都可用；
			// 联机时 SimulationSpeed 内部强制 1x，不会破坏锁步）
			if (@event is InputEventKey speedKey && speedKey.Pressed && !speedKey.Echo &&
				!RTS.Core.UserUI.ChatOpen)
			{
				int speed = speedKey.Keycode switch
				{
					Key.Key1 => 1,
					Key.Key2 => 2,
					Key.Key3 => 4,
					Key.Key4 => 8,
					_ => 0
				};
				if (speed > 0)
				{
					var sim = RTS.Core.SimManager.Instance;
					if (sim != null)
					{
						var net = RTS.Network.NetworkManager.Instance;
						bool onlineLocked = net != null && !RTS.Network.NetworkManager.OfflineMode &&
							!(RTS.Network.LockstepManager.Instance?.ReplayMode ?? false);
						if (!onlineLocked)
						{
							sim.SimulationSpeed = speed;
							GD.Print($"[Time] 倍速 -> x{speed}");
						}
						else
						{
							GD.Print("[Time] 联机对局强制 1x，倍速不可用");
						}
					}
					GetViewport().SetInputAsHandled();
					return;
				}
			}

			// 旁观者：允许左键/框选查看，禁止右键与其它控制输入
			if (RTS.Network.NetworkManager.Instance?.LocalIsObserver == true)
			{
				HandleObserverMouse(@event);
				return;
			}
			// P1-4：Ctrl+H 发起重开投票（锁步确定性处理）
			if (@event is InputEventKey voteKey && voteKey.Pressed && !voteKey.Echo &&
				voteKey.Keycode == Key.H && voteKey.CtrlPressed)
			{
				RTS.UI.VoteUI.SendVote(RTS.Core.SimManager.VoteKindRematch, true);
				GetViewport().SetInputAsHandled();
				return;
			}

			// 调试作弊：F11 给自己刷资源（走锁步指令，双端确定性一致）
			// 注意：F8 是 Godot 编辑器自带的停止运行快捷键，不能占用
			if (@event is InputEventKey cheatKey && cheatKey.Pressed && !cheatKey.Echo && cheatKey.Keycode == Key.F11)
			{
				SendCheatResources();
				GetViewport().SetInputAsHandled();
				return;
			}
			if (@event is InputEventKey buildKey && buildKey.Pressed && !buildKey.Echo && buildKey.Keycode == Key.F9)
			{
				SendCheatToggle("CheatToggleBuild", "秒建建筑");
				GetViewport().SetInputAsHandled();
				return;
			}
			if (@event is InputEventKey techKey && techKey.Pressed && !techKey.Echo && techKey.Keycode == Key.F10)
			{
				SendCheatToggle("CheatToggleResearch", "秒点科技");
				GetViewport().SetInputAsHandled();
				return;
			}

			if (_activePreview != null) { HandleBuildInput(@event); return; }

			// ---- 单位指令热键（攻击移动 / 停止 / 驻守）----
			// 这三个是 RTS 最基础的指令，此前**完全没有快捷键**：
			// 只能右键点地（=移动）或在动作面板里点。教程里写的"按 A 攻击移动"
			// 在当时是做不到的（A 还被动作面板槽位占用）。
			if (HandleCommandHotkeys(@event))
				return;

			if (@event is InputEventMouseButton mb)
			{
				if (mb.ButtonIndex == MouseButton.Left)
				{
					if (mb.Pressed)
					{
						// 攻击移动待命：左键点地面下达（A 键进入这个状态，见 HandleCommandHotkeys）
						if (_pendingOrderHotkey.Length > 0)
						{
							ConfirmPendingOrderHotkey(GetMousePos());
							GetViewport().SetInputAsHandled();
							return;
						}

						// 英雄技能目标选择
						if (_pendingHeroSkill.Length > 0)
						{
							ConfirmHeroSkill();
							GetViewport().SetInputAsHandled();
							return;
						}

						// 雷达落点选择：点地面确认
						if (_radarPending)
						{
							SendRadar();
							GetViewport().SetInputAsHandled();
							return;
						}

						// 轨道炮目标选择：点地面确认落点
						if (_orbitalStrikePending)
						{
							SendOrbitalStrike();
							GetViewport().SetInputAsHandled();
							return;
						}

						// 扩散待放置：点地面确定圆心
						var nanoPanel = GetTree().Root.FindChild("RacePanel_Nano", true, false) as NanoPanelUI;

						if (nanoPanel != null && nanoPanel.IsSpreadPending)
						{
							nanoPanel.ConfirmSpreadAt(GetMousePos());
							GetViewport().SetInputAsHandled();
							return;
						}

						var plantPanel = GetTree().Root.FindChild("RacePanel_Plant", true, false) as PlantPanelUI;
						if (plantPanel != null && plantPanel.IsSpreadPending)
						{
							plantPanel.ConfirmSpreadAt(GetMousePos());
							GetViewport().SetInputAsHandled();
							return;
						}

						HandleLeftClick();
					}
					else if (_isDragging) EndDragging();
				}
				else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
				{
					if (_radarPending || _orbitalStrikePending || _pendingHeroSkill.Length > 0)
					{
						_radarPending = false;
						_orbitalStrikePending = false;
						_pendingHeroSkill = "";
						GetViewport().SetInputAsHandled();
						return;
					}

					HandleRightClick();
				}
			}
			else if (@event is InputEventMouseMotion && _isDragging)
			{
				_dragEnd = GetMousePos();
				_gameFX?.UpdateBox(_dragStart, _dragEnd, true);
			}
		}

		private void HandleLeftClick()
		{
			if (Input.IsKeyPressed(Key.Ctrl))
			{
				var target = WorldScanner.Raycast(GetTree(), GetMousePos(), Main.Instance.LocalPlayerID);
				if (RTS.Network.NetworkManager.Instance?.LocalIsObserver == true)
					UpdateSelection(WorldScanner.ScreenSelectAll(GetTree(), target, GetScreenWorldRect()));
				else
					UpdateSelection(WorldScanner.ScreenSelect(GetTree(), target, GetScreenWorldRect(), Main.Instance.LocalPlayerID));
			}
			else
			{
				_isDragging = true;
				_dragStart = GetMousePos();
			}
		}

		// 旁观者输入：只放行左键选择/拖拽框选，其它一律忽略
		private void HandleObserverMouse(InputEvent @event)
		{
			if (@event is InputEventMouseButton mb)
			{
				if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
					HandleLeftClick();
				else if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed && _isDragging)
					EndDragging();
			}
			else if (@event is InputEventMouseMotion && _isDragging)
			{
				_dragEnd = GetMousePos();
				_gameFX?.UpdateBox(_dragStart, _dragEnd, true);
			}
		}

		private void EndDragging()
		{
			_isDragging = false;
			_dragEnd = GetMousePos();
			_gameFX?.UpdateBox(_dragStart, _dragEnd, false);

			// Shift 多选：点击=加入/移出（toggle），框选=追加合并，点空地=保留原选择
			bool shiftMulti = Input.IsKeyPressed(Key.Shift);
			List<IEntity> result;
			if (_dragStart.DistanceTo(_dragEnd) < DragThreshold)
			{
				var hit = WorldScanner.Raycast(GetTree(), _dragEnd, Main.Instance.LocalPlayerID);
				if (shiftMulti)
				{
					result = new List<IEntity>(_selectedEntities);
					if (hit != null)
					{
						if (result.Contains(hit))
							result.Remove(hit);
						else
							result.Add(hit);
					}
				}
				else
				{
					result = hit != null ? new List<IEntity> { hit } : new List<IEntity>();
				}
			}
			else
			{
				var boxed = RTS.Network.NetworkManager.Instance?.LocalIsObserver == true
					? WorldScanner.BoxSelectAll(GetTree(), new Rect2(_dragStart, _dragEnd - _dragStart).Abs())
					: WorldScanner.BoxSelect(GetTree(), new Rect2(_dragStart, _dragEnd - _dragStart).Abs(), Main.Instance.LocalPlayerID);
				if (shiftMulti)
				{
					var merged = new List<IEntity>(_selectedEntities);
					foreach (var e in boxed)
					{
						if (!merged.Contains(e))
							merged.Add(e);
					}
					result = merged;
				}
				else
				{
					result = boxed;
				}
			}
			UpdateSelection(result);
		}

		private void UpdateSelection(List<IEntity> newSelection)
		{
			// 旁观者：直接发布选择快照（不经过 TargetController，面板只看信息）
			if (RTS.Network.NetworkManager.Instance?.LocalIsObserver == true)
			{
				_selectedEntities = newSelection ?? new List<IEntity>();
				int team = _selectedEntities.Count > 0 ? _selectedEntities[0].TeamID : -1;
				RTS.Core.Events.GameEventBus.PublishSelectionChanged(_selectedEntities, team);
				return;
			}
			if (TargetController == null || !GodotObject.IsInstanceValid(TargetController))
				return;
			TargetController.SelectEntities(newSelection);
			_selectedEntities = TargetController.SelectedEntities;

			int myTeam = GetTargetPlayer()?.TeamId ?? -1;
			RTS.Core.Events.GameEventBus.PublishSelectionChanged(_selectedEntities, myTeam);
		}

		public void ForceSelect(IEntity entity) => UpdateSelection(new List<IEntity> { entity });

		// 选中本地队伍所有“闲置工人”：无战斗/采集/建造/移动目标
		private void SelectIdleWorkers()
		{
			int myTeam = GetTargetPlayer()?.TeamId ?? -1;
			if (myTeam <= 0)
				return;
			var sim = RTS.Core.SimManager.Instance;
			if (sim?.World == null)
				return;

			var idle = new List<IEntity>();
			foreach (var u in sim.World.Units.Values)
			{
				if (u == null || u.IsDead || u.TeamID != myTeam)
					continue;
				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitTypeId);
				if (cfg == null || !cfg.IsWorker)
					continue;
				if (u.CombatTargetId >= 0 || u.HasTarget)
					continue;
				var node = sim.FindEntityById(u.ID);
				if (node == null || node.Brain == null)
					continue;
				if (node.Brain.GetActiveHarvestSource() != null ||
					node.Brain.GetActiveBuildTarget() != null)
					continue;
				idle.Add(node);
			}

			UpdateSelection(idle);
			if (idle.Count > 0)
				GD.Print($"[Select] 选中闲置工人 {idle.Count} 个");
		}

		// =========================================================
		// 单位指令热键：攻击移动 / 停止 / 驻守
		//
		// 为什么需要：这三个是最基础的 RTS 指令，而本项目此前**一个快捷键都没有**，
		// 只能靠右键点地（= 移动，不会自动索敌）或去动作面板里点。
		// 教程里甚至写着"按 A 攻击移动"，但当时 A 被动作面板槽位占着、
		// 攻击移动本身也没绑定任何键 —— 玩家照着做只会触发别的东西。
		//
		// 交互沿用 RTS 惯例：按 A 进入"攻击移动待命"，再左键点地面下达；
		// 按 S / H 立即生效（不需要目标点）。
		// =========================================================
		private string _pendingOrderHotkey = "";

		/// <summary>返回 true 表示这次输入已被指令热键吃掉。</summary>
		private bool HandleCommandHotkeys(InputEvent @event)
		{
			if (@event is not InputEventKey key || !key.Pressed || key.Echo)
				return false;
			if (RTS.Core.UserUI.ChatOpen)
				return false;
			if (RTS.Network.NetworkManager.Instance?.LocalIsObserver == true)
				return false;

			// 选择类：全选作战单位 / 全选工人 / 空闲工人
			// 放在这里（而不是 _Process 轮询）是为了尊重 UI 的输入消费：
			// 种族建造面板也吃 F1..Fn，_Process 轮询会绕过去导致同时触发两件事。
			if (Input.IsActionPressed("game_select_all_army"))
			{
				SelectAllOfKind(workersOnly: false);
				GetViewport().SetInputAsHandled();
				return true;
			}
			if (Input.IsActionPressed("game_select_all_workers"))
			{
				SelectAllOfKind(workersOnly: true);
				GetViewport().SetInputAsHandled();
				return true;
			}
			if (Input.IsActionPressed("game_select_idle_workers"))
			{
				SelectIdleWorkers();
				GetViewport().SetInputAsHandled();
				return true;
			}

			// 镜头回中
			if (Input.IsActionPressed("game_cam_center"))
			{
				(GetTree().CurrentScene?.GetNodeOrNull<RTSCamera>("RTSCamera"))?.CenterOnFocus();
				GetViewport().SetInputAsHandled();
				return true;
			}

			// 编队：Ctrl+数字 存，数字 取（需要事件本身，见 HandleControlGroupEvent）
			if (HandleControlGroupEvent(@event))
			{
				GetViewport().SetInputAsHandled();
				return true;
			}

			if (Input.IsActionPressed("game_attack_move"))
			{
				_pendingOrderHotkey = "AttackMove";
				GD.Print("[Order] 攻击移动：左键点目标位置（Esc 取消）");
				GetViewport().SetInputAsHandled();
				return true;
			}

			if (Input.IsActionPressed("game_stop"))
			{
				_pendingOrderHotkey = "";
				IssueImmediateOrder("Stop");
				GetViewport().SetInputAsHandled();
				return true;
			}

			if (Input.IsActionPressed("game_hold"))
			{
				_pendingOrderHotkey = "";
				IssueImmediateOrder("Hold");
				GetViewport().SetInputAsHandled();
				return true;
			}

			// Esc 取消待命指令
			if (_pendingOrderHotkey.Length > 0 && key.Keycode == Key.Escape)
			{
				_pendingOrderHotkey = "";
				GD.Print("[Order] 已取消");
				GetViewport().SetInputAsHandled();
				return true;
			}

			return false;
		}

		/// <summary>左键落下"待命指令"的目标点（目前只有攻击移动）。</summary>
		private void ConfirmPendingOrderHotkey(Vector2 worldPos)
		{
			string actionId = _pendingOrderHotkey;
			_pendingOrderHotkey = "";
			if (actionId.Length == 0) return;

			var units = TargetController?.GetMySelectedUnits();
			if (units == null || units.Count == 0)
				return;

			int localId = Main.Instance?.LocalPlayerID ?? 0;
			var ids = new List<int>();
			foreach (var u in units)
				if (u.LogicEntity != null) ids.Add(u.LogicEntity.ID);
			if (ids.Count == 0) return;

			bool queue = Input.IsKeyPressed(Key.Shift);
			RTS.Network.LockstepManager.Instance?.SendAction(
				NetAction.GroundCommand(actionId, localId, worldPos, ids, -1, queue));

			_gameFX?.SpawnClickMarker(worldPos, new Color(1f, 0.5f, 0.2f, 1f));
			GD.Print($"[Order] {actionId} → {ids.Count} 个单位 @ ({worldPos.X:0},{worldPos.Y:0})");
		}

		/// <summary>对当前选中单位下达"不需要目标点"的指令（Stop/Hold）。</summary>
		private void IssueImmediateOrder(string actionId)		{
			var units = TargetController?.GetMySelectedUnits();
			if (units == null || units.Count == 0)
				return;

			int localId = Main.Instance?.LocalPlayerID ?? 0;
			var ids = new List<int>();
			foreach (var u in units)
				if (u.LogicEntity != null) ids.Add(u.LogicEntity.ID);
			if (ids.Count == 0)
				return;

			// 走锁步指令（不是本地直接 StartAction），保证双端一致
			RTS.Network.LockstepManager.Instance?.SendAction(
				NetAction.GroundCommand(actionId, localId, Vector2.Zero, ids, -1, false));
		}

		// =========================================================
		// 编队：Ctrl+数字 记录当前选择，数字 取回
		//
		// 这是 RTS 的核心手感（"1 队主力 / 2 队分矿"），此前完全没有。
		// 存的是**实体 ID 而不是节点引用**：单位会死、会换场景，
		// 按 ID 每次取回时重新解析，死掉的自动剔除。
		//
		// 判定用事件而不是 Input.IsPhysicalKeyPressed 轮询：
		// 轮询会在按住时每帧重复触发，还得自己写去抖；
		// 事件天然"一次按下一次触发"，也不会绕过 UI 的输入消费。
		// =========================================================
		private readonly List<int>[] _controlGroups = new List<int>[RTS.Settings.InputActions.ControlGroupCount];

		/// <summary>处理编队按键事件。</summary>
		private bool HandleControlGroupEvent(InputEvent @event)
		{
			if (@event is not InputEventKey key || !key.Pressed || key.Echo)
				return false;

			int index = RTS.Settings.InputActions.ControlGroupIndex(key.PhysicalKeycode);
			if (index < 0)
				return false;

			if (Input.IsKeyPressed(Key.Ctrl))
				StoreControlGroup(index);
			else
				RecallControlGroup(index);
			return true;
		}

		private void StoreControlGroup(int index)
		{
			var ids = new List<int>();
			foreach (var e in _selectedEntities)
				if (e?.LogicEntity != null) ids.Add(e.LogicEntity.ID);
			_controlGroups[index] = ids;
			GD.Print($"[Group] 编队 {index + 1} ← {ids.Count} 个单位");
		}

		private void RecallControlGroup(int index)
		{
			var ids = _controlGroups[index];
			if (ids == null || ids.Count == 0)
				return;

			var sim = RTS.Core.SimManager.Instance;
			if (sim == null)
				return;

			// 每次取回都重新解析：单位死了/换场景了就自动剔除
			var alive = new List<IEntity>();
			foreach (int id in ids)
			{
				var node = sim.FindEntityById(id);
				if (node != null && !node.IsDeadOrNull())
					alive.Add(node);
			}

			if (alive.Count == 0)
			{
				GD.Print($"[Group] 编队 {index + 1} 已无存活单位");
				_controlGroups[index] = null;
				return;
			}

			UpdateSelection(alive);
			GD.Print($"[Group] 编队 {index + 1} → {alive.Count} 个单位");
		}

		/// <summary>全选：workersOnly=false 选作战单位，true 只选工人。</summary>
		private void SelectAllOfKind(bool workersOnly)
		{
			int myTeam = GetTargetPlayer()?.TeamId ?? -1;
			if (myTeam <= 0)
				return;
			var sim = RTS.Core.SimManager.Instance;
			if (sim?.World == null)
				return;

			var picked = new List<IEntity>();
			foreach (var u in sim.World.Units.Values)
			{
				if (u == null || u.IsDead || u.TeamID != myTeam)
					continue;
				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitTypeId);
				if (cfg == null || cfg.IsSegment)
					continue;

				if (workersOnly != cfg.IsWorker)
					continue;

				var node = sim.FindEntityById(u.ID);
				if (node != null)
					picked.Add(node);
			}

			UpdateSelection(picked);
			GD.Print($"[Select] 全选{(workersOnly ? "工人" : "作战单位")} {picked.Count} 个");
		}

		private void HandleRightClick()
		{
			if (TargetController == null || !GodotObject.IsInstanceValid(TargetController))
				return;
			Vector2 mPos = GetMousePos();
			IEntity target = WorldScanner.Raycast(GetTree(), mPos, Main.Instance.LocalPlayerID);
			bool isShift = Input.IsKeyPressed(Key.Shift);
			var myUnits = TargetController.GetMySelectedUnits()
				.Where(u => WorldScanner.IsOperable(u, Main.Instance.LocalPlayerID))
				.ToList();
			int myTeam = GetTargetPlayer()?.TeamId ?? -1;

			var rallyStructures = _selectedEntities
				.OfType<Structure>()
				.Where(s => s.TeamID == myTeam && IsProductionStructure(s) && s.LogicEntity != null)
				.ToList();

			if (myUnits.Count == 0 && rallyStructures.Count == 0)
				return;

			int targetEntityId = target?.LogicEntity?.ID ?? -1;

			// 生产建筑：右键设置/追加集结点（新造单位会自动执行集结队列）
			if (rallyStructures.Count > 0)
			{
				string rallyAction = "Move";
				if (target != null && target.TeamID != myTeam && target.TeamID != -1)
					rallyAction = "Attack";
				else if (target is ResourceStructure)
					rallyAction = "Harvest";

				var rallyCmd = NetAction.GroundCommand(
					"Rally_" + rallyAction, Main.Instance.LocalPlayerID, mPos,
					rallyStructures.Select(s => s.LogicEntity.ID), targetEntityId, isShift);
				LockstepManager.Instance.SendAction(rallyCmd);
			}

			// 单位：普通/Shift 队列指令
			if (myUnits.Count > 0)
			{
				int[] entityIds = myUnits.Where(u => u.LogicEntity != null).Select(u => u.LogicEntity.ID).ToArray();
				if (entityIds.Length > 0)
				{
					// 泰伦驻扎：工程兵右键藻类工厂进入驻扎
					if (target is Structure garrisonTarget &&
						RTS.Data.Configs.ConfigDatabase.GetStructure(garrisonTarget.StructureName) is { } gCfg &&
						gCfg.GarrisonCapacity > 0 &&
						myUnits.All(u => u is Unit uu &&
							RTS.Data.Configs.ConfigDatabase.GetUnit(uu.UnitName)?.CanGarrison == true))
					{
						var garrisonCmd = NetAction.GroundCommand(
							"Garrison", Main.Instance.LocalPlayerID, mPos,
							entityIds, targetEntityId, isShift);
						LockstepManager.Instance.SendAction(garrisonCmd);
						_gameFX?.SpawnClickMarker(mPos, new Color(0.6f, 0.9f, 0.3f, 1f));
						return;
					}

					string order = DetermineOrder(myUnits[0], target);
					var cmd = NetAction.GroundCommand(
						order, Main.Instance.LocalPlayerID, mPos,
						entityIds, targetEntityId, isShift);
					LockstepManager.Instance.SendAction(cmd);
				}
			}

			_gameFX?.SpawnClickMarker(mPos, GetClickColor(target, isShift));
		}

		// 小地图右键：对选中的己方单位下达移动指令（目标点 = 小地图换算的世界坐标）
		public void HandleMinimapRightClick(Vector2 worldPos)
		{
			if (RTS.Network.NetworkManager.Instance?.LocalIsObserver == true)
				return;

			int localTeam = Main.Instance?.LocalPlayerID ?? 0;
			int[] entityIds = _selectedEntities
				.Where(e => e.LogicEntity != null && e.TeamID == localTeam)
				.Select(e => e.LogicEntity.ID).ToArray();
			if (entityIds.Length == 0)
				return;

			bool isShift = Input.IsKeyPressed(Key.Shift);
			var cmd = NetAction.GroundCommand(
				"Move", localTeam, worldPos, entityIds, -1, isShift);
			LockstepManager.Instance.SendAction(cmd);
			_gameFX?.SpawnClickMarker(worldPos, new Color(0.6f, 0.9f, 0.3f, 1f));
		}

		// 是否有训练槽（生产建筑）——只有这类建筑才能设置集结点
		private bool IsProductionStructure(Structure structure)
		{
			if (structure?.Brain == null)
				return false;

			foreach (Node child in structure.Brain.GetChildren())
			{
				if (child is TrainUnitAction)
					return true;
			}

			// 恶魔自动生产建筑同样支持集结点
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(structure.StructureName);
			if (cfg != null && cfg.AutoProduceUnitIds.Count > 0)
				return true;

			return false;
		}

		private string DetermineOrder(Unit source, IEntity target)
		{
			if (target == null)
			{
				if (source != null && RTS.Data.Configs.ConfigDatabase.GetUnit(source.UnitName)?.DefaultAttackMove == true)
					return "AttackMove";

				return "Move";
			}
			if (target is ShrineStructure) return "Move";
			if (target is ResourceStructure || target.ToString().Contains("Resource")) return "Harvest";
			if (source != null && target.TeamID == source.TeamID && IsRepairableTarget(source, target)) return "Repair";
			// 洞穴残骸：右键直接快速重建
			if (target is Structure wreck && wreck.StructureName == "CaveWreckage" &&
				source != null && source.TeamID == wreck.TeamID)
				return "RebuildWreckage";
			if (target is Structure s)
			{
				if ((s.CurrentState == Structure.StructureState.Blueprint || s.IsUnderConstruction) && (source == null || s.TeamID == source.TeamID))
					return "Build_" + s.StructureName;
				if (source != null && RTS.Core.SimManager.Instance != null &&
					RTS.Core.SimManager.Instance.AreTeamsHostile(source.TeamID, s.TeamID)) return "Attack";
			}
			// P1-5：同组盟友不能右键攻击（2v2）；中立塔(-2)可以打
			if (source != null &&
				RTS.Core.SimManager.Instance != null &&
				RTS.Core.SimManager.Instance.AreTeamsHostile(source.TeamID, target.TeamID)) return "Attack";

			if (source != null && RTS.Data.Configs.ConfigDatabase.GetUnit(source.UnitName)?.DefaultAttackMove == true)
				return "AttackMove";

			return "Move";
		}

		private static bool IsRepairableTarget(Unit source, IEntity target)
		{
			if (RTS.Data.Configs.ConfigDatabase.GetUnit(source.UnitName)?.CanRepair != true)
				return false;

			if (target.LogicEntity == null || target.LogicEntity.IsDead || target.LogicEntity.Hp >= target.LogicEntity.MaxHp)
				return false;

			if (target is Structure s)
				return s.CurrentState == Structure.StructureState.Completed;

			if (target is Unit u)
			{
				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(u.UnitName);
				return cfg != null && cfg.ArmorType == RTS.Data.ArmorType.Mechanical;
			}

			return false;
		}

		public void EnterBuildMode(string name, Texture2D icon, int size, Godot.Collections.Dictionary<ResourceType, float> costs, CreepType reqCreep)
		{
			ExitBuildMode();
			_panelBuildMode = false;
			_pendingName = name;
			_pendingSize = size; _pendingCosts = costs; _pendingReqCreep = reqCreep;
			int ownerTeam = GetTargetPlayer()?.TeamId ?? 0;
			_activePreview = new BuildPreview();
			_activePreview.Setup(icon, size, reqCreep, ownerTeam);
			(_gameFX ?? (Node)GetTree().Root).AddChild(_activePreview);
		}

		// 安全获取本地玩家：TargetController 可能已被释放（玩家死亡/跨场景）
		private Player GetTargetPlayer()
		{
			if (TargetController != null && GodotObject.IsInstanceValid(TargetController))
			{
				try
				{
					return TargetController.GetParent<Player>();
				}
				catch (System.Exception)
				{
					// 原生节点已释放：IsInstanceValid 通过但访问仍抛
					// ObjectDisposedException（此前会中断 EnterBuildMode，
					// 导致纳米建造预览不创建、按钮点了没反应）
				}
			}
			int localId = Main.Instance?.LocalPlayerID ?? 0;
			return localId > 0 ? RTS.World.Game.GetPlayerByTeam(localId) : null;
		}

		// 面板凭空建造入口（纳米/植物共用；指令前缀按种族区分，见 ExecutePanelBuild）
		public void EnterPanelBuildMode(string name, Texture2D icon, int size, Godot.Collections.Dictionary<ResourceType, float> costs, CreepType reqCreep)
		{
			EnterBuildMode(name, icon, size, costs, reqCreep);
			_panelBuildMode = true;
		}

		// 纳米虫面板技能：扩散菌毯（F 键同款）
		public void TriggerNanoSpread()
		{
			Vector2 m = GetMousePos();
			var cmd = NetAction.GroundCommand("NanoSpread", Main.Instance.LocalPlayerID, m);
			LockstepManager.Instance.SendAction(cmd);
		}

		// 面板技能点击的地面反馈标记
		public void SpawnSpreadClickFeedback(Vector2 pos, Color color = default)
		{
			_gameFX?.SpawnClickMarker(pos, color.A == 0f ? Colors.Purple : color);
		}

		private void HandleBuildInput(InputEvent @event)
		{
			if (@event.IsActionPressed("ui_cancel") || (@event is InputEventMouseButton m && m.ButtonIndex == MouseButton.Right))
			{
				ExitBuildMode();
				GetViewport().SetInputAsHandled();
				return;
			}
			if (@event is InputEventMouseButton release && release.ButtonIndex == MouseButton.Left && !release.Pressed)
			{
				_wallDragActive = false;
				return;
			}
			if (@event is InputEventMouseMotion motion && _wallDragActive && _panelBuildMode &&
				_activePreview != null && MapGrid.Instance != null)
			{
				Vector2I cur = MapGrid.Instance.WorldToGrid(_activePreview.AlignedWorldPos);
				if (cur != _wallLastCell)
				{
					PlaceWallLine(_wallLastCell, cur);
					_wallLastCell = cur;
				}
				GetViewport().SetInputAsHandled();
				return;
			}
			if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed && _activePreview?.CanPlace == true)
			{
				bool shift = Input.IsKeyPressed(Key.Shift);

				if (_panelBuildMode)
					ExecutePanelBuild();
				else
					ExecuteBuild(shift);

				// 树墙：按住左键拖拽连续生成一排，不退出建造模式
				if (_pendingName == "PlantTreeWall" && MapGrid.Instance != null)
				{
					_wallDragActive = true;
					_wallLastCell = MapGrid.Instance.WorldToGrid(_activePreview.AlignedWorldPos);
					GetViewport().SetInputAsHandled();
					return;
				}

				if (!shift) ExitBuildMode();
				GetViewport().SetInputAsHandled();
			}
		}

		// 树墙拖拽：从上一格到当前格沿 Bresenham 直线逐格放置
		private void PlaceWallLine(Vector2I from, Vector2I to)
		{
			int x0 = from.X;
			int y0 = from.Y;
			int x1 = to.X;
			int y1 = to.Y;
			int dx = Mathf.Abs(x1 - x0);
			int dy = -Mathf.Abs(y1 - y0);
			int sx = x0 < x1 ? 1 : -1;
			int sy = y0 < y1 ? 1 : -1;
			int err = dx + dy;
			bool first = true;

			while (true)
			{
				if (!first)
					ExecutePanelBuildWallAt(new Vector2I(x0, y0));
				first = false;
				if (x0 == x1 && y0 == y1)
					break;
				int e2 = 2 * err;
				if (e2 >= dy) { err += dy; x0 += sx; }
				if (e2 <= dx) { err += dx; y0 += sy; }
			}
		}

		private void ExecutePanelBuildWallAt(Vector2I gPos)
		{
			var grid = MapGrid.Instance;
			if (grid == null || _activePreview == null)
				return;

			var player = GetTargetPlayer();
			if (player == null)
				return;

			if (!grid.IsPositionAvailableForBlueprint(gPos, _pendingSize, _pendingReqCreep, player.TeamId))
				return;

			Vector2 spawnPos = grid.GetAlignedWorldPos(gPos, _pendingSize);
			var cmd = NetAction.GroundCommand(GetPanelBuildPrefix() + _pendingName, Main.Instance.LocalPlayerID, spawnPos);
			LockstepManager.Instance.SendAction(cmd);
		}

		private void ExecutePanelBuild()
		{
			var grid = MapGrid.Instance;
			if (grid == null || _activePreview == null)
				return;

			var player = GetTargetPlayer();
			if (player == null)
				return;

			Vector2I gPos = grid.GetTopLeftFromCenter(grid.WorldToGrid(_activePreview.AlignedWorldPos), _pendingSize);

			if (!grid.IsPositionAvailableForBlueprint(gPos, _pendingSize, _pendingReqCreep, player.TeamId))
				return;

			Vector2 spawnPos = grid.GetAlignedWorldPos(gPos, _pendingSize);
			var cmd = NetAction.GroundCommand(GetPanelBuildPrefix() + _pendingName, Main.Instance.LocalPlayerID, spawnPos);
			LockstepManager.Instance.SendAction(cmd);
		}

		// 面板建造指令前缀：纳米 = NanoBuild_，植物 = PlantBuild_（两族互不共用）
		private string GetPanelBuildPrefix()
		{
			var p = GetTargetPlayer();
			return p?.Race?.RaceName == "Plant" ? "PlantBuild_" : "NanoBuild_";
		}

		private void ExecuteBuild(bool queue)
		{
			var player = GetTargetPlayer();
			if (player == null)
				return;
			var grid = MapGrid.Instance;
			Vector2I gPos = grid.GetTopLeftFromCenter(grid.WorldToGrid(_activePreview.AlignedWorldPos), _pendingSize);

			if (!grid.IsPositionAvailableForBlueprint(gPos, _pendingSize, _pendingReqCreep, player.TeamId)) return;
			if (!player.PlayerData.HasResources(_pendingCosts)) return;

			var popCfg2 = RTS.Data.Configs.ConfigDatabase.GetStructure(_pendingName);
			// 蓝图建筑豁免人口检查（多足牧羊人蓝图满人口死锁）
			if (popCfg2 != null && popCfg2.SupplyUsed > 0 &&
				!_pendingName.StartsWith("Blueprint_") &&
				player.PlayerData.GetUsedSupply() + popCfg2.SupplyUsed > player.PlayerData.GetMaxSupply())
				return;

			Vector2 spawnPos = grid.GetAlignedWorldPos(gPos, _pendingSize);

			int[] entityIds = _selectedEntities.OfType<Unit>()
								.Where(u => u.TeamID == player.TeamId && u.LogicEntity != null)
								.Select(u => u.LogicEntity.ID).ToArray();

			if (entityIds.Length == 0) return;

			var cmd = NetAction.GroundCommand(
				"Build_" + _pendingName, player.TeamId, spawnPos, entityIds, -1, queue);
			LockstepManager.Instance.SendAction(cmd);
		}

		private void ExitBuildMode()
		{
			_activePreview?.QueueFree();
			_activePreview = null;
			_panelBuildMode = false;
			_wallDragActive = false;
		}

		private void OnUITriggered(string id)
		{
			// 旁观者点击动作按钮只查看，不发出任何指令
			if (RTS.Network.NetworkManager.Instance?.LocalIsObserver == true)
				return;
			if (_selectedEntities.Count == 0) return;

			// 雷达：先进入落点选择模式，左键点地面后发指令
			if (id == "Radar" && _selectedEntities[0] is Structure rc &&
				RTS.Data.Configs.ConfigDatabase.GetStructure(rc.StructureName) is { } radarCfg &&
				(radarCfg.PanelSkillMode & 1) != 0)
			{
				_radarPending = true;
				return;
			}

			// 轨道炮：先进入目标选择模式，左键点地面后发指令
			if (id == "OrbitalStrike" && _selectedEntities[0] is Structure oc &&
				RTS.Data.Configs.ConfigDatabase.GetStructure(oc.StructureName) is { } strikeCfg &&
				(strikeCfg.PanelSkillMode & 2) != 0)
			{
				_orbitalStrikePending = true;
				return;
			}

			// 洞穴面板技能：共振波（敌方单位）/ 地震波（地面）/ 制造虫洞（地面）
			if (id == "ResonanceWave" && _selectedEntities[0] is Structure rw &&
				RTS.Data.Configs.ConfigDatabase.GetStructure(rw.StructureName) is { } rwCfg &&
				(rwCfg.PanelSkillMode & 8) != 0)
			{
				_pendingHeroSkill = id;
				_pendingHeroSkillGround = false;
				return;
			}

			if (id == "SeismicWave" && _selectedEntities[0] is Structure sw &&
				RTS.Data.Configs.ConfigDatabase.GetStructure(sw.StructureName) is { } swCfg &&
				(swCfg.PanelSkillMode & 16) != 0)
			{
				_pendingHeroSkill = id;
				_pendingHeroSkillGround = true;
				return;
			}

			if (id == "CaveWormhole" && _selectedEntities[0] is Structure wc &&
				RTS.Data.Configs.ConfigDatabase.GetStructure(wc.StructureName) is { } wcCfg &&
				(wcCfg.PanelSkillMode & 32) != 0)
			{
				_pendingHeroSkill = id;
				_pendingHeroSkillGround = true;
				return;
			}

			// 花田 - 全能性：点地面在己方菌毯上生成叶犬
			if (id == "PlantOmni" && _selectedEntities[0] is Structure omni &&
				RTS.Data.Configs.ConfigDatabase.GetStructure(omni.StructureName) is { } omniCfg &&
				(omniCfg.PanelSkillMode & 64) != 0)
			{
				_pendingHeroSkill = id;
				_pendingHeroSkillGround = true;
				return;
			}

			// 植物：森林蔓延（建筑技能，以建筑为圆心定范围）
			if (id == "PlantForestSpread" && _selectedEntities[0] is Structure forestTree &&
				RTS.Data.Configs.ConfigDatabase.GetStructure(forestTree.StructureName) is { } treeCfg &&
				(treeCfg.PanelSkillMode & 128) != 0)
			{
				_pendingHeroSkill = id;
				_pendingHeroSkillGround = true;
				return;
			}

			// 沙虫：潜地（自施）/ 吞噬（敌方单位）
			if (id == "Burrow" && _selectedEntities[0] is Unit burrowUnit &&
				RTS.Data.Configs.ConfigDatabase.GetUnit(burrowUnit.UnitName) is { } burrowCfg &&
				burrowCfg.Skill1Kind == 6)
			{
				SendHeroSkill(id, null, Vector2.Zero);
				return;
			}

			if (id == "Devour" && _selectedEntities[0] is Unit devourUnit &&
				RTS.Data.Configs.ConfigDatabase.GetUnit(devourUnit.UnitName) is { } devourCfg &&
				devourCfg.Skill2Kind == 7)
			{
				_pendingHeroSkill = id;
				_pendingHeroSkillGround = false;
				return;
			}

			// 英雄技能：自施立即发，需要目标的进入选择模式
			if ((id == "HeroSkill1" || id == "HeroSkill2") && _selectedEntities[0] is Unit heroUnit)
			{
				var heroCfg = RTS.Data.Configs.ConfigDatabase.GetUnit(heroUnit.UnitName);
				if (heroCfg == null)
					return;

				int mode = id == "HeroSkill1" ? heroCfg.Skill1TargetMode : heroCfg.Skill2TargetMode;

				if (mode == 0)
				{
					SendHeroSkill(id, null, Vector2.Zero);
					return;
				}

				_pendingHeroSkill = id;
				_pendingHeroSkillGround = mode == 3;
				return;
			}

			// 电浆炮：点地面指定落点
		if (id == "PlasmaStrike" && _selectedEntities[0] is Unit plasmaUnit &&
			RTS.Data.Configs.ConfigDatabase.GetUnit(plasmaUnit.UnitName) is { } plasmaCfg &&
			plasmaCfg.Skill1Kind == 2)
		{
			_pendingHeroSkill = id;
			_pendingHeroSkillGround = true;
			return;
		}

		// 机动模式：点选后进入落点选择，左键点地面（30 格内）确认
		if (id == "CruiserMobility" && _selectedEntities[0] is Unit mobUnit &&
			RTS.Data.Configs.ConfigDatabase.GetUnit(mobUnit.UnitName) is { } mobCfg &&
			mobCfg.Skill1Kind == 5)
		{
			_pendingHeroSkill = id;
			_pendingHeroSkillGround = true;
			return;
		}

		// 泰伦解放者：架设需要指定范围圈
		if (id == "Deploy" && _selectedEntities[0] is Unit deployUnit &&
			RTS.Data.Configs.ConfigDatabase.GetUnit(deployUnit.UnitName) is { } deployCfg &&
			deployCfg.DeployRequiresTargetCircle)
		{
			_pendingHeroSkill = id;
			_pendingHeroSkillGround = true;
			return;
		}

		if (id.StartsWith("Build_") && _selectedEntities[0] is Unit u)
			{
				var act = u.Brain.GetAction<BuildAction>(id);
				if (act != null) EnterBuildMode(act.StructureName, act.Icon, act.GridSize, act.Costs, act.RequiredCreep);
				return;
			}

			int[] entityIds = _selectedEntities.Where(e => e.LogicEntity != null && e.TeamID == Main.Instance.LocalPlayerID)
											   .Select(e => e.LogicEntity.ID).ToArray();
			if (entityIds.Length == 0) return;

			var cmd = NetAction.GroundCommand(id, Main.Instance.LocalPlayerID, Vector2.Zero, entityIds);
			LockstepManager.Instance.SendAction(cmd);
		}

		private void ConfirmHeroSkill()
		{
			string id = _pendingHeroSkill;
			bool ground = _pendingHeroSkillGround;
			_pendingHeroSkill = "";
			_pendingHeroSkillGround = false;

			if (id.Length == 0)
				return;

			bool structureOrigin = _selectedEntities.OfType<Structure>().Any(s => s.LogicEntity != null);
			if (ground)
			{
				if (structureOrigin)
					SendStructureSkill(id, null, GetMousePos());
				else
					SendHeroSkill(id, null, GetMousePos());
				return;
			}

			var hit = WorldScanner.Raycast(GetTree(), GetMousePos(), Main.Instance.LocalPlayerID);
			if (structureOrigin)
				SendStructureSkill(id, hit, Vector2.Zero);
			else
				SendHeroSkill(id, hit, Vector2.Zero);
		}

		// 建筑面板技能指令（共振波/地震波/制造虫洞）：与英雄技能同构，施法者为选中建筑
		private void SendStructureSkill(string actionId, IEntity target, Vector2 groundPos)
		{
			var structure = _selectedEntities.OfType<Structure>().FirstOrDefault(s => s.LogicEntity != null);
			if (structure?.LogicEntity == null)
				return;

			var cmd = NetAction.GroundCommand(
				actionId, Main.Instance.LocalPlayerID, groundPos,
				new[] { structure.LogicEntity.ID }, target?.LogicEntity?.ID ?? -1);
			LockstepManager.Instance.SendAction(cmd);
			_gameFX?.SpawnClickMarker(groundPos == Vector2.Zero ? GetMousePos() : groundPos,
				new Color(0.85f, 0.65f, 0.2f, 1f));
		}

		private void SendHeroSkill(string actionId, IEntity target, Vector2 groundPos)
		{
			var hero = _selectedEntities.OfType<Unit>().FirstOrDefault(u => u.LogicEntity != null);

			if (hero?.LogicEntity == null)
				return;

			var cmd = NetAction.GroundCommand(
				actionId, Main.Instance.LocalPlayerID, groundPos,
				new[] { hero.LogicEntity.ID }, target?.LogicEntity?.ID ?? -1);
			LockstepManager.Instance.SendAction(cmd);
		}

		private void SendOrbitalStrike()
		{
			_orbitalStrikePending = false;

			var structure = RTS.Core.WorldScanner.FindOwnedOrbitalControl(GetTree(), Main.Instance?.LocalPlayerID ?? 0);
			if (structure?.LogicEntity == null)
				return;

			Vector2 m = GetMousePos();
			var cmd = NetAction.GroundCommand(
				"OrbitalStrike", Main.Instance.LocalPlayerID, m, new[] { structure.LogicEntity.ID });
			LockstepManager.Instance.SendAction(cmd);
			_gameFX?.SpawnClickMarker(m, new Color(1f, 0.5f, 0.15f, 1f));
		}

		private void SendRadar()
		{
			_radarPending = false;

			var structure = RTS.Core.WorldScanner.FindOwnedOrbitalControl(GetTree(), Main.Instance?.LocalPlayerID ?? 0);
			if (structure?.LogicEntity == null)
				return;

			Vector2 m = GetMousePos();
			var cmd = NetAction.GroundCommand(
				"Radar", Main.Instance.LocalPlayerID, m, new[] { structure.LogicEntity.ID });
			LockstepManager.Instance.SendAction(cmd);
			_gameFX?.SpawnClickMarker(m, new Color(0.3f, 0.9f, 1f, 1f));
		}

		public void EnterRadarPending()
		{
			_radarPending = true;
		}

		public void EnterOrbitalStrikePending()
		{
			_orbitalStrikePending = true;
		}


		private void SendCheatResources()
		{
			var cmd = NetAction.GroundCommand("CheatAddResources", Main.Instance.LocalPlayerID, Vector2.Zero);
			LockstepManager.Instance.SendAction(cmd);
			GD.Print("[Cheat] 已发送刷资源指令（F11）");
		}

		private void SendCheatToggle(string actionId, string label)
		{
			var cmd = NetAction.GroundCommand(actionId, Main.Instance.LocalPlayerID, Vector2.Zero);
			LockstepManager.Instance.SendAction(cmd);
			GD.Print($"[Cheat] 已切换 {label}");
		}

		// 鼠标射线与 Y=0 地面（逻辑 XZ 平面）的交点
	private Vector2 GetMousePos()
	{
		Camera3D cam = GetViewport().GetCamera3D();
		if (cam == null)
			return Vector2.Zero;

			Vector2 screenPos = GetViewport().GetMousePosition();
			Vector3 origin = cam.ProjectRayOrigin(screenPos);
			Vector3 dir = cam.ProjectRayNormal(screenPos);

			if (Mathf.IsZeroApprox(dir.Y))
				return Vector2.Zero;

			float t = -origin.Y / dir.Y;
		Vector3 point = origin + dir * t;
		return new Vector2(point.X, point.Z);
	}

	public Vector2 GetMouseWorldPos() => GetMousePos();

	// 所有带射程/落点半径的技能，在待确认阶段显示范围圈
	private void UpdateSkillRangeIndicators()
	{
		var ind = SkillRangeIndicator.GetOrCreate(GetTree());
		Vector2 mouse = GetMousePos();

		// 雷达：落点效果圈（直径 20 格 = 半径 10 格）
		if (_radarPending)
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure("OrbitalControl");
			if (cfg != null && cfg.RadarRadiusTiles > 0)
			{
				ind.HideRanges();
				ind.ShowEffectRange(new Vector3(mouse.X, 0f, mouse.Y), cfg.RadarRadiusTiles * 64f, new Color(0.3f, 0.9f, 1f, 0.5f));
				return;
			}
		}

		// 轨道炮：落点效果圈
		if (_orbitalStrikePending)
		{
			var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure("OrbitalControl");
			if (cfg != null && cfg.StrikeRadiusTiles > 0)
			{
				ind.HideRanges();
				ind.ShowEffectRange(new Vector3(mouse.X, 0f, mouse.Y), cfg.StrikeRadiusTiles * 64f, new Color(1f, 0.55f, 0.2f, 0.5f));
				return;
			}
		}

		// 纳米虫扩散：落点效果圈
		var nanoPanel = GetTree().Root.FindChild("RacePanel_Nano", true, false) as NanoPanelUI;
		if (nanoPanel != null && nanoPanel.IsSpreadPending)
		{
			var world = SimManager.Instance?.World;
			if (world != null && (float)world.CarpetSpreadRadiusTiles > 0f)
			{
				ind.HideRanges();
				ind.ShowEffectRange(
					new Vector3(mouse.X, 0f, mouse.Y),
					(float)world.CarpetSpreadRadiusTiles * 64f,
					new Color(0.75f, 0.3f, 1f, 0.45f));
				return;
			}
		}

		// 洞穴面板技能：地震波 / 制造虫洞 的落点效果圈
		if (_pendingHeroSkill == "SeismicWave" || _pendingHeroSkill == "CaveWormhole")
		{
			var structure = _selectedEntities.OfType<Structure>().FirstOrDefault();
			if (structure != null)
			{
				var cfg = RTS.Data.Configs.ConfigDatabase.GetStructure(structure.StructureName);
				if (cfg != null)
				{
					ind.HideRanges();
					float radius = _pendingHeroSkill == "SeismicWave"
						? cfg.WaveRadiusTiles * 64f
						: 96f;
					ind.ShowEffectRange(new Vector3(mouse.X, 0f, mouse.Y), radius, new Color(0.85f, 0.65f, 0.2f, 0.45f));
					return;
				}
			}
		}

		// 植物：森林蔓延——以施法建筑为圆心显示施法范围圈
			if (_pendingHeroSkill == "PlantForestSpread")
			{
				var tree = _selectedEntities.OfType<Structure>().FirstOrDefault(s => s.LogicEntity != null);
				if (tree?.LogicEntity != null)
				{
					var center = new Vector3((float)tree.LogicEntity.Position.X, 0f, (float)tree.LogicEntity.Position.Y);
					var treeCfg = RTS.Data.Configs.ConfigDatabase.GetStructure(tree.StructureName);
					int rangeTiles = treeCfg?.ForestSpreadRangeTiles ?? 12;
					ind.HideRanges();
					ind.ShowCastRange(center, rangeTiles * 64f, new Color(0.3f, 0.9f, 0.4f, 0.5f));
					return;
				}
			}

		// 英雄技能/电浆炮/机动模式：施法者射程圈（落点类技能额外显示鼠标处效果圈）
		if (_pendingHeroSkill.Length > 0)
		{
			var unit = _selectedEntities.OfType<Unit>().FirstOrDefault(u => u.LogicEntity != null);
			if (unit != null)
			{
				var cfg = RTS.Data.Configs.ConfigDatabase.GetUnit(unit.UnitName);
				if (cfg != null)
				{
					string id = _pendingHeroSkill;
					int rangeTiles = id switch
					{
						"HeroSkill1" => cfg.Skill1RangeTiles,
						"HeroSkill2" => cfg.Skill2RangeTiles,
						"Devour" => cfg.Skill2RangeTiles,
						"PlasmaStrike" => cfg.PlasmaAutoTargetRangeTiles,
						"CruiserMobility" => cfg.MobilityRangeTiles,
						"Deploy" => (int)Mathf.Ceil(GetMaxWeaponRangeTiles(unit)),
						_ => 0
					};

					var center = new Vector3((float)unit.LogicEntity.Position.X, 0f, (float)unit.LogicEntity.Position.Y);
					ind.HideRanges();

					if (rangeTiles > 0)
						ind.ShowCastRange(center, rangeTiles * 64f, new Color(0.35f, 0.85f, 1f, 0.5f));

					if (id == "PlasmaStrike")
					{
						var pw = RTS.Data.Configs.ConfigDatabase.GetWeapon("PlasmaArtillery");
						float effect = pw?.ImpactRadiusTiles ?? 2f;
						ind.ShowEffectRange(new Vector3(mouse.X, 0f, mouse.Y), effect * 64f, new Color(1f, 0.55f, 0.2f, 0.5f));
					}

					if (id == "Deploy")
					{
						float circle = cfg.DeployTargetCircleTiles * 64f;
						ind.ShowEffectRange(new Vector3(mouse.X, 0f, mouse.Y), circle, new Color(0.85f, 0.5f, 1f, 0.45f));
					}

					return;
				}
			}
		}

		ind.HideRanges();
	}

	// 单位最远武器射程（格）
	private float GetMaxWeaponRangeTiles(Unit unit)
	{
		float best = 0f;
		if (unit?.CombatModule != null)
		{
			foreach (var w in unit.CombatModule.Weapons)
				best = Mathf.Max(best, w.AttackRange);
		}
		return best / 64f;
	}

	// 正交俯视相机的可视世界矩形（XZ 平面）
	private Rect2 GetScreenWorldRect()
		{
			Camera3D cam = GetViewport().GetCamera3D();
			if (cam == null)
				return new Rect2();

			// 倾斜视角下用屏幕四角射线打地面，取 AABB 作为同类型选择范围
			Rect2 vp = GetViewport().GetVisibleRect();
			Vector2[] corners =
			{
				new Vector2(vp.Position.X, vp.Position.Y),
				new Vector2(vp.End.X, vp.Position.Y),
				new Vector2(vp.Position.X, vp.End.Y),
				vp.End
			};

			Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
			Vector2 max = new Vector2(float.MinValue, float.MinValue);

			foreach (var corner in corners)
			{
				Vector3 origin = cam.ProjectRayOrigin(corner);
				Vector3 dir = cam.ProjectRayNormal(corner);
				if (Mathf.IsZeroApprox(dir.Y))
					continue;

				float t = -origin.Y / dir.Y;
				Vector3 point = origin + dir * t;
				min.X = Mathf.Min(min.X, point.X);
				min.Y = Mathf.Min(min.Y, point.Z);
				max.X = Mathf.Max(max.X, point.X);
				max.Y = Mathf.Max(max.Y, point.Z);
			}

			return new Rect2(min, max - min);
		}

		private Color GetClickColor(IEntity target, bool shift) =>
			target != null && target.TeamID != Main.Instance.LocalPlayerID ? Colors.Red : (shift ? Colors.Yellow : Colors.Green);
	}
}
