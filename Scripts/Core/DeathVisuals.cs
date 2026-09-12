using Godot;

namespace RTS.Core
{
	/// <summary>死亡视觉唯一实现：单位与建筑共用（灰化/下沉/延迟释放）。</summary>
	public static class DeathVisuals
	{
		/// <summary>在主线程播放死亡动画：视觉回调先执行，然后下沉 2.6 秒并延迟释放。</summary>
		public static void PlaySinkAndFree(Node3D node, float extraSink, System.Action onStart)
		{
			// 视觉/节点操作延迟到主线程：自爆/献祭可能在模拟线程触发
			SimEventQueue.EnqueueMain(() =>
			{
				if (!GodotObject.IsInstanceValid(node))
					return;

				onStart?.Invoke();
				if (node is RTS.Units.Unit || node is RTS.Units.Structure && node is not RTS.Units.ResourceStructure)
					GameAudio.Instance?.PlayAt("blast", node.GlobalPosition);

				var tween = node.CreateTween();
				if (tween == null)
					return;

				float sinkDepth = 140f + extraSink;
				tween.TweenProperty(node, "global_position:y", node.GlobalPosition.Y - sinkDepth, 2.6f)
					.SetTrans(Tween.TransitionType.Sine)
					.SetEase(Tween.EaseType.In);
				tween.TweenInterval(0.4f);
				// 释放延迟到派发屏障执行，避免 tick 期间释放节点导致 AccessViolation
				tween.TweenCallback(Callable.From(() => SimEventQueue.EnqueueMain(() =>
				{
					if (GodotObject.IsInstanceValid(node))
						node.QueueFree();
				})));
			});
		}
	}
}
