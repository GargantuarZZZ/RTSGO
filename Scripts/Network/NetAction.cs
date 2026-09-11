using System;
using System.IO;
using Godot;
using RTS.Simulation;
using FP = FixMath.NET.Fix64;

namespace RTS.Network
{
	/// <summary>
	/// 网络指令协议结构。
	///
	/// 当前仍兼容你的旧结构：
	/// - Move / Attack / Build_xxx 等战斗锁步指令
	/// - LOBBY_UPDATE / GAME_PREPARE 等系统指令
	///
	/// 本次修复重点：
	/// 1. 加入协议版本。
	/// 2. 加入 Sequence 序号，用于强去重。
	/// 3. 加入 TryDeserialize，避免坏包导致空指令继续流入系统。
	/// 4. 限制 EntityIDs 长度，防止异常包造成内存问题。
	/// </summary>
	public struct NetAction
	{
		public const int ProtocolVersion = 2;
		public const int MaxEntityIdCount = 512;
		public const int MaxStringLength = 256;

		/// <summary>
		/// 常规地面指令工厂：统一“目标点 → ×1000 定点编码”的锁步协议，
		/// 替代各处重复的 `new NetAction { PlayerID=…, EntityIDs=…, TargetX=RoundToInt(x*1000)… }` 样板。
		/// </summary>
		public static NetAction GroundCommand(
			string actionId,
			int playerId,
			Vector2 worldPos,
			System.Collections.Generic.IEnumerable<int> entityIds = null,
			int targetEntityId = -1,
			bool isQueue = false)
		{
			return new NetAction
			{
				PlayerID = playerId,
				EntityIDs = entityIds == null ? System.Array.Empty<int>() : new System.Collections.Generic.List<int>(entityIds).ToArray(),
				ActionId = actionId,
				TargetX = Mathf.RoundToInt(worldPos.X * 1000f),
				TargetY = Mathf.RoundToInt(worldPos.Y * 1000f),
				TargetEntityID = targetEntityId,
				IsQueue = isQueue
			};
		}

		/// <summary>
		/// 目标点解码：与 GroundCommand 的 ×1000 定点编码对称，保持定点数不转 float。
		/// 全项目 16 处手写 `(FP)TargetX / (FP)1000m` 全部收敛到这里。
		/// </summary>
		public FPVector2 DecodeTargetPos()
		{
			return new FPVector2((FP)TargetX / (FP)1000m, (FP)TargetY / (FP)1000m);
		}

		// =========================================================
		// 协议元数据
		// =========================================================

		public int Version;        // 协议版本
		public uint Sequence;      // 发送者本地递增序号，用于去重

		// =========================================================
		// 基础元数据
		// =========================================================

		public int PlayerID;       // 网络玩家 ID，不是 TeamID
		public int TargetTick;     // 执行目标逻辑帧

		// =========================================================
		// 指令标识
		// =========================================================

		public string ActionId;        // Move / Attack / LOBBY_UPDATE / GAME_PREPARE
		public string ActionIdExtra;   // 种族名、额外字符串等

		// =========================================================
		// 目标数据
		// =========================================================

		public int[] EntityIDs;
		public long TargetX;
		public long TargetY;
		public int TargetEntityID;

		// =========================================================
		// 状态位
		// =========================================================

		public bool IsQueue;

		// =========================================================
		// 序列化
		// =========================================================

		public byte[] Serialize()
		{
			using var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);

			// 新协议头
			writer.Write(ProtocolVersion);
			writer.Write(Sequence);

			// 基础元数据
			writer.Write(PlayerID);
			writer.Write(TargetTick);

			// 指令标识
			writer.Write(SanitizeString(ActionId));
			writer.Write(SanitizeString(ActionIdExtra));

			// 实体数组
			if (EntityIDs == null)
			{
				writer.Write(0);
			}
			else
			{
				int count = Math.Min(EntityIDs.Length, MaxEntityIdCount);
				writer.Write(count);

				for (int i = 0; i < count; i++)
				{
					writer.Write(EntityIDs[i]);
				}
			}

			// 目标数据
			writer.Write(TargetX);
			writer.Write(TargetY);
			writer.Write(TargetEntityID);

			// 状态位
			writer.Write(IsQueue);

			return ms.ToArray();
		}

		// =========================================================
		// 反序列化：安全版本
		// =========================================================

