using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace GetOUT;

/// <summary>
/// 局内踢人：房主把断线玩家从最近一次存档里删掉，所有在线的人在现有连接上各自清掉当前局，
/// 再用这份存档重新载入（和原版“保存并退出 → 读档”得到的状态一致，只是不用断线、不用回大厅）。
///
/// 流程：
///   1. 房主：暂停旧局、扣住出站消息、开启收包缓冲 → 广播 Prepare(新存档, 被踢的人, 在线的人)
///   2. 客户端：同样停下来 → 回复 Ready
///   3. 房主：收齐 Ready → 清空旧局消息 → 广播 Go
///   4. 所有人：本地重载（CleanUp 时拦截断线）；原版 Launch() 会关闭缓冲并放行新局消息
///   5. 客户端报告 Loaded，房主收齐后广播 Complete，大家一起淡入
/// 任何一步失败，所有人都断线回主菜单（存档已经是删过人的版本，房主重新开房读档即可）。
/// </summary>
internal static class Reloader
{
	private const int ReadyTimeoutMs = 15000;

	private static readonly ConditionalWeakTable<INetGameService, object> RegisteredServices = new ConditionalWeakTable<INetGameService, object>();

	/// <summary>为 true 时拦截 NetService.Disconnect。只在我们调用 RunManager.CleanUp 期间为 true。</summary>
	public static bool SuppressDisconnect;

	private static bool _busy;
	private static bool _reloading;
	private static bool _cancellingHandshake;
	private static uint _seq;
	private static uint _activeSeq;
	private static ulong _hostId;
	private static SerializableRun? _pendingRun;
	private static List<ulong> _pendingKicked = new List<ulong>();
	private static List<ulong> _pendingOnline = new List<ulong>();
	private static string _kickedText = "";
	private static HashSet<ulong> _awaitingReady = new HashSet<ulong>();
	private static TaskCompletionSource<bool>? _allReady;
	private static INetGameService? _reloadNet;
	private static TaskCompletionSource<Exception>? _reloadFailure;
	private static bool _failureSent;
	private static HashSet<ulong> _awaitingLoaded = new HashSet<ulong>();
	private static TaskCompletionSource<bool>? _loadComplete;

	public static bool IsBusy => _busy;

	/// <summary>玩家点了“保存并退出”，正在回主菜单。下一局 Launch 时清除。</summary>
	public static bool Quitting;

	public static Task<Exception>? ReloadFailureTask => _reloading ? _reloadFailure?.Task : null;

	// 这些对象在 RunManager.CleanUp 里没有被 Dispose，但在构造时往 NetService 注册了消息处理器。
	// 原版换局会直接丢弃整个网络连接所以无所谓；我们复用连接，必须手动释放，否则旧局的处理器还会继续收消息。
	// InputSynchronizer 不在这里释放：后面调用 RemoteCursorContainer.Initialize 时会先 Deinitialize（里面会 Dispose 旧的）。
	private static readonly string[] LeftoverDisposables =
	{
		"CombatStateSynchronizer",
		"EventSynchronizer",
		"OneOffSynchronizer",
		"MapSelectionSynchronizer",
		"TreasureRoomRelicSynchronizer",
		"HoveredModelTracker",
		"MerchantSynchronizer",
		"RunLocationTargetedBuffer",
		"ActChangeSynchronizer"
	};

	public static void EnsureHandlers(INetGameService? net)
	{
		if (net == null || !net.Type.IsMultiplayer() || RegisteredServices.TryGetValue(net, out _))
		{
			return;
		}
		RegisteredServices.Add(net, new object());
		net.RegisterMessageHandler<GetOutPrepareMessage>(OnPrepare);
		net.RegisterMessageHandler<GetOutReadyMessage>(OnReady);
		net.RegisterMessageHandler<GetOutGoMessage>(OnGo);
		net.RegisterMessageHandler<GetOutAbortMessage>(OnAbort);
		net.RegisterMessageHandler<GetOutFailedMessage>(OnReloadFailed);
		net.RegisterMessageHandler<GetOutLoadedMessage>(OnReloadLoaded);
		net.RegisterMessageHandler<GetOutCompleteMessage>(OnReloadComplete);
	}

	// ==================================================================
	// 房主
	// ==================================================================

	/// <summary>当前能否在局内踢人（多人局房主、没有正在进行的重载），不能就弹提示。</summary>
	public static bool CanKick(out NetHostGameService host)
	{
		host = null!;
		if (Quitting)
		{
			return false;
		}
		RunManager? run = RunManager.Instance;
		if (run == null || !run.IsInProgress)
		{
			Texts.Warn(Texts.Get("not_in_run"));
			return false;
		}
		if (run.NetService is not NetHostGameService netHost)
		{
			Texts.Warn(Texts.Get("host_only"));
			return false;
		}
		if (_busy)
		{
			Texts.Warn(Texts.Get("busy"));
			return false;
		}
		host = netHost;
		return true;
	}

