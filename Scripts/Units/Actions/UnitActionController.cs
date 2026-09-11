// File: res://Scripts/Units/Actions/UnitActionController.cs
using Godot;
using System.Collections.Generic;
using RTS.Data;
using RTS.Actions.Implementation;
using FP = FixMath.NET.Fix64;

namespace RTS.Actions
{
	// --- [核心升级 1] 定义指令数据包 ---
	public struct OrderData
{
	public string ActionName;
	public RTS.Simulation.FPVector2 TargetPos; // 定点数坐标
	public IEntity TargetObj;
}

	[GlobalClass]
	public partial class UnitActionController : Node
	{
		private IEntity _owner;
		private List<UnitAction> _activeActions = new();
		// 托管动作列表：延迟回调里不再遍历原生子节点。
		// 直接遍历 GetChildren() 可能拿到“原生已释放、指针被复用”的过期子节点，
		// 读 Name 时触发 AccessViolation（4AI 对局闪退的另一个来源）。
		private readonly List<UnitAction> _registeredActions = new();
		// 动作节点缓存：模拟线程禁止 GetNodeOrNull，初始化时一次性缓存
		private readonly Dictionary<string, UnitAction> _actionCache = new();
		// 动作列表/缓存可能被模拟线程（研究完成加动作）与主线程（RefreshActionCache）并发访问，必须加锁
		private readonly object _actionLock = new();

		// 大脑持有者（动作面板/生产队列用来反查所属实体）
		public IEntity EntityOwner => _owner;

		// --- [核心升级 2] 队列改为使用 ActionQueueManager ---
		private ActionQueueManager _bodyQueue = new();
		private ActionQueueManager _productionQueue = new();

		public void Initialize(IEntity owner)
		{
			_owner = owner;
			lock (_actionLock)
			{
				foreach (Node child in GetChildren())
				{
					if (child is UnitAction action)
					{
						action.Initialize(owner);
						action.OnFinished += OnActionFinishedCallback;
						_registeredActions.Add(action);
					}
				}
			}
			RefreshActionCache();
		}

		private UnitAction GetActionNode(string name)
		{
			lock (_actionLock)
			{
				if (_actionCache.TryGetValue(name, out var cached))
				{
					if (GodotObject.IsInstanceValid(cached))
						return cached;
					_actionCache.Remove(name);
				}
			}
			// 模拟线程禁止 GetNodeOrNull：请求主线程刷新缓存（本次指令若真缺失会被丢弃一次）
			// 必须带 IsInstanceValid 守卫：单位可能在请求发出后死亡释放，
			// 直接 EnqueueMain(RefreshActionCache) 会在已释放的节点上
			// 调用 GetChildren() → ObjectDisposedException → 4AI 对局闪退。
			RTS.Core.SimEventQueue.EnqueueMain(() =>
			{
				if (GodotObject.IsInstanceValid(this))
					RefreshActionCache();
			});
			return null;
		}

		/// <summary>主线程调用：重建动作缓存（生成/科技加动作后必须刷新，模拟线程禁止 GetNodeOrNull）。</summary>
		public void RefreshActionCache()
		{
			lock (_actionLock)
			{
				_actionCache.Clear();
				for (int i = _registeredActions.Count - 1; i >= 0; i--)
				{
					var action = _registeredActions[i];
					if (!GodotObject.IsInstanceValid(action))
					{
						_registeredActions.RemoveAt(i);
						continue;
					}
					// 只用 C# 属性 ActionName 做键——action.Name 是 Godot 节点名，
					// 节点释放后读取会在 native 层抛 ObjectDisposed/Handle 异常
					// （长局闪退的 [SimEvent] 主线程延迟动作异常来源）。
					if (!string.IsNullOrEmpty(action.ActionName))
						_actionCache[action.ActionName] = action;
				}
			}
		}


		public void LogicTick(double delta)
		{
			for (int i = _activeActions.Count - 1; i >= 0; i--)
				_activeActions[i].OnUpdate(delta);
			MirrorBehaviorToSimUnit();
		}

