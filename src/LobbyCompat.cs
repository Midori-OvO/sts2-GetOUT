using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace GetOUT;

/// <summary>
/// 让同一个 dll 兼容多个游戏版本。LoadRunLobby 记录玩家的方式：
///   v0.111.0 起：public List&lt;LoadRunLobbyPlayer&gt; Players（结构体，含 id/isModded/isReady）
///   v0.107.1：   public HashSet&lt;ulong&gt; ConnectedPlayerIds + private HashSet&lt;ulong&gt; _readyPlayers（已不支持，分支保留）
/// 这里全部用反射访问，不在编译期绑定任何一个版本独有的成员。
/// </summary>
internal static class LobbyCompat
{
	private static readonly Type LobbyType = typeof(LoadRunLobby);

	/// <summary>当前在大厅里（已连接）的玩家 id，包括房主自己。</summary>
	public static HashSet<ulong> ConnectedIds(LoadRunLobby lobby)
	{
		if (AccessTools.Property(LobbyType, "Players")?.GetValue(lobby) is IList players)
		{
			HashSet<ulong> ids = new HashSet<ulong>();
			if (players.Count == 0)
			{
				return ids;
			}
			FieldInfo idField = AccessTools.Field(players[0]!.GetType(), "id");
			foreach (object? player in players)
			{
				ids.Add((ulong)idField.GetValue(player)!);
			}
			return ids;
		}
		if (AccessTools.Property(LobbyType, "ConnectedPlayerIds")?.GetValue(lobby) is HashSet<ulong> connected)
		{
			return connected.ToHashSet();
		}
		throw new MissingMemberException("LoadRunLobby 既没有 Players 也没有 ConnectedPlayerIds，游戏版本不受支持");
	}

	public static bool AllConnectedReady(LoadRunLobby lobby)
	{
		if (AccessTools.Property(LobbyType, "Players")?.GetValue(lobby) is IList players)
		{
			if (players.Count == 0)
			{
				return false;
			}
			FieldInfo readyField = AccessTools.Field(players[0]!.GetType(), "isReady");
			return players.Cast<object>().All(p => (bool)readyField.GetValue(p)!);
		}
		if (AccessTools.Field(LobbyType, "_readyPlayers")?.GetValue(lobby) is HashSet<ulong> ready)
		{
			return ConnectedIds(lobby).All(ready.Contains);
		}
		return false;
	}

	/// <summary>还在握手中、尚未进入大厅的玩家数；字段不存在时当作 0。</summary>
	public static int ConnectingCount(LoadRunLobby lobby)
	{
		return AccessTools.Field(LobbyType, "_connectingPlayers")?.GetValue(lobby) is ICollection connecting ? connecting.Count : 0;
	}

	/// <summary>房主踢人后，人数可能已经满足开局条件：照原版的逻辑再检查一次。</summary>
	public static void RecheckBegin(LoadRunLobby lobby)
	{
		AccessTools.Method(LobbyType, "BeginRunForAllPlayersIfAllReady")?.Invoke(lobby, null);
	}

	/// <summary>不断线重载时临时建的大厅：直接把在线的人都标记为已连接并就绪。</summary>
	public static void AddReadyPlayers(LoadRunLobby lobby, IEnumerable<ulong> playerIds, INetGameService net)
	{
		if (AccessTools.Property(LobbyType, "Players")?.GetValue(lobby) is IList players)
		{
			bool modded = IsLocalModded(net);
			Type playerType = players.GetType().GetGenericArguments()[0];
			foreach (ulong id in playerIds)
			{
				object player = Activator.CreateInstance(playerType)!;
				AccessTools.Field(playerType, "id").SetValue(player, id);
				AccessTools.Field(playerType, "isModded")?.SetValue(player, modded);
				AccessTools.Field(playerType, "isReady")?.SetValue(player, true);
				players.Add(player);
			}
			return;
		}
		if (AccessTools.Property(LobbyType, "ConnectedPlayerIds")?.GetValue(lobby) is HashSet<ulong> connected)
		{
			HashSet<ulong>? ready = AccessTools.Field(LobbyType, "_readyPlayers")?.GetValue(lobby) as HashSet<ulong>;
			foreach (ulong id in playerIds)
			{
				connected.Add(id);
				ready?.Add(id);
			}
			return;
		}
		throw new MissingMemberException("LoadRunLobby 既没有 Players 也没有 ConnectedPlayerIds，游戏版本不受支持");
	}

	/// <summary>Beta 版：等价于 net.LocalVersion.IsModded()（正式版的 INetGameService 没有 LocalVersion）。</summary>
	private static bool IsLocalModded(INetGameService net)
	{
		try
		{
			object? version = AccessTools.Property(net.GetType(), "LocalVersion")?.GetValue(net);
			if (version == null)
			{
				return true;
			}
			if (AccessTools.Method(version.GetType(), "IsModded", Type.EmptyTypes)?.Invoke(version, null) is bool modded)
			{
				return modded;
			}
		}
		catch (Exception ex)
		{
			MainFile.Logger.Warn("读取本机 mod 状态失败：" + ex.Message);
		}
		// 装了本 mod 就一定是 modded
		return true;
	}

	/// <summary>房主当前连着的客户端。ConnectedPeers 的返回类型各版本不同（v0.111.0 是 List，v0.113.0 是 IReadOnlyList），只能反射。</summary>
	public static HashSet<ulong> ConnectedPeers(NetHostGameService host)
	{
		return ((AccessTools.Property(typeof(NetHostGameService), "ConnectedPeers")?.GetValue(host) as IEnumerable<NetClientData>)
			?? Enumerable.Empty<NetClientData>()).Select(p => p.peerId).ToHashSet();
	}
}

/// <summary>
/// 重载时临时建的 LoadRunLobby 需要一个监听器；我们不走大厅界面，全部空实现。
/// 两个版本的 ILoadRunLobbyListener 方法签名不同（PlayerConnected 的参数类型变了），
/// 所以不静态实现接口，而是用 DispatchProxy 在运行时按当前游戏版本的接口生成实现。
/// </summary>
public class SilentLobbyListener : DispatchProxy
{
	public static ILoadRunLobbyListener Create()
	{
		return DispatchProxy.Create<ILoadRunLobbyListener, SilentLobbyListener>();
	}

	protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
	{
		switch (targetMethod?.Name)
		{
		case "ShouldAllowRunToBegin":
			return Task.FromResult(result: true);
		case "LocalPlayerDisconnected":
			MainFile.Logger.Info("[重载] 临时大厅收到本地断线：" + (args != null && args.Length > 0 ? args[0] : null));
			return null;
		default:
			return null;
		}
	}
}