	/// <summary>存档里当前断线的玩家（不含房主）。</summary>
	public static List<SerializablePlayer> OfflinePlayers(NetHostGameService host, SerializableRun save)
	{
		HashSet<ulong> connected = LobbyCompat.ConnectedPeers(host);
		return save.Players.Where(p => p.NetId != host.NetId && !connected.Contains(p.NetId)).ToList();
	}

	public static void RequestKick(IReadOnlyCollection<ulong> kickIds)
	{
		if (!CanKick(out NetHostGameService host))
		{
			return;
		}
		SerializableRun? latest = RunSaves.Latest;
		if (latest == null)
		{
			Texts.Warn(Texts.Get("no_save"));
			return;
		}
		HashSet<ulong> connected = LobbyCompat.ConnectedPeers(host);
		HashSet<ulong> kick = kickIds.Where(id => id != host.NetId).ToHashSet();
		List<SerializablePlayer> kickedPlayers = RunSaves.FindPlayers(latest, kick);
		if (kickedPlayers.Count == 0)
		{
			Texts.Warn(Texts.Get("nobody_to_kick"));
			return;
		}
		List<SerializablePlayer> back = kickedPlayers.Where(p => connected.Contains(p.NetId)).ToList();
		if (back.Count > 0)
		{
			Texts.Warn(Texts.Format("player_back", ("players", Texts.Describe(host.Platform, back))));
			return;
		}

		SerializableRun save = RunSaves.Clone(latest);
		RunSaves.RemovePlayers(save, kick);
		List<ulong> kicked = kickedPlayers.Select(p => p.NetId).ToList();
		List<ulong> online = save.Players.Select(p => p.NetId).Where(id => id == host.NetId || connected.Contains(id)).ToList();
		List<ulong> clients = online.Where(id => id != host.NetId).ToList();
		string kickedText = Texts.Describe(host.Platform, kickedPlayers);

		_activeSeq = ++_seq;
		try
		{
			ReloadQuiescence.Begin(host);
			_busy = true;
			_pendingRun = save;
			_pendingKicked = kicked;
			_pendingOnline = online;
			_kickedText = kickedText;
			_awaitingReady = clients.ToHashSet();
			_allReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (_awaitingReady.Count == 0)
			{
				_allReady.TrySetResult(true);
			}
			host.SetBufferMessages(true);
			StartNetworkPump(host);
			MainFile.Logger.Info($"[房主] 踢出 {string.Join(",", kicked)}，重载到{DescribeTarget(save)}，剩余 {save.Players.Count} 人，在线 {online.Count} 人");
			Texts.Info(Texts.Format("kicking", ("players", kickedText)));
			host.SendMessage(new GetOutPrepareMessage { seq = _activeSeq, run = save, kicked = kicked, online = online });
			TaskHelper.RunSafely(HostWaitThenGo(host, _activeSeq));
		}
		catch (Exception ex)
		{
			TaskHelper.RunSafely(AbortHandshakeAsync(host, _activeSeq, "no_response", ex, notifyClients: true));
		}
	}

	private static async Task HostWaitThenGo(NetHostGameService host, uint seq)
	{
		try
		{
			Task ready = _allReady!.Task;
			Task finished = await Task.WhenAny(ready, Task.Delay(ReadyTimeoutMs));
			if (seq != _activeSeq || _cancellingHandshake || _pendingRun == null)
			{
				return;
			}
			if (finished != ready)
			{
				MainFile.Logger.Info($"[房主] 等待就绪超时，未就绪：{string.Join(",", _awaitingReady)}。取消本次踢人");
				await AbortHandshakeAsync(host, seq, "no_response", notifyClients: true);
				return;
			}
			InitializeReload(host, _pendingOnline);
			await ReloadLocally(_pendingRun, sendGo: true);
		}
		catch (Exception ex)
		{
			await AbortHandshakeAsync(host, seq, "no_response", ex, notifyClients: true);
		}
	}

	private static void OnReady(GetOutReadyMessage message, ulong senderId)
	{
		if (message.seq != _activeSeq || _allReady == null)
		{
			return;
		}
		_awaitingReady.Remove(senderId);
		if (_awaitingReady.Count == 0)
		{
			_allReady.TrySetResult(true);
		}
	}

