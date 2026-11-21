using System.Collections.Generic;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Saves;

namespace GetOUT;

// 游戏会通过 ReflectionHelper.GetSubtypesInMods<INetMessage>() 自动注册这些类型，
// 所以所有玩家都必须装同一版本的本 mod，否则消息 ID 对不上。
// ShouldBuffer = false：重载期间消息总线处于缓冲状态，握手消息必须能穿过去。
// LogLevel.Debug：和游戏自己的消息一样，收发时不写进普通日志。

/// <summary>
/// 房主 → 所有人：准备重载。附带已经删掉被踢玩家的存档、被踢的人（用于提示），
/// 以及参与这次重载的在线玩家（存档里其他还没回来的人照原版读档那样保持断线状态）。
/// </summary>
public struct GetOutPrepareMessage : INetMessage
{
	public uint seq;

	public SerializableRun run;

	public List<ulong> kicked;

	public List<ulong> online;

	public bool ShouldBroadcast => false;

	public NetTransferMode Mode => NetTransferMode.Reliable;

	public LogLevel LogLevel => LogLevel.Debug;

	public bool ShouldBuffer => false;

	public void Serialize(PacketWriter writer)
	{
		writer.WriteUInt(seq);
		writer.Write(run);
		WriteIds(writer, kicked);
		WriteIds(writer, online);
	}

	public void Deserialize(PacketReader reader)
	{
		seq = reader.ReadUInt();
		run = reader.Read<SerializableRun>();
		kicked = ReadIds(reader);
		online = ReadIds(reader);
	}

	private static void WriteIds(PacketWriter writer, List<ulong>? ids)
	{
		writer.WriteInt(ids?.Count ?? 0);
		foreach (ulong id in ids ?? new List<ulong>())
		{
			writer.WriteULong(id);
		}
	}

	private static List<ulong> ReadIds(PacketReader reader)
	{
		int count = reader.ReadInt();
		List<ulong> ids = new List<ulong>(count);
		for (int i = 0; i < count; i++)
		{
			ids.Add(reader.ReadULong());
		}
		return ids;
	}
}

/// <summary>客户端 → 房主：已停止处理旧局消息，可以开始。</summary>
public struct GetOutReadyMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

/// <summary>房主 → 所有人：所有人都已就绪，开始本地重载。</summary>
public struct GetOutGoMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

/// <summary>房主 → 所有人：有人没就绪，取消本次踢人。</summary>
public struct GetOutAbortMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

/// <summary>任意一方 → 其他人：本地重载失败，所有人断线回主菜单。</summary>
public struct GetOutFailedMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

/// <summary>客户端 → 房主：本地重载完成。</summary>
public struct GetOutLoadedMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

/// <summary>房主 → 所有人：所有人都加载完了，一起淡入。</summary>
public struct GetOutCompleteMessage : INetMessage
{
	public uint seq;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteUInt(seq);
	public void Deserialize(PacketReader reader) => seq = reader.ReadUInt();
}

/// <summary>
/// 读档大厅里房主 → 已进大厅的客户端：把这个玩家从存档里删掉。
/// 客户端只在入场时收过一次存档，开局消息不带存档，所以要靠这条消息同步。
/// </summary>
public struct GetOutLobbyKickMessage : INetMessage
{
	public ulong playerId;
	public bool ShouldBroadcast => false;
	public NetTransferMode Mode => NetTransferMode.Reliable;
	public LogLevel LogLevel => LogLevel.Debug;
	public bool ShouldBuffer => false;
	public void Serialize(PacketWriter writer) => writer.WriteULong(playerId);
	public void Deserialize(PacketReader reader) => playerId = reader.ReadULong();
}
