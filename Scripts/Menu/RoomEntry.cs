using Godot;
using System;

public partial class RoomEntry : HBoxContainer
{
	public string HostIp { get; private set; }

	// UI 调用加入时触发
	public Action<string> OnJoinRequested;

	public void Setup(string ip, string roomName, int currentPlayers, int maxPlayers)
	{
		HostIp = ip;
		// 静态按钮（加入）按 tr_key 翻译；房间名/人数是动态文本不走键值
		RTS.Settings.Localization.ApplySceneTranslations(this);
		GetNode<Label>("LblRoomName").Text = $"{roomName} ({ip})";
		GetNode<Label>("LblPlayers").Text = $"{currentPlayers}/{maxPlayers}";

		var btn = GetNode<Button>("BtnJoin");
		btn.Pressed += () => OnJoinRequested?.Invoke(HostIp);
	}
}