	// ==================================================================
	// 客户端
	// ==================================================================

	private static void OnPrepare(GetOutPrepareMessage message, ulong senderId)
	{
		INetGameService? net = RunManager.Instance?.NetService;
		if (net is not NetClientGameService client || _busy || _cancellingHandshake)
		{
			return;
		}
		_activeSeq = message.seq;
		_hostId = senderId;
		try
		{
			ReloadQuiescence.Begin(client);
			_busy = true;
			_pendingRun = message.run;
			_pendingKicked = message.kicked ?? new List<ulong>();
			_pendingOnline = message.online ?? new List<ulong>();
			_kickedText = string.Join(Texts.Get("list_sep"), _pendingKicked.Select(id => Texts.PlayerName(client.Platform, id)));
			client.SetBufferMessages(true);
			StartNetworkPump(client);
			MainFile.Logger.Info($"[客户端] 房主踢出 {string.Join(",", _pendingKicked)}，重载到{DescribeTarget(message.run)}");
			Texts.Info(Texts.Format("host_kicking", ("players", _kickedText)));
			client.SendMessage(new GetOutReadyMessage { seq = message.seq });
			TaskHelper.RunSafely(ClientGoTimeout(client, message.seq));
		}
		catch (Exception ex)
		{
			TaskHelper.RunSafely(AbortHandshakeAsync(client, message.seq, "host_cancelled", ex));
		}
	}

	/// <summary>房主迟迟不发 Go/Abort（比如房主掉线或报错）时，恢复正常处理消息，避免客户端永远卡在缓冲状态。</summary>
	private static async Task ClientGoTimeout(NetClientGameService client, uint seq)
	{
		await Task.Delay(ReadyTimeoutMs * 2);
		if (_activeSeq == seq && _pendingRun != null && _busy && !_reloading)
		{
			MainFile.Logger.Info($"[客户端] 等待房主开始超时 seq={seq}，恢复处理消息");
			await AbortHandshakeAsync(client, seq, "host_timeout");
		}
	}

	private static void OnGo(GetOutGoMessage message, ulong senderId)
	{
		INetGameService? net = RunManager.Instance?.NetService;
		if (net is not NetClientGameService client || senderId != _hostId || message.seq != _activeSeq || _pendingRun == null || _reloading)
		{
			return;
		}
		ClearBufferedMessages(client);
		InitializeReload(client, _pendingOnline);
		TaskHelper.RunSafely(ReloadLocally(_pendingRun));
	}

	private static void OnAbort(GetOutAbortMessage message, ulong senderId)
	{
		INetGameService? net = _reloadNet ?? RunManager.Instance?.NetService;
		if (net is not NetClientGameService client || senderId != _hostId || message.seq != _activeSeq)
		{
			return;
		}
		if (_reloading)
		{
			FailReload(new InvalidOperationException("房主已中止本次重载"));
			return;
		}
		MainFile.Logger.Info("[客户端] 房主取消了踢人");
		TaskHelper.RunSafely(AbortHandshakeAsync(client, message.seq, "host_cancelled"));
	}

	// ==================================================================
	// 握手失败：恢复旧局
	// ==================================================================

	private static async Task AbortHandshakeAsync(INetGameService net, uint seq, string noticeKey, Exception? error = null, bool notifyClients = false)
	{
		if (_activeSeq != seq || _reloading || _cancellingHandshake)
		{
			return;
		}
		_cancellingHandshake = true;
		_busy = true;
		_pendingRun = null;
		_allReady = null;
		if (error != null)
		{
			MainFile.Logger.Error($"踢人握手失败：{error}");
		}
		SceneTree tree = (SceneTree)Engine.GetMainLoop();
		await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		try
		{
			if (notifyClients)
			{
				try
				{
					net.SendMessage(new GetOutAbortMessage { seq = seq });
				}
				catch (Exception ex)
				{
					MainFile.Logger.Error($"通知客户端取消失败：{ex}");
				}
			}
			try
			{
				ReloadQuiescence.Cancel();
			}
			catch (Exception ex)
			{
				MainFile.Logger.Error($"恢复旧局发送失败：{ex}");
			}
			try
			{
				net.SetBufferMessages(false);
			}
			catch (Exception ex)
			{
				MainFile.Logger.Error($"恢复旧局消息处理失败：{ex}");
			}
			Texts.Warn(Texts.Get(noticeKey));
		}
		finally
		{
			ReloadNetworkPump.Stop();
			ResetState();
			_cancellingHandshake = false;
		}
	}

	// ==================================================================
	// 本地重载（房主和客户端都走这里）
	// ==================================================================

