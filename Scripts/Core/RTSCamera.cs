using Godot;

// 伪 3D RTS 相机：倾斜上帝视角（透视投影，近大远小）
// 移动方式：鼠标贴边 / 小地图；滚轮缩放（FOV）
public partial class RTSCamera : Camera3D
{
	[Export] public float Speed = 2400.0f;        // 基准移动速度（世界单位/秒）
	[Export] public float Margin = 20.0f;         // 屏幕边缘触发宽度
	[Export] public float Height = 2500.0f;       // 相机高度（1 格 = 64 世界单位）
	[Export(PropertyHint.Range, "30,80,1")]
	public float Pitch = 55.0f;                   // 俯仰角：与水平面的夹角（度数）
	[Export] public float MinHeight = 600.0f;
	[Export] public float MaxHeight = 6000.0f;

	public override void _Ready()
	{
		Projection = ProjectionType.Perspective;
		Fov = 50.0f;
		Near = 1.0f;
		Far = 20000.0f;
		GlobalPosition = new Vector3(GlobalPosition.X, Height, GlobalPosition.Z);
		MakeCurrent();
		ApplyView();
	}

	public override void _Process(double delta)
	{
        if (RTS.UI.MatchMenu.BlocksGameInput) return;
		Vector2 direction = Vector2.Zero;

		// 键盘平移（方向键，走 InputMap 可改键）。
		// 之前只有鼠标贴边一种移动方式 —— 玩家想用键盘挪镜头完全没反应，
		// 而 RTS 里"左手键盘挪视角、右手鼠标操作"是基本手感。
		if (Input.IsActionPressed("game_cam_left")) direction.X -= 1f;
		if (Input.IsActionPressed("game_cam_right")) direction.X += 1f;
		if (Input.IsActionPressed("game_cam_up")) direction.Y -= 1f;
		if (Input.IsActionPressed("game_cam_down")) direction.Y += 1f;

		// 中键拖拽平移：按住中键时鼠标移动直接带动镜头，
		// 并且**屏蔽贴边**，否则拖到屏幕边缘会被贴边逻辑抢走。
		bool middleDrag = Input.IsMouseButtonPressed(MouseButton.Middle);
		if (middleDrag)
		{
			Vector2 mousePosNow = GetViewport().GetMousePosition();
			if (_dragLastMouse != Vector2.Zero)
			{
				// 屏幕位移 → 世界位移：按当前高度缩放，保证"拖多少走多少"的手感一致
				Vector2 screenDelta = mousePosNow - _dragLastMouse;
				float worldPerPixel = Height / 600f;
				PanBy(new Vector2(-screenDelta.X, -screenDelta.Y) * worldPerPixel);
			}
			_dragLastMouse = mousePosNow;
		}
		else
		{
			_dragLastMouse = Vector2.Zero;
		}

		// 鼠标贴边（拖拽时不生效，见上）
		if (!middleDrag && direction == Vector2.Zero)
		{
			Vector2 mousePos = GetViewport().GetMousePosition();
			Vector2 screenSize = GetViewport().GetVisibleRect().Size;

			if (mousePos.X <= Margin)
				direction.X -= 1f;
			if (mousePos.X >= screenSize.X - Margin)
				direction.X += 1f;
			if (mousePos.Y <= Margin)
				direction.Y -= 1f;
			if (mousePos.Y >= screenSize.Y - Margin)
				direction.Y += 1f;
		}

		if (direction != Vector2.Zero)
			PanBy(direction.Normalized() * Speed * (Height / 2500.0f) * (float)delta);

		// 每帧保持斜俯视朝向（位置被其他系统修改后也不会“飞出视角”）
		ApplyView();
	}

	private Vector2 _dragLastMouse = Vector2.Zero;

	/// <summary>按世界单位平移镜头（保持高度与俯仰）。</summary>
	public void PanBy(Vector2 worldDelta)
	{
		Vector3 pos = GlobalPosition;
		pos.X += worldDelta.X;
		// 相机朝 -Z：屏幕上方 = 地图上方 = -Z
		pos.Z += worldDelta.Y;
		pos.Y = Height;
		GlobalPosition = pos;
	}

	/// <summary>把镜头复位到地图/焦点中心。绑定 game_cam_center（空格）。</summary>
	public void CenterOnFocus()
	{
		SetViewCenter(_lastCenter);
	}

	private Vector2 _lastCenter = Vector2.Zero;

	/// <summary>键盘平移是否被聊天输入等 UI 抢占（聊天时不该同时挪镜头）。</summary>
	private static bool InputBlockedByUi() => RTS.Core.UserUI.ChatOpen;

	// 进入游戏/切场景后自动对准目标点（出生点/主基地）
	public void SetViewCenter(Vector2 worldPos)
	{
		_lastCenter = worldPos;
		// 屏幕中心 = 视线焦点 = 目标点：相机位置向 +Z 退 lookAhead，焦点正好落在 worldPos
		float lookAhead = GlobalPosition.Y / Mathf.Tan(Mathf.DegToRad(Pitch));
		GlobalPosition = new Vector3(worldPos.X, Height, worldPos.Y + lookAhead);
		ApplyView();
	}

	// 倾斜上帝视角：视线焦点在相机前方 XZ 平面上，Pitch 控制俯仰
	private void ApplyView()
	{
		float lookAhead = GlobalPosition.Y / Mathf.Tan(Mathf.DegToRad(Pitch));
		// 相机绕 Y 旋转 180°：朝 -Z 看，让屏幕上方对应地图上方（2D Y 小 = Z 小）
		Vector3 focus = new Vector3(GlobalPosition.X, 0f, GlobalPosition.Z - lookAhead);
		LookAt(focus, Vector3.Up);
	}

	// 沿视线方向推拉：缩放时画面中心（视线焦点）保持不动
	private void ZoomAlongView(float factor)
	{
		float lookAhead = GlobalPosition.Y / Mathf.Tan(Mathf.DegToRad(Pitch));
		Vector3 focus = new Vector3(GlobalPosition.X, 0f, GlobalPosition.Z - lookAhead);
		Vector3 newPos = focus + (GlobalPosition - focus) * factor;
		newPos.Y = Mathf.Clamp(newPos.Y, MinHeight, MaxHeight);
		Height = newPos.Y;
		GlobalPosition = newPos;
		ApplyView();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
        if (RTS.UI.MatchMenu.BlocksGameInput) return;
		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.WheelUp)
			{
				ZoomAlongView(0.85f);
				GetViewport().SetInputAsHandled();
			}
			else if (mb.ButtonIndex == MouseButton.WheelDown)
			{
				ZoomAlongView(1f / 0.85f);
				GetViewport().SetInputAsHandled();
			}
		}
	}
}
