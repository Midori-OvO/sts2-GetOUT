using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace GetOUT;

/// <summary>
/// 读档大厅里踢人。三种读档界面（多人、每日、自定义）共用 NRemoteLoadLobbyPlayerContainer，
/// 所以全部挂在它上面：房主看到没进大厅的玩家槽位上有“踢出”按钮，确认后把这个人从存档里删掉，
/// 并通知已经进大厅的客户端做同样的删除。被删的人之后再连进来，原版会以“不在存档里”拒绝。
/// </summary>
internal static class LobbyKick
{
	private const string ButtonName = "GetOUTKick";

	private static NRemoteLoadLobbyPlayerContainer? _container;
	private static INetGameService? _handlerNet;

	public static void OnInitialized(NRemoteLoadLobbyPlayerContainer container)
	{
		_container = container;
		LoadRunLobby? lobby = GetLobby(container);
		if (lobby == null)
		{
			return;
		}
		if (lobby.NetService.Type == NetGameType.Client)
		{
			RegisterHandler(lobby.NetService);
		}
		Refresh(container);
	}

	public static void OnCleanup(NRemoteLoadLobbyPlayerContainer container)
	{
		if (!ReferenceEquals(container, _container))
		{
			return;
		}
		UnregisterHandler();
		_container = null;
	}

	/// <summary>有人进出大厅时更新各槽位的按钮：只给房主、只给不在大厅里的人显示。</summary>
	public static void Refresh(NRemoteLoadLobbyPlayerContainer container)
	{
		LoadRunLobby? lobby = GetLobby(container);
		if (lobby == null || lobby.NetService.Type != NetGameType.Host)
		{
			return;
		}
		HashSet<ulong> connected = LobbyCompat.ConnectedIds(lobby);
		foreach (NRemoteLobbyPlayer slot in GetNodes(container))
		{
			if (!GodotObject.IsInstanceValid(slot) || slot.PlayerId == lobby.NetService.NetId)
			{
				continue;
			}
			Button button = slot.GetNodeOrNull<Button>(ButtonName) ?? AddButton(slot, slot.PlayerId);
			button.Visible = !connected.Contains(slot.PlayerId);
		}
	}

	private static Button AddButton(NRemoteLobbyPlayer slot, ulong playerId)
	{
		Button button = Ui.MakeDangerButton(Texts.Get("kick_button"), 18);
		button.Name = ButtonName;
		button.MouseFilter = Control.MouseFilterEnum.Stop;
		// 挂在槽位右边外侧、垂直居中，不改原版槽位的布局。槽位此时可能还没排版，所以用锚点而不是算坐标
		button.AnchorLeft = 1f;
		button.AnchorRight = 1f;
		button.AnchorTop = 0.5f;
		button.AnchorBottom = 0.5f;
		button.OffsetLeft = 12f;
		button.OffsetRight = 12f;
		button.OffsetTop = 0f;
		button.OffsetBottom = 0f;
		button.GrowHorizontal = Control.GrowDirection.End;
		button.GrowVertical = Control.GrowDirection.Both;
		button.Pressed += () => TaskHelper.RunSafely(ConfirmAndKick(playerId));
		slot.AddChild(button);
		return button;
	}

	private static async Task ConfirmAndKick(ulong playerId)
	{
		NRemoteLoadLobbyPlayerContainer? container = _container;
		LoadRunLobby? lobby = container == null ? null : GetLobby(container);
		if (container == null || lobby == null || lobby.NetService.Type != NetGameType.Host)
		{
			return;
		}
		SerializablePlayer? player = lobby.Run.Players.Find(p => p.NetId == playerId);
		if (player == null)
		{
			return;
		}
		string name = Texts.PlayerName(lobby.NetService.Platform, playerId);
		bool ok = await Ui.Confirm("confirm_lobby", ("name", name), ("character", Texts.CharacterName(player)));
		// 弹窗期间大厅可能已经关了，或者这个人刚好连了进来
		if (!ok || !ReferenceEquals(container, _container) || !ReferenceEquals(lobby, GetLobby(container)))
		{
			return;
		}
		if (LobbyCompat.ConnectedIds(lobby).Contains(playerId))
		{
			Texts.Warn(Texts.Format("player_back", ("players", name)));
			return;
		}
		Apply(container, lobby, playerId);
		MainFile.Logger.Info($"[大厅] 房主把 {playerId} 从存档中删除，剩余 {lobby.Run.Players.Count} 人");
		Texts.Info(Texts.Format("lobby_kicked", ("players", name)));
		lobby.NetService.SendMessage(new GetOutLobbyKickMessage { playerId = playerId });
		LobbyCompat.RecheckBegin(lobby);
	}