		public static bool TryDeserialize(byte[] data, out NetAction action)
		{
			action = default;

			if (data == null || data.Length == 0)
				return false;

			try
			{
				using var ms = new MemoryStream(data);
				using var reader = new BinaryReader(ms);

				int firstInt = reader.ReadInt32();

				// =================================================
				// 新协议：第一个 int 是 ProtocolVersion。
				// 旧协议：第一个 int 是 PlayerID。
				//
				// 为了兼容你当前已经发出去的旧包，这里支持旧格式读取。
				// 后面所有客户端都更新后，可以删除 Legacy 分支。
				// =================================================

				if (firstInt == ProtocolVersion)
				{
					action.Version = firstInt;
					action.Sequence = reader.ReadUInt32();

					action.PlayerID = reader.ReadInt32();
					action.TargetTick = reader.ReadInt32();

					action.ActionId = SafeReadString(reader);
					action.ActionIdExtra = SafeReadString(reader);

					int len = reader.ReadInt32();

					if (len < 0 || len > MaxEntityIdCount)
						return false;

					action.EntityIDs = new int[len];

					for (int i = 0; i < len; i++)
					{
						action.EntityIDs[i] = reader.ReadInt32();
					}

					action.TargetX = reader.ReadInt64();
					action.TargetY = reader.ReadInt64();
					action.TargetEntityID = reader.ReadInt32();

					action.IsQueue = reader.ReadBoolean();
				}
				else
				{
					// 旧协议兼容：
					// 旧格式开头是 PlayerID，没有 Version 和 Sequence。
					action.Version = 1;
					action.Sequence = 0;

					action.PlayerID = firstInt;
					action.TargetTick = reader.ReadInt32();

					action.ActionId = SafeReadString(reader);
					action.ActionIdExtra = SafeReadString(reader);

					int len = reader.ReadInt32();

					if (len < 0 || len > MaxEntityIdCount)
						return false;

					action.EntityIDs = new int[len];

					for (int i = 0; i < len; i++)
					{
						action.EntityIDs[i] = reader.ReadInt32();
					}

					action.TargetX = reader.ReadInt64();
					action.TargetY = reader.ReadInt64();
					action.TargetEntityID = reader.ReadInt32();

					action.IsQueue = reader.ReadBoolean();
				}

				if (string.IsNullOrEmpty(action.ActionId))
					return false;

				return true;
			}
			catch (Exception e)
			{
				Godot.GD.PrintErr($"[NetAction] 反序列化失败: {e.Message}");
				action = default;
				return false;
			}
		}

		// =========================================================
		// 反序列化：旧接口兼容
		// =========================================================

		public static NetAction Deserialize(byte[] data)
		{
			if (TryDeserialize(data, out var action))
			{
				return action;
			}

			return new NetAction
			{
				Version = ProtocolVersion,
				Sequence = 0,
				PlayerID = -1,
				TargetTick = -1,
				ActionId = "",
				ActionIdExtra = "",
				EntityIDs = new int[0],
				TargetX = 0,
				TargetY = 0,
				TargetEntityID = -1,
				IsQueue = false
			};
		}

		// =========================================================
		// 工具方法
		// =========================================================

		private static string SanitizeString(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "";

			if (value.Length <= MaxStringLength)
				return value;

			return value.Substring(0, MaxStringLength);
		}

		private static string SafeReadString(BinaryReader reader)
		{
			string value = reader.ReadString();

			if (value == null)
				return "";

			if (value.Length > MaxStringLength)
				return value.Substring(0, MaxStringLength);

			return value;
		}

		public bool IsValid()
		{
			return !string.IsNullOrEmpty(ActionId) && PlayerID > 0;
		}

		public bool IsSystemAction()
		{
			return !string.IsNullOrEmpty(ActionId) &&
				   (ActionId.StartsWith("LOBBY_") || ActionId.StartsWith("GAME_"));
		}

		public bool IsLockstepAction()
		{
			return !string.IsNullOrEmpty(ActionId) && !IsSystemAction();
		}

		public string BuildDebugString()
		{
			string ids = EntityIDs == null ? "" : string.Join(",", EntityIDs);

			return $"Ver={Version}, Seq={Sequence}, Player={PlayerID}, Tick={TargetTick}, Action={ActionId}, Extra={ActionIdExtra}, Entities=[{ids}], Target=({TargetX},{TargetY}), TargetEntity={TargetEntityID}, Queue={IsQueue}";
		}
	}
}
