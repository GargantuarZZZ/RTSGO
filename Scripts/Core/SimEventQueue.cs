using System.Collections.Concurrent;
using System;

namespace RTS.Core
{
	public enum SimEventType
	{
		HealthChanged,
		Died,
		ResourceAmountChanged,
		ResourceDepleted,
	}

	/// <summary>
	/// 模拟线程 → 主线程的事件队列。
	/// 锁步模拟不允许碰 Godot 对象（信号/节点），所有跨层事件先入队，
	/// 由主线程在 SimManager._Process 末尾统一派发。
	/// </summary>
	public readonly struct SimEvent
	{
		public readonly SimEventType Type;
		public readonly int EntityId;
		public readonly float A;
		public readonly float B;

		public SimEvent(SimEventType type, int entityId, float a = 0f, float b = 0f)
		{
			Type = type;
			EntityId = entityId;
			A = a;
			B = b;
		}
	}

	public static class SimEventQueue
	{
		private static readonly ConcurrentQueue<SimEvent> _queue = new();
		private static readonly ConcurrentQueue<Action> _mainActions = new();

		/// <summary>
		/// 把一段“只碰 Godot 视觉/节点”的代码延迟到主线程执行。
		/// 模拟线程禁止直接调用，必须用这个入口入队（闭包内只捕获值类型/纯数据）。
		/// </summary>
		public static void EnqueueMain(Action action)
		{
			_mainActions.Enqueue(action);
		}

		public static bool TryDequeueMain(out Action action)
		{
			return _mainActions.TryDequeue(out action);
		}

		public static void Enqueue(in SimEvent e)
		{
			_queue.Enqueue(e);
		}

		public static bool TryDequeue(out SimEvent e)
		{
			return _queue.TryDequeue(out e);
		}

	}
}