	private static void InitializeReload(INetGameService net, List<ulong> online)
	{
		_reloadNet = net;
		_reloadFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		_failureSent = false;
		_awaitingLoaded = online.Where(id => id != net.NetId).ToHashSet();
		_loadComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (net is NetHostGameService && _awaitingLoaded.Count == 0)
		{
			_loadComplete.TrySetResult(true);
		}
	}

	private static async Task ReloadLocally(SerializableRun save, bool sendGo = false)
	{
		RunManager runManager = RunManager.Instance;
		INetGameService net = runManager.NetService;
		_reloading = true;
		try
		{
			// 先让出一帧，避免在 NetService.Update() 派发消息的过程中拆掉整局
			SceneTree tree = (SceneTree)Engine.GetMainLoop();
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			ThrowIfReloadFailed();
			await NGame.Instance!.Transition.FadeOut();
			ThrowIfReloadFailed();
			await LoadMultiplayerRun(runManager, net, save, sendGo);
			ThrowIfReloadFailed();
			await WaitForAllLoaded(net);
			await NGame.Instance.Transition.FadeIn();
			ThrowIfReloadFailed();
			MainFile.Logger.Info($"踢人完成，本局剩余 {save.Players.Count} 名玩家");
			Texts.Info(Texts.Format("kicked", ("players", _kickedText)));
		}
		catch (Exception ex)
		{
			FailReload(ex);
			SceneTree tree = (SceneTree)Engine.GetMainLoop();
			await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
			MainFile.Logger.Error($"踢人后重载失败：{ex}");
			SuppressDisconnect = false;
			try { ReloadQuiescence.Discard(); }
			catch (Exception cleanupError) { MainFile.Logger.Info("清理暂停状态失败：" + cleanupError.Message); }
			try { runManager.CleanUp(graceful: false); }
			catch (Exception cleanupError) { MainFile.Logger.Info("清理旧局失败：" + cleanupError.Message); }
			try { net.Disconnect(NetError.Quit); }
			catch (Exception cleanupError) { MainFile.Logger.Info("关闭连接失败：" + cleanupError.Message); }
			try
			{
				await NGame.Instance!.ReturnToMainMenuWithInternalError(ex);
				Texts.Warn(Texts.Get("reload_stopped"));
			}
			catch (Exception cleanupError)
			{
				MainFile.Logger.Error($"返回主菜单失败：{cleanupError}");
			}
		}
		finally
		{
			ReloadNetworkPump.Stop();
			ReloadQuiescence.Discard();
			ResetState();
		}
	}

	private static async Task LoadMultiplayerRun(RunManager runManager, INetGameService net, SerializableRun save, bool sendGo)
	{
		CleanUpForReload(runManager);
		ReloadQuiescence.Discard();
		net.SetBufferMessages(true);
		if (!net.IsConnected)
		{
			throw new InvalidOperationException("重载期间连接已断开");
		}
		if (sendGo)
		{
			ClearBufferedMessages(net);
			net.SendMessage(new GetOutGoMessage { seq = _activeSeq });
		}

		LoadRunLobby lobby = new LoadRunLobby(net, SilentLobbyListener.Create(), save);
		try
		{
			// 只有在线的人进大厅；存档里其他还没回来的人照原版读档那样保持断线，回来后可以重新加入
			List<ulong> playerIds = _pendingOnline.Where(id => save.Players.Any(p => p.NetId == id)).ToList();
			LobbyCompat.AddReadyPlayers(lobby, playerIds, net);
			NGame.Instance!.RemoteCursorContainer.Initialize(lobby.InputSynchronizer, playerIds);
			NGame.Instance.ReactionContainer.InitializeNetworking(net);
			RunState runState = RunState.FromSerializable(save);
			// 房主这里会把删过人的存档写盘（原版读档时递增重载次数顺带保存）
			await runManager.SetUpSavedMultiplayer(runState, lobby);
			ThrowIfReloadFailed();
			await NGame.Instance.LoadRun(runState, save.PreFinishedRoom);
			ThrowIfReloadFailed();
		}
		finally
		{
			lobby.CleanUp(disconnectSession: false);
		}
	}

	private static void CleanUpForReload(RunManager runManager)
	{
		List<IDisposable> leftovers = CollectLeftovers(runManager);
		SuppressDisconnect = true;
		try
		{
			runManager.CleanUp(graceful: false);
		}
		finally
		{
			SuppressDisconnect = false;
		}
		foreach (IDisposable item in leftovers)
		{
			try
			{
				item.Dispose();
			}
			catch (Exception ex)
			{
				MainFile.Logger.Info($"[重载] 释放 {item.GetType().Name} 失败：{ex.Message}");
			}
		}
	}