	/// <summary>从这份存档里删掉玩家，并移除他的槽位。房主和客户端都走这里。</summary>
	private static void Apply(NRemoteLoadLobbyPlayerContainer container, LoadRunLobby lobby, ulong playerId)
	{
		if (playerId == lobby.NetService.NetId)
		{
			return;
		}
		// 槽位要先拿掉：原版刷新槽位时会在存档里查这个人，查不到会抛异常
		IList nodes = GetNodeList(container);
		for (int i = nodes.Count - 1; i >= 0; i--)
		{
			if (nodes[i] is NRemoteLobbyPlayer slot && slot.PlayerId == playerId)
			{
				nodes.RemoveAt(i);
				slot.QueueFree();
			}
		}
		RunSaves.RemovePlayers(lobby.Run, new[] { playerId });
		MegaLabel? others = AccessTools.Field(typeof(NRemoteLoadLobbyPlayerContainer), "_othersLabel")?.GetValue(container) as MegaLabel;
		if (others != null)
		{
			LocString text = new LocString("main_menu_ui", "MULTIPLAYER_LOAD_MENU.OTHERS");
			text.Add("others", lobby.Run.Players.Count - 1);
			others.Text = text.GetFormattedText();
		}
	}

	private static void OnLobbyKick(GetOutLobbyKickMessage message, ulong senderId)
	{
		NRemoteLoadLobbyPlayerContainer? container = _container;
		LoadRunLobby? lobby = container == null ? null : GetLobby(container);
		if (container == null || lobby == null || lobby.NetService.Type != NetGameType.Client)
		{
			return;
		}
		if (!lobby.Run.Players.Exists(p => p.NetId == message.playerId))
		{
			return;
		}
		string name = Texts.PlayerName(lobby.NetService.Platform, message.playerId);
		Apply(container, lobby, message.playerId);
		MainFile.Logger.Info($"[大厅] 房主把 {message.playerId} 从存档中删除");
		Texts.Info(Texts.Format("lobby_kicked", ("players", name)));
	}

	private static void RegisterHandler(INetGameService net)
	{
		UnregisterHandler();
		net.RegisterMessageHandler<GetOutLobbyKickMessage>(OnLobbyKick);
		_handlerNet = net;
	}

	private static void UnregisterHandler()
	{
		try
		{
			_handlerNet?.UnregisterMessageHandler<GetOutLobbyKickMessage>(OnLobbyKick);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Info("注销大厅消息处理失败：" + ex.Message);
		}
		_handlerNet = null;
	}

	private static LoadRunLobby? GetLobby(NRemoteLoadLobbyPlayerContainer container)
	{
		return AccessTools.Field(typeof(NRemoteLoadLobbyPlayerContainer), "_lobby")?.GetValue(container) as LoadRunLobby;
	}

	private static IList GetNodeList(NRemoteLoadLobbyPlayerContainer container)
	{
		return AccessTools.Field(typeof(NRemoteLoadLobbyPlayerContainer), "_nodes")?.GetValue(container) as IList
			?? throw new MissingFieldException(nameof(NRemoteLoadLobbyPlayerContainer), "_nodes");
	}

	private static IEnumerable<NRemoteLobbyPlayer> GetNodes(NRemoteLoadLobbyPlayerContainer container)
	{
		foreach (object? item in GetNodeList(container))
		{
			if (item is NRemoteLobbyPlayer slot)
			{
				yield return slot;
			}
		}
	}
}
