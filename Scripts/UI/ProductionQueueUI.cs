// File: res://Scripts/UI/ProductionQueueUI.cs
using Godot;
using System.Collections.Generic;
using RTS.Actions;
using RTS.Actions.Implementation;
using RTS.Data;
using RTS.Data.Configs;
using RTS.Network;
using RTS.Units;
using RTS.World;
using RTS.Core.Events;
namespace RTS.Core
{
	public partial class ProductionQueueUI : PanelContainer
	{
		// 使用 GridContainer 布局
		[Export] public GridContainer IconContainer;
		[Export] public PackedScene QueueIconPrefab;

		[Export] public Vector2 IconSize = new Vector2(30, 30);

		private UnitActionController _currentBrain;

		public override void _Ready()
		{
			GameEventBus.SelectionChanged += OnBusSelectionChanged;
			// 引用缺失时禁用自身，避免空引用报错
			if (IconContainer == null || QueueIconPrefab == null)
			{
				GD.PrintErr("[UI] 生产队列 UI 引用丢失：请在 Inspector 中绑定 IconContainer 和 QueueIconPrefab");
				SetProcess(false);
				Visible = false;
				return;
			}
			Visible = false;
		}

		public override void _ExitTree()
		{
			GameEventBus.SelectionChanged -= OnBusSelectionChanged;
			base._ExitTree();
		}

		private void OnBusSelectionChanged(System.Collections.Generic.List<IEntity> selection, int team)
		{
			Setup(selection != null && selection.Count > 0 ? selection[0] : null);
		}

		public void Setup(IEntity entity)
		{
			bool isValid = entity != null && entity.Brain != null && entity.TeamID == Main.Instance.LocalPlayerID;
			_currentBrain = isValid ? entity.Brain : null;
			Visible = isValid;
			SetProcess(isValid);
			if (!isValid) ClearQueue();
		}

		public override void _Process(double delta)
		{
			// 防御：选中单位/建筑已销毁时避免访问已释放对象
			if (_currentBrain == null || !GodotObject.IsInstanceValid(_currentBrain))
			{
				_currentBrain = null;
				return;
			}

			// 1. 准备数据
			var active = _currentBrain.GetActiveProductionAction();
			var queue = _currentBrain.GetProductionQueueSnapshot();
			List<UnitAction> actions = new();

			if (active != null) actions.Add(active);
			if (queue != null) actions.AddRange(queue);

			// 科技研究进度：选中可研究建筑且正在研究时，显示在最前面
			var research = GetResearchProgress();
			int researchOffset = research != null ? 1 : 0;
			int targetCount = actions.Count + researchOffset;
			int currentCount = IconContainer.GetChildCount();

			// 2. 同步节点数量 (多退少补)
			// A. 补齐
			for (int i = currentCount; i < targetCount; i++)
			{
				// 将根节点实例化为 Button
				var btn = QueueIconPrefab.Instantiate<Button>();

				btn.CustomMinimumSize = IconSize;
				btn.Size = IconSize;

				// 绑定 Pressed 信号以支持点击取消
				btn.Pressed += () =>
				{
					if (btn.Disabled)
						return;

					int index = btn.GetIndex() - researchOffset;
					// 获取该生产建筑的逻辑 ID
					if (_currentBrain?.EntityOwner is IEntity entity && entity.LogicEntity != null)
					{
			var cmd = NetAction.GroundCommand(
				"CancelProduction", Main.Instance.LocalPlayerID, Vector2.Zero,
				new[] { entity.LogicEntity.ID }, index);
						LockstepManager.Instance.SendAction(cmd);
					}
				};

				IconContainer.AddChild(btn);
			}

			// B. 删除
			for (int i = currentCount - 1; i >= targetCount; i--)
			{
				IconContainer.GetChild(i).Free();
			}

			// 3. 刷新显示
			for (int i = 0; i < targetCount; i++)
			{
				bool isResearch = research != null && i == 0;
				var action = isResearch ? null : actions[i - researchOffset];
				var btn = IconContainer.GetChild(i) as Button;

				if (btn != null) btn.CustomMinimumSize = IconSize;

				// --- 依然保留 TextureRect 负责显示图片（防拉伸逻辑不受影响） ---
				var iconRect = btn.GetNodeOrNull<TextureRect>("Icon");
				if (iconRect != null)
				{
					iconRect.Texture = isResearch ? research.Value.Icon : action.Icon;
					iconRect.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
					iconRect.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
					iconRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
				}

				// --- 设置进度条 ---
				var bar = btn.GetNodeOrNull<ProgressBar>("ProgressBar");
				if (bar != null)
				{
					if (isResearch)
					{
						bar.Visible = true;
						bar.Value = research.Value.Progress;
					}
					else
					{
						bool isFirst = (i == researchOffset && active != null);
						bar.Visible = isFirst;
						if (isFirst)
							bar.Value = action is TrainUnitAction t ? t.GetProgress() * 100 : 0;
					}
				}

				if (btn != null)
				{
					btn.Disabled = isResearch;
					btn.TooltipText = isResearch ? research.Value.Name : "";
				}
			}
		}

		// 当前玩家是否有正在进行的科技研究（且选中的建筑可研究）
		private (Texture2D Icon, float Progress, string Name)? GetResearchProgress()
		{
			if (_currentBrain == null || !GodotObject.IsInstanceValid(_currentBrain) ||
				_currentBrain.EntityOwner is not Structure structure ||
				!GodotObject.IsInstanceValid(structure))
				return null;

			bool canResearch = false;

			foreach (Node child in _currentBrain.GetChildren())
			{
				if (child is ResearchAction)
				{
					canResearch = true;
					break;
				}
			}

			if (!canResearch)
				return null;

			var player = Game.GetPlayerByTeam(Main.Instance?.LocalPlayerID ?? 0);
			var pd = player?.PlayerData;

			if (pd == null || !pd.IsResearching || pd.ResearchDuration <= FixMath.NET.Fix64.Zero)
				return null;

			float progress = (float)(pd.ResearchProgress / pd.ResearchDuration) * 100f;
			var cfg = ConfigDatabase.GetTech(pd.ResearchingTechId);

			return (cfg?.Icon, progress, RTS.Settings.Localization.TrName(pd.ResearchingTechId));
		}

		private void ClearQueue()
		{
			foreach (Node child in IconContainer.GetChildren()) child.Free();
		}
	}
}