	private static List<IDisposable> CollectLeftovers(RunManager runManager)
	{
		List<IDisposable> list = new List<IDisposable>();
		foreach (string name in LeftoverDisposables)
		{
			try
			{
				if (AccessTools.Property(typeof(RunManager), name)?.GetValue(runManager) is IDisposable disposable)
				{
					list.Add(disposable);
				}
			}
			catch
			{
			}
		}
		return list;
	}

	// ==================================================================
	// 失败处理与加载确认
	// ==================================================================

	public static void ThrowIfReloadFailed()
	{
		if (_reloadFailure?.Task.IsCompleted == true)
		{
			throw _reloadFailure.Task.Result;
		}
	}

	public static void FailReload(Exception error)
	{
		if (_reloadFailure?.TrySetResult(error) != true || _failureSent || _reloadNet == null)
		{
			return;
		}
		_failureSent = true;
		try
		{
			_reloadNet.SendMessage(new GetOutFailedMessage { seq = _activeSeq });
		}
		catch (Exception ex)
		{
			MainFile.Logger.Info("通知其他玩家结束重载失败：" + ex.Message);
		}
	}

	private static void OnReloadFailed(GetOutFailedMessage message, ulong senderId)
	{
		if (message.seq != _activeSeq || _reloadNet == null || !_reloading
			|| (_reloadNet is NetClientGameService && senderId != _hostId)
			|| !_pendingOnline.Contains(senderId))
		{
			return;
		}
		if (_reloadNet is NetClientGameService)
		{
			_failureSent = true;
		}
		FailReload(new InvalidOperationException($"玩家 {senderId} 已中止本次重载"));
	}

	private static async Task WaitForAllLoaded(INetGameService net)
	{
		Task completed = _loadComplete?.Task ?? throw new InvalidOperationException("加载确认状态丢失");
		if (net is NetClientGameService)
		{
			net.SendMessage(new GetOutLoadedMessage { seq = _activeSeq });
		}
		Task<Exception> failed = _reloadFailure?.Task ?? throw new InvalidOperationException("重载状态丢失");
		Task finished = await Task.WhenAny(completed, failed, Task.Delay(ReadyTimeoutMs * 3));
		ThrowIfReloadFailed();
		if (finished != completed)
		{
			throw new TimeoutException("等待所有玩家完成重载超时");
		}
		if (net is NetHostGameService)
		{
			net.SendMessage(new GetOutCompleteMessage { seq = _activeSeq });
		}
	}

	private static void OnReloadLoaded(GetOutLoadedMessage message, ulong senderId)
	{
		if (_reloadNet is NetHostGameService && message.seq == _activeSeq && _awaitingLoaded.Remove(senderId)
			&& _awaitingLoaded.Count == 0)
		{
			_loadComplete?.TrySetResult(true);
		}
	}

	private static void OnReloadComplete(GetOutCompleteMessage message, ulong senderId)
	{
		if (_reloadNet is NetClientGameService && message.seq == _activeSeq && senderId == _hostId)
		{
			_loadComplete?.TrySetResult(true);
		}
	}

	// ==================================================================
	// 杂项
	// ==================================================================

	private static void StartNetworkPump(INetGameService net)
	{
		uint seq = _activeSeq;
		ReloadNetworkPump.Start(net, error =>
		{
			if (_activeSeq != seq || !_busy)
			{
				return;
			}
			if (_reloading)
			{
				FailReload(error);
			}
			else
			{
				TaskHelper.RunSafely(AbortHandshakeAsync(net, seq, "no_response", error, net is NetHostGameService));
			}
		});
	}

	private static void ResetState()
	{
		_reloading = false;
		_busy = false;
		_pendingRun = null;
		_pendingKicked = new List<ulong>();
		_pendingOnline = new List<ulong>();
		_allReady = null;
		_reloadNet = null;
		_reloadFailure = null;
		_loadComplete = null;
	}

	private static void ClearBufferedMessages(INetGameService net)
	{
		try
		{
			object? bus = AccessTools.Field(net.GetType(), "_messageBus")?.GetValue(net);
			if (bus != null && AccessTools.Field(bus.GetType(), "_bufferedMessages")?.GetValue(bus) is IList list)
			{
				list.Clear();
			}
		}
		catch (Exception ex)
		{
			MainFile.Logger.Info($"清空缓冲失败：{ex.Message}");
		}
	}

	private static string DescribeTarget(SerializableRun save)
	{
		return $"第 {save.CurrentActIndex + 1} 幕第 {save.FloorReached + 1} 层";
	}
}