		// P0-1 里程碑 3：把当前行为（动作名/目标实体）镜像进 SimUnit，纳入确定性哈希。
		// 只写定点数/字符串，不碰 Godot API。
		private void MirrorBehaviorToSimUnit()
		{
			if (_owner?.LogicEntity is not RTS.Simulation.SimUnit su)
				return;

			UnitAction primary = null;
			foreach (var act in _activeActions)
			{
				if ((act.Layer & (ActionLayer.Body | ActionLayer.Movement)) != 0)
				{
					primary = act;
					break;
				}
			}

			su.ActiveActionName = primary?.ActionName ?? "";
		}

		public UnitAction GetActiveProductionAction()
		{
			foreach (var action in _activeActions)
			{
				if ((action.Layer & ActionLayer.Production) != 0)
					return action;
			}
			return null;
		}

		/// <summary>排队中的生产任务数（模拟线程可读，纯 List.Count）。</summary>
		public int GetProductionQueueCount() => _productionQueue.Count;

		// 当前正在建造的目标（建造光束用）
		public IEntity GetActiveBuildTarget()
		{
			foreach (var action in _activeActions)
			{
				if (action is RTS.Actions.Implementation.BuildAction b && b.TargetStructure != null)
					return b.TargetStructure;
			}

			return null;
		}

		// 当前正在采集的目标（采矿光束用）
		public IEntity GetActiveHarvestSource()
		{
			foreach (var action in _activeActions)
			{
				if (action is RTS.Actions.Implementation.HarvestAction h && h.SourceEntity != null)
					return h.SourceEntity;
			}

			return null;
		}

		// [UI接口] 获取排队中的动作列表 (适配 ActionQueueManager)
		public UnitAction[] GetProductionQueueSnapshot()
		{
			List<UnitAction> list = new();
			foreach (var order in _productionQueue.GetSnapshot())
			{
				// 模拟线程禁止 GetNodeOrNull：一律走缓存
				var node = GetActionNode(order.ActionName);
				if (node != null) list.Add(node);
			}
			return list.ToArray();
		}

		// --- [核心升级 3] 支持参数传递的 StartAction ---
		public void StartAction(string actionName, bool isAuto = false, bool isQueue = false)
		{
			StartAction(actionName, RTS.Simulation.FPVector2.Zero, null, isAuto, isQueue);
		}

		public void StartAction(string actionName, RTS.Simulation.FPVector2 targetPos, IEntity targetObj, bool isAuto = false, bool isQueue = false)
		{
			var actionNode = GetActionNode(actionName);
			if (actionNode == null || !GodotObject.IsInstanceValid(actionNode)) return;

			if (!isAuto && !actionNode.TryPayCost())
			{
				return;
			}

			bool isProd = (actionNode.Layer & ActionLayer.Production) != 0;
			var tQueue = isProd ? _productionQueue : _bodyQueue;

			if (!isAuto && isProd && (tQueue.Count > 0 || IsLayerActive(ActionLayer.Production)))
			{
				isQueue = true;
			}

			if (isQueue && actionNode.Queueable)
			{
				tQueue.Enqueue(new OrderData { ActionName = actionName, TargetPos = targetPos, TargetObj = targetObj });
				TryProcessSpecificQueue(tQueue, isProd);
				return;
			}

			if (!isAuto && !isQueue)
			{
				StopConflicts(actionNode);
				tQueue.Clear();
			}

			actionNode.Setup(targetPos, targetObj);
			TryStartImmediate(actionNode, isAuto);
		}

		private void TryStartImmediate(UnitAction newAction, bool isAuto)
		{
			if (!newAction.CanExecute()) return;

			if (CheckConflict(newAction, out var conflicts))
			{
				if (!isAuto)
				{
					bool blocked = false;

					foreach (var old in conflicts)
					{
						if (!old.CanBeInterrupted)
						{
							// 存在不可打断技能时，新指令直接被拒，技能继续
							blocked = true;
							continue;
						}

						StopAction(old);
					}

					if (blocked)
						return;
				}
				else
				{
					return; // 自动指令被阻塞
				}
			}
			ForceExecute(newAction);
		}

