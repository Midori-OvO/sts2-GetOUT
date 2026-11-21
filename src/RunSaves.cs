using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace GetOUT;

/// <summary>
/// 房主最近一次写盘的多人存档，以及从存档里删除玩家的操作。
/// 局内踢人用的就是这份存档：它和原版“保存并退出”后再读档得到的状态完全一样
/// （进房间时、战斗结束进入奖励时、事件中都会写一次）。
/// </summary>
internal static class RunSaves
{
	public static SerializableRun? Latest { get; private set; }

	/// <summary>写盘时的捕获生效过。生效后就不再用房间入口那份后备存档。</summary>
	private static bool _fromSaveFile;

	/// <summary>原版写多人存档时调用。</summary>
	public static void RememberWritten(SerializableRun save)
	{
		_fromSaveFile = true;
		Latest = Clone(save);
	}

	/// <summary>
	/// 后备：只在写盘的补丁没生效时使用。读档进入“已打完、正在领奖励”的房间时，原版这里给的是不带奖励状态的存档，
	/// 用它覆盖会让踢人后重打一遍这场战斗。
	/// </summary>
	public static void RememberRoomEntry(SerializableRun save)
	{
		if (!_fromSaveFile)
		{
			Latest = Clone(save);
		}
	}

	public static void Forget()
	{
		Latest = null;
	}

	/// <summary>
	/// 深拷贝存档。RunState.FromSerializable 会直接复用存档里的列表（比如 MapPointHistory），
	/// 不拷贝的话之后读档、存档都可能改到我们手里这一份。
	/// </summary>
	public static SerializableRun Clone(SerializableRun save)
	{
		JsonTypeInfo<SerializableRun> info = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
		return JsonSerializer.Deserialize(JsonSerializer.Serialize(save, info), info)!;
	}

	/// <summary>
	/// 从存档里删除这些玩家：玩家本身、房间里留给他们的额外奖励、地图绘画、历史统计。
	/// 怪物血量、格挡等按当前人数实时计算，不用改；共享随机数和玩家无关。
	/// </summary>
	/// <returns>实际删除的人数。</returns>
	public static int RemovePlayers(SerializableRun save, ICollection<ulong> playerIds)
	{
		int removed = save.Players.RemoveAll(p => playerIds.Contains(p.NetId));
		if (removed == 0)
		{
			return 0;
		}
		if (save.PreFinishedRoom?.ExtraRewards != null)
		{
			foreach (ulong id in playerIds)
			{
				save.PreFinishedRoom.ExtraRewards.Remove(id);
			}
		}
		save.MapDrawings?.drawings?.RemoveAll(d => playerIds.Contains(d.playerId));
		// 结算分数时会把所有人的统计加起来再除以人数，留着被删玩家的条目会算错
		if (save.MapPointHistory != null)
		{
			foreach (List<MapPointHistoryEntry> act in save.MapPointHistory)
			{
				foreach (MapPointHistoryEntry entry in act)
				{
					entry.PlayerStats?.RemoveAll(s => playerIds.Contains(s.PlayerId));
				}
			}
		}
		return removed;
	}

	public static List<SerializablePlayer> FindPlayers(SerializableRun save, ICollection<ulong> playerIds)
	{
		return save.Players.Where(p => playerIds.Contains(p.NetId)).ToList();
	}
}
