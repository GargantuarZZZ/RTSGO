using Godot;
using RTS.Data.Configs;
using RTS.Simulation;
using RTS.Units;

/// <summary>
/// 建筑计数标签公因式：地狱城怨灵计数与泰伦藻类工厂驻扎计数原来各复制一份
/// Label3D 外观 + “父级结构/本地玩家/配置”可见性检查，全部收敛到这里。
/// 子类只提供标签名、颜色与计数来源。
/// </summary>
public abstract partial class StructureCountLabel3D : Node3D
{
	private Label3D _label;

	protected abstract string LabelNodeName { get; }
	protected abstract Color LabelColor { get; }

	/// <summary>返回 (alive, max)；返回 false 表示本建筑没有这种计数，标签隐藏。</summary>
	protected abstract bool TryGetCount(Structure st, SimStructure sim, SimWorld world, out int alive, out int max);

	public override void _Ready()
	{
		_label = new Label3D
		{
			Name = LabelNodeName,
			FontSize = 96,
			OutlineSize = 12,
			PixelSize = 0.25f,
			NoDepthTest = true,
			OutlineModulate = Colors.Black,
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			Modulate = LabelColor
		};
		AddChild(_label);
	}

	public override void _Process(double delta)
	{
		if (GetParent() is not Structure st || st.LogicEntity is not SimStructure sim)
		{
			Visible = false;
			return;
		}

		if (RTS.Core.Main.Instance?.LocalPlayerID != st.TeamID)
		{
			Visible = false;
			return;
		}

		var cfg = ConfigDatabase.GetStructure(st.StructureName);
		if (cfg == null)
		{
			Visible = false;
			return;
		}

		var world = RTS.Core.SimManager.Instance?.World;
		if (world == null)
		{
			Visible = false;
			return;
		}

		// 主线程视觉组件遍历 World 必须持 WorldLock，否则和模拟线程
		// 并发修改字典会抛 “Collection was modified” 异常
		lock (RTS.Core.SimManager.Instance.WorldLock)
		{
			if (!TryGetCount(st, sim, world, out int alive, out int max))
			{
				Visible = false;
				return;
			}
			_label.Text = RTS.Settings.Localization.Tr(CountKey, alive, max);
		}
		Visible = true;
	}

	protected abstract string CountKey { get; }
}
