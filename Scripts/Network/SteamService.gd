# res://Scripts/Network/SteamService.gd
extends Node

# --- 信号 ---
signal packet_received(sender_id, data)
signal lobby_created(lobby_id)
signal lobby_joined(id)
signal lobby_list_received(lobbies)
signal player_list_updated(members)

# --- 变量 ---
var lobby_id: int = 0
var is_host: bool = false
var steam: Object = null
var is_initialized: bool = false 
var _offline: bool = false

# 用于 480 测试的唯一房间过滤标识
const GAME_FILTER_KEY = "game_name"
const GAME_FILTER_VALUE = "RTSArcade_Test_001" 

func _init():
	for arg in OS.get_cmdline_args() + OS.get_cmdline_user_args():
		if arg == "--offline" or arg == "--single" or arg == "--no-steam":
			_offline = true
			break
	if not _offline:
		steam = Engine.get_singleton("Steam")

func _ready():
	if _offline:
		return
	if steam:
		var init_result = steam.steamInit()
		
		var is_ok: bool = false
		if typeof(init_result) == TYPE_BOOL:
			is_ok = init_result
		elif typeof(init_result) == TYPE_DICTIONARY:
			is_ok = (init_result.get("status", 0) == 1)
			
		if is_ok:
			is_initialized = true
			steam.lobby_created.connect(_on_lobby_created)
			steam.lobby_joined.connect(_on_lobby_joined)
			steam.lobby_match_list.connect(_on_lobby_match_list)
			steam.lobby_chat_update.connect(_on_lobby_chat_update)
		else:
			printerr("[Steam-Debug] 初始化失败！可能原因：Steam 未启动、未登录，或根目录缺少 steam_appid.txt")
	else:
		printerr("[Steam-Debug] 严重错误：未找到 Steam GDExtension 插件！")

func _process(_delta):
	if is_initialized:
		steam.run_callbacks()
		_read_p2p_packets()

# --- 大厅操作 ---
func create_lobby():
	if steam: 
		# 2 = Public (公开大厅，这很重要！1是仅限好友)
		steam.createLobby(2, 8) 

func search_lobbies():
	if steam: 
		steam.addRequestLobbyListDistanceFilter(3)
		steam.addRequestLobbyListStringFilter(GAME_FILTER_KEY, GAME_FILTER_VALUE, 0)
		steam.requestLobbyList()

func join_lobby(id: int):
	if steam: 
		steam.joinLobby(id)

func leave_lobby():
	if steam and lobby_id != 0:
		steam.leaveLobby(lobby_id)
		lobby_id = 0
		is_host = false

func get_lobby_owner_steam_id() -> int:
	if steam and is_initialized and lobby_id != 0:
		return steam.getLobbyOwner(lobby_id)
	return 0

# --- 回调处理 ---
func _on_lobby_created(_is_connected: int, id: int):
	if _is_connected == 1:
		lobby_id = id
		is_host = true
		steam.setLobbyData(lobby_id, GAME_FILTER_KEY, GAME_FILTER_VALUE)
		
		var host_name = steam.getPersonaName()
		var room_name = host_name + " 的 RTS 房间"
		steam.setLobbyData(lobby_id, "name", room_name)
		lobby_created.emit(id)
		_update_player_list()

func _on_lobby_joined(id: int, _permissions: int, _locked: bool, response: int):
	if response == 1: # 1 = SUCCESS
		if lobby_id == id:
			# 创建大厅后 Steam 也会回一次加入回调，避免重复重置大厅状态
			return
		lobby_id = id
		lobby_joined.emit(id)
		_update_player_list()
	else:
		printerr("[Steam-Debug] 加入失败！错误码参考: 2=不存在, 3=没权限, 4=满员, 5=网络错误")

func _on_lobby_match_list(lobbies: Array):
	var formatted_lobbies = []
	
	if lobbies.is_empty():
		printerr("[Steam-Debug] 警告：服务器返回空列表，未找到符合条件的房间！")
	
	for id in lobbies:
		# 读取测试数据
		var lobby_name = steam.getLobbyData(id, "name")
		var filter_tag = steam.getLobbyData(id, GAME_FILTER_KEY)
		
		if lobby_name == "":
			lobby_name = "大厅: " + str(id)
			
		var dict = {
			"id": id,
			"name": lobby_name
		}
		formatted_lobbies.append(dict)
	lobby_list_received.emit(formatted_lobbies)

func _on_lobby_chat_update(_id: int, _changed_id: int, _making_id: int, _chat_state: int):
	_update_player_list()

# --- 手动遍历玩家列表 ---
func _update_player_list():
	if not is_initialized or lobby_id == 0:
		return
		
	var member_count = steam.getNumLobbyMembers(lobby_id)
	var member_data = []
	var owner_id = 0
	if steam.has_method("getLobbyOwner"):
		owner_id = steam.getLobbyOwner(lobby_id)
	
	for i in range(member_count):
		var m_steam_id = steam.getLobbyMemberByIndex(lobby_id, i)
		var m_name = steam.getFriendPersonaName(m_steam_id)
		member_data.append({"id": m_steam_id, "name": m_name, "is_host": m_steam_id == owner_id})
	
	player_list_updated.emit(member_data)

func refresh_player_list():
	_update_player_list()

# --- 网络收发包 ---
func _read_p2p_packets():
	if not is_initialized: return
	
	var packet_size = steam.getAvailableP2PPacketSize(0)
	while packet_size > 0:
		var packet = steam.readP2PPacket(packet_size, 0)
		
		# 确保 packet 真的是个字典并且里面有东西
		if packet != null and typeof(packet) == TYPE_DICTIONARY and not packet.is_empty():
			
			# 兼容获取发送者的 Steam ID
			var sender_id = 0
			if packet.has("remote_steam_id"):
				sender_id = packet["remote_steam_id"]
			elif packet.has("steam_id_remote"):
				sender_id = packet["steam_id_remote"]
			elif packet.has("steam_id"):
				sender_id = packet["steam_id"]
			else:
				printerr("[Steam-Debug] 收到未知格式的数据包，找不到发送者 ID 键: ", packet)
			
			# 如果成功拿到 ID 和 数据，就向上层 C# 发送信号
			if sender_id != 0 and packet.has("data"):
				packet_received.emit(sender_id, packet["data"])
				
		# 继续读取下一个包
		packet_size = steam.getAvailableP2PPacketSize(0)

func broadcast_packet(data: PackedByteArray):
	if not is_initialized or lobby_id == 0: return
	
	var member_count = steam.getNumLobbyMembers(lobby_id)
	var my_id = steam.getSteamID()
	
	for i in range(member_count):
		var member_id = steam.getLobbyMemberByIndex(lobby_id, i)
		if member_id != my_id:
			steam.sendP2PPacket(member_id, data, 2, 0)

# 定向发包：丢包补发时只发给请求方，避免补发风暴
func send_packet_to(steam_id: int, data: PackedByteArray):
	if not is_initialized or lobby_id == 0: return
	if steam_id <= 0: return
	if steam_id == get_local_steam_id(): return
	steam.sendP2PPacket(steam_id, data, 2, 0)

func get_local_steam_id() -> int:
	return steam.getSteamID() if steam else 0