		private void OnActionFinishedCallback(UnitAction action)
		{
			StopAction(action);

			bool isProduction = (action.Layer & ActionLayer.Production) != 0;

			if (isProduction)
			{
				TryProcessSpecificQueue(_productionQueue, true);
			}
			else
			{
				TryProcessSpecificQueue(_bodyQueue, false);

				if (!IsLayerActive(ActionLayer.Body) && !IsLayerActive(ActionLayer.Movement))
				{
					StartAction("Idle", RTS.Simulation.FPVector2.Zero, null, true);
				}
			}
		}

		// --- [核心升级 4] 处理队列时还原参数 ---
		// 注意这里参数改为了 ActionQueueManager
		private void TryProcessSpecificQueue(ActionQueueManager queue, bool isProduction)
		{
			if (queue.Count > 0)
			{
				var nextOrder = queue.Peek();
				var nextAction = GetActionNode(nextOrder.ActionName);

				if (nextAction != null)
				{
					bool hasConflict = CheckConflict(nextAction, out var conflicts);

					bool blockedByIdleOnly = true;
					foreach(var c in conflicts) {
						if (c.ActionName != "Idle") { blockedByIdleOnly = false; break; }
					}

					if (!hasConflict || blockedByIdleOnly)
					{
						if (hasConflict && blockedByIdleOnly)
						{
							foreach(var c in conflicts) StopAction(c);
						}
						queue.Dequeue();
						nextAction.Setup(nextOrder.TargetPos, nextOrder.TargetObj);
						// 因为只要是从队列里拿出来的任务，在入队前就已经付过钱了。
						TryStartImmediate(nextAction, true);
					}
				}
				else if (nextAction == null)
				{
					queue.Dequeue();
					TryProcessSpecificQueue(queue, isProduction);
				}
			}
		}

		// --- [新增] 队列取消与退款逻辑 ---
		public void CancelProduction(int uiIndex)
		{
			var active = GetActiveProductionAction();
			bool hasActive = active != null;

			// 情况 1: 取消的是正在生产的第一个任务
			if (uiIndex == 0 && hasActive)
			{
				RefundProduction(active.ActionName);
				StopAction(active);
				TryProcessSpecificQueue(_productionQueue, true);

				return;
			}

			// 情况 2: 取消的是排队中的任务
			int queueIndex = hasActive ? uiIndex - 1 : uiIndex;
			if (_productionQueue.TryRemoveAt(queueIndex, out var removedOrder))
			{
				RefundProduction(removedOrder.ActionName);
			}
		}

		public void CancelLastProduction()
		{
			int totalCount = _productionQueue.Count + (GetActiveProductionAction() != null ? 1 : 0);
			if (totalCount > 0)
			{
				CancelProduction(totalCount - 1);
			}
		}

		private void RefundProduction(string actionName)
		{
			var actionNode = GetNodeOrNull<UnitAction>(actionName);
			if (actionNode is TrainUnitAction trainAction)
			{
				var player = RTS.World.Game.GetPlayerByTeam(_owner.TeamID);
				if (player != null && player.PlayerData != null)
				{
					// 批量退款
					player.PlayerData.AddResources(trainAction.Costs);
				}
			}
		}

		private bool IsLayerActive(ActionLayer layerMask)
		{
			foreach(var act in _activeActions)
			{
				if ((act.Layer & layerMask) != 0) return true;
			}
			return false;
		}

		private void StopConflicts(UnitAction newAction)
		{
			ActionLayer newImpact = newAction.Layer | newAction.BlockingLayers;
			List<UnitAction> toStop = new();
			foreach (var active in _activeActions)
			{
				ActionLayer oldImpact = active.Layer | active.BlockingLayers;
				if ((newImpact & oldImpact) != ActionLayer.None && active.CanBeInterrupted)
					toStop.Add(active);
			}
			foreach (var action in toStop) StopAction(action);
		}

		private bool CheckConflict(UnitAction newAction, out List<UnitAction> conflicts)
		{
			conflicts = new List<UnitAction>();
			ActionLayer newImpact = newAction.Layer | newAction.BlockingLayers;
			foreach (var active in _activeActions)
			{
				ActionLayer oldImpact = active.Layer | active.BlockingLayers;
				if ((newImpact & oldImpact) != ActionLayer.None) conflicts.Add(active);
			}
			return conflicts.Count > 0;
		}

