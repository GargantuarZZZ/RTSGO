using System.Collections.Generic;
using Godot;

namespace RTS.Actions
{
	// 纯逻辑类，不继承 Node
	public class ActionQueueManager
	{
		private Queue<OrderData> _queue = new();

		// 获取队列长度
		public int Count => _queue.Count;

		// 基础队列操作
		public void Enqueue(OrderData order) => _queue.Enqueue(order);
		public OrderData Dequeue() => _queue.Dequeue();
		public OrderData Peek() => _queue.Peek();
		public void Clear() => _queue.Clear();

		// 获取快照，用于 UI 遍历或路径预览
		public List<OrderData> GetSnapshot() => new List<OrderData>(_queue);

		// 核心封装：移除指定索引的指令（用于玩家中途取消排队）
		// 返回 bool 表示是否移除成功，通过 out 返回被移除的数据以便退款
		public bool TryRemoveAt(int index, out OrderData removedOrder)
		{
			removedOrder = default;
			var list = new List<OrderData>(_queue);

			if (index >= 0 && index < list.Count)
			{
				removedOrder = list[index];
				list.RemoveAt(index);

				// 重新构建队列
				_queue.Clear();
				foreach (var item in list) _queue.Enqueue(item);
				return true;
			}
			return false;
		}
	}
}