		private void ForceExecute(UnitAction action)
		{
			if (!_activeActions.Contains(action)) {
				_activeActions.Add(action);
				action.OnEnter();
			}
		}

		public void StopAction(UnitAction action)
		{
			if (_activeActions.Contains(action)) {
				action.OnExit();
				_activeActions.Remove(action);
			}
		}

		public void StopAll()
		{
			for (int i = _activeActions.Count - 1; i >= 0; i--) StopAction(_activeActions[i]);
			_bodyQueue.Clear();
			_productionQueue.Clear();
		}

		public T GetAction<T>(string name) where T : UnitAction => GetActionNode(name) as T;

		// 模拟线程安全：只查缓存 key，不触碰 Godot 对象（AI 索敌/派工用）
		public bool HasCachedAction(string name)
		{
			return _actionCache.ContainsKey(name);
		}

		// [预览路径升级] 支持从快照读取坐标
		// 找到 UnitActionController.cs 约 400 行处的 GetPreviewPathPoints 方法

		public List<Vector2> GetPreviewPathPoints()
		{
			List<Vector2> points = new();

			// 1. 当前正在执行的动作
			foreach (var act in _activeActions)
			{
				// MoveAction 使用 TargetPosFP
				if (act is MoveAction m)
					points.Add(m.TargetPosFP.ToGodotVector2());
				else if (act is BuildAction b)
					points.Add(b.GetTargetPos());
			}

			// 2. 队列里的任务
			foreach (var order in _bodyQueue.GetSnapshot())
			{
				// 队列里存的是原始 Vector2，直接添加
				if (order.ActionName == "Move" || order.ActionName.StartsWith("Build_"))
					points.Add(order.TargetPos.ToGodotVector2());
			}
			return points;
		}

		// =========================================================
		// 确定性状态哈希：用于脱同步检测（生产计时器 / 双队列内容）
		// 禁止使用 HashCode（跨进程随机化），统一 FNV-1a
		// =========================================================

		public long GetDeterministicStateHash()
		{
			long hash = 1469598103934665603L;

			foreach (var act in _activeActions)
			{
				MixString(ref hash, act.ActionName);

				if (act is TrainUnitAction t)
				{
					Mix(ref hash, t.GetTimerRaw1000());
				}

				// 技能/攻击/建造/采集等动作自身的确定性状态
				Mix(ref hash, act.GetDeterministicExtraHash());
			}

			MixQueue(ref hash, _bodyQueue);
			MixQueue(ref hash, _productionQueue);

			return hash;
		}

		private static void MixQueue(ref long hash, ActionQueueManager queue)
		{
			foreach (var order in queue.GetSnapshot())
			{
				MixString(ref hash, order.ActionName);
				Mix(ref hash, (long)(order.TargetPos.X * (FP)1000m));
				Mix(ref hash, (long)(order.TargetPos.Y * (FP)1000m));
				Mix(ref hash, order.TargetObj?.LogicEntity?.ID ?? -1);
			}
		}

		private static void MixString(ref long hash, string value)
		{
			if (value == null)
				return;

			foreach (char c in value)
				Mix(ref hash, c);
		}

		private static void Mix(ref long hash, long value)
		{
			unchecked
			{
				hash ^= value;
				hash *= 1099511628211L;
			}
		}

		// 脱同步报告用：当前动作/队列快照
		public string GetDebugSnapshot()
		{
			var sb = new System.Text.StringBuilder();

			sb.Append("Active[");
			foreach (var act in _activeActions)
			{
				sb.Append(act.ActionName);
				if (act is TrainUnitAction t)
					sb.Append($"({(int)(t.GetProgress() * 100)}%)");
				sb.Append(' ');
			}
			sb.Append("] BodyQueue[");

			foreach (var order in _bodyQueue.GetSnapshot())
			{
				sb.Append(order.ActionName).Append(' ');
			}
			sb.Append("] ProdQueue[");

			foreach (var order in _productionQueue.GetSnapshot())
			{
				sb.Append(order.ActionName).Append(' ');
			}
			sb.Append(']');

			return sb.ToString();
		}
	}
}
