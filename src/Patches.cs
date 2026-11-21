using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using STS2RitsuLib.Patching.Core;
using STS2RitsuLib.Patching.Models;

namespace GetOUT;

internal static class Patches
{
	/// <summary>局内踢人必需的补丁。任何一个失败，整个 mod 停用。</summary>
	public static void RegisterCore(ModPatcher patcher)
	{
		patcher.RegisterPatch<SaveRunPatch>();
		patcher.RegisterPatch<RecordInitialStatePatch>();
		patcher.RegisterPatch<RunCleanUpPatch>();
		patcher.RegisterPatch<LaunchPatch>();
		patcher.RegisterPatch<DisconnectPatch>();
		patcher.RegisterPatch<RunProcessPatch>();
		patcher.RegisterPatch<InputPatch>();
		patcher.RegisterPatch<PauseMenuReadyPatch>();
		patcher.RegisterPatch<PauseMenuInitializePatch>();
		patcher.RegisterPatch<CloseToMenuPatch>();
	}

	public static void RegisterLobby(ModPatcher patcher)
	{
		patcher.RegisterPatch<LobbyContainerInitializePatch>();
		patcher.RegisterPatch<LobbyContainerChangedPatch>();
		patcher.RegisterPatch<LobbyContainerCleanupPatch>();
		patcher.RegisterPatch<SoloBeginPatch>();
	}

	public static void RegisterExtras(ModPatcher patcher)
	{
		patcher.RegisterPatch<RemotePlayerDisconnectedPatch>();
		// 游戏内部字段变了时只放弃入场同步超时保护
		try
		{
			ReloadSyncGuard.Validate();
			patcher.RegisterPatch<WaitForSyncPatch>();
		}
		catch (Exception ex)
		{
			MainFile.Logger.Warn("入场同步超时保护不可用：" + ex.Message);
		}
	}
}

// ======================================================================
// 存档
// ======================================================================

/// <summary>房主每次写多人存档时记下一份，局内踢人就从这份存档里删人。</summary>
internal sealed class SaveRunPatch : IPatchMethod
{
	public static string PatchId => "save_run";
	public static bool IsCritical => false;
	public static string Description => "记录房主最近一次写盘的多人存档";

	public static ModPatchTarget[] GetTargets() =>
		new[] { new ModPatchTarget(typeof(RunSaveManager), "SaveRun", new[] { typeof(SerializableRun), typeof(bool) }, ignoreIfMissing: true) };

	public static void Prefix(SerializableRun save, bool isMultiplayer)
	{
		if (!isMultiplayer)
		{
			return;
		}
		try
		{
			RunSaves.RememberWritten(save);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Warn("记录存档失败：" + ex.Message);
		}
	}
}

/// <summary>后备：写盘补丁在某个游戏版本上挂不上时，用进房间时交给战斗回放记录的房间入口状态。</summary>
internal sealed class RecordInitialStatePatch : IPatchMethod
{
	public static string PatchId => "record_initial_state";
	public static bool IsCritical => false;
	public static string Description => "记录房间入口存档（后备）";

	public static ModPatchTarget[] GetTargets() =>
		new[] { new ModPatchTarget(typeof(CombatReplayWriter), "RecordInitialState", ignoreIfMissing: true) };

	public static void Postfix(SerializableRun serializableRun)
	{
		try
		{
			if (RunManager.Instance?.NetService?.Type == NetGameType.Host)
			{
				RunSaves.RememberRoomEntry(serializableRun);
			}
		}
		catch (Exception ex)
		{
			MainFile.Logger.Warn("记录房间入口存档失败：" + ex.Message);
		}
	}
}

/// <summary>一局结束（回主菜单、换局、我们自己重载）时丢掉旧存档，免得踢人时载入上一局。</summary>
internal sealed class RunCleanUpPatch : IPatchMethod
{
	public static string PatchId => "run_cleanup";
	public static string Description => "换局时清掉记录的存档";

	public static ModPatchTarget[] GetTargets() => new[] { new ModPatchTarget(typeof(RunManager), "CleanUp") };

	public static void Prefix()
	{
		RunSaves.Forget();
	}
}

// ======================================================================
// 不断线重载
// ======================================================================

internal sealed class LaunchPatch : IPatchMethod
{
	public static string PatchId => "launch";
	public static string Description => "注册联机消息处理器";

	public static ModPatchTarget[] GetTargets() => new[] { new ModPatchTarget(typeof(RunManager), "Launch") };

	public static void Postfix(RunManager __instance)
	{
		Reloader.Quitting = false;
		try
		{
			Reloader.EnsureHandlers(__instance.NetService);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Error("注册消息处理器失败：" + ex.Message);
		}
	}
}

/// <summary>重载时 RunManager.CleanUp 会顺手断开网络；我们要复用连接，所以这段时间拦掉。</summary>
internal sealed class DisconnectPatch : IPatchMethod
{
	public static string PatchId => "disconnect";
	public static string Description => "重载期间拦截断线";

	public static ModPatchTarget[] GetTargets() => new[]
	{
		new ModPatchTarget(typeof(NetHostGameService), "Disconnect"),
		new ModPatchTarget(typeof(NetClientGameService), "Disconnect")
	};

	public static bool Prefix()
	{
		return !Reloader.SuppressDisconnect;
	}
}

internal sealed class RunProcessPatch : IPatchMethod
{
	public static string PatchId => "run_process";
	public static string Description => "重载期间由 mod 接手联机服务的更新";

	public static ModPatchTarget[] GetTargets() => new[] { new ModPatchTarget(typeof(NRun), "_Process") };

	public static bool Prefix()
	{
		return !ReloadNetworkPump.ShouldSkipRunUpdate();
	}
}

/// <summary>
/// 重载期间吞掉所有输入；踢人列表打开时 Esc 关闭列表，其他按键不传给游戏。
/// 全部在 prefix 里处理：Harmony 的 postfix 即使 prefix 返回 false 也会执行。
/// </summary>
internal sealed class InputPatch : IPatchMethod
{
	public static string PatchId => "input";
	public static string Description => "重载和列表打开期间拦截输入";

	public static ModPatchTarget[] GetTargets() => new[] { new ModPatchTarget(typeof(NGame), "_Input") };

	public static bool Prefix(NGame __instance, InputEvent inputEvent)
	{
		if (Reloader.IsBusy)
		{
			__instance.GetViewport()?.SetInputAsHandled();
			return false;
		}
		if (!KickMenu.IsOpen || inputEvent is not InputEventKey key)
		{
			return true;
		}
		if (key.Pressed && !key.Echo && key.Keycode == Key.Escape)
		{
			KickMenu.Close();
		}
		__instance.GetViewport()?.SetInputAsHandled();
		return false;
	}
}

// ======================================================================
// 暂停菜单
// ======================================================================

internal sealed class PauseMenuReadyPatch : IPatchMethod
{
	public static string PatchId => "pause_menu_ready";
	public static string Description => "ESC 菜单加入踢人按钮";

	public static ModPatchTarget[] GetTargets() => new[] { new ModPatchTarget(typeof(NPauseMenu), "_Ready") };

	public static void Postfix(NPauseMenu __instance)
	{
		try
		{
			PauseMenuEntry.Inject(__instance);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Error("暂停菜单按钮添加失败：" + ex);
		}
	}
}

internal sealed class PauseMenuInitializePatch : IPatchMethod
{
	public static string PatchId => "pause_menu_initialize";
	public static string Description => "ESC 菜单的键盘/手柄焦点顺序";

	public static ModPatchTarget[] GetTargets() => new[] { new ModPatchTarget(typeof(NPauseMenu), "Initialize") };

	public static void Postfix(NPauseMenu __instance)
	{
		try
		{
			PauseMenuEntry.UpdateNavigation(__instance);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Error("暂停菜单导航更新失败：" + ex);
		}
	}
}

/// <summary>保存并退出期间不允许再踢人，直到下一局开始。</summary>
internal sealed class CloseToMenuPatch : IPatchMethod
{
	public static string PatchId => "close_to_menu";
	public static string Description => "保存并退出时禁用踢人按钮";

	public static ModPatchTarget[] GetTargets() => new[] { new ModPatchTarget(typeof(NPauseMenu), "CloseToMenu") };

	public static void Prefix(NPauseMenu __instance)
	{
		Reloader.Quitting = true;
		try
		{
			PauseMenuEntry.Disable(__instance);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Error("禁用暂停菜单按钮失败：" + ex.Message);
		}
	}
}

internal sealed class WaitForSyncPatch : IPatchMethod
{
	public static string PatchId => "wait_for_sync";
	public static bool IsCritical => false;
	public static string Description => "重载后战斗入场同步的超时保护";

	public static ModPatchTarget[] GetTargets() => new[] { new ModPatchTarget(typeof(CombatStateSynchronizer), "WaitForSync") };

	public static void Postfix(object __instance, ref Task __result)
	{
		ReloadSyncGuard.WaitForSyncPostfix(__instance, ref __result);
	}
}

/// <summary>有人断线时提醒房主可以踢人。</summary>
internal sealed class RemotePlayerDisconnectedPatch : IPatchMethod
{
	public static string PatchId => "remote_player_disconnected";
	public static bool IsCritical => false;
	public static string Description => "玩家断线时提示房主";

	public static ModPatchTarget[] GetTargets() =>
		new[] { new ModPatchTarget(typeof(RunManager), "RemotePlayerDisconnected", ignoreIfMissing: true) };

	public static void Postfix(RunManager __instance, ulong playerId)
	{
		try
		{
			if (Reloader.IsBusy || Reloader.Quitting || __instance.NetService is not NetHostGameService host)
			{
				return;
			}
			Texts.Info(Texts.Format("player_left", ("players", Texts.PlayerName(host.Platform, playerId))));
		}
		catch (Exception ex)
		{
			MainFile.Logger.Warn("断线提示失败：" + ex.Message);
		}
	}
}

// ======================================================================
// 读档大厅
// ======================================================================

internal sealed class LobbyContainerInitializePatch : IPatchMethod
{
	public static string PatchId => "lobby_container_initialize";
	public static string Description => "读档大厅槽位加入踢人按钮";

	public static ModPatchTarget[] GetTargets() =>
		new[] { new ModPatchTarget(typeof(NRemoteLoadLobbyPlayerContainer), "Initialize") };

	public static void Postfix(NRemoteLoadLobbyPlayerContainer __instance)
	{
		try
		{
			LobbyKick.OnInitialized(__instance);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Error("读档大厅踢人按钮初始化失败：" + ex);
		}
	}
}

/// <summary>原版的 OnPlayerConnected / OnPlayerDisconnected 最后都会走到这里。</summary>
internal sealed class LobbyContainerChangedPatch : IPatchMethod
{
	public static string PatchId => "lobby_container_changed";
	public static string Description => "有人进出读档大厅时更新踢人按钮";

	public static ModPatchTarget[] GetTargets() =>
		new[] { new ModPatchTarget(typeof(NRemoteLoadLobbyPlayerContainer), "OnPlayerChanged") };

	public static void Postfix(NRemoteLoadLobbyPlayerContainer __instance)
	{
		try
		{
			LobbyKick.Refresh(__instance);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Warn("更新踢人按钮失败：" + ex.Message);
		}
	}
}

internal sealed class LobbyContainerCleanupPatch : IPatchMethod
{
	public static string PatchId => "lobby_container_cleanup";
	public static string Description => "离开读档大厅时注销消息处理";

	public static ModPatchTarget[] GetTargets() =>
		new[] { new ModPatchTarget(typeof(NRemoteLoadLobbyPlayerContainer), "Cleanup") };

	public static void Prefix(NRemoteLoadLobbyPlayerContainer __instance)
	{
		LobbyKick.OnCleanup(__instance);
	}
}

/// <summary>原版读档大厅至少要两个人才能开局。存档里只剩房主一个人时（其他人都被踢了），允许房主一个人开局。</summary>
internal sealed class SoloBeginPatch : IPatchMethod
{
	public static string PatchId => "solo_begin";
	public static string Description => "存档只剩房主时允许一个人开局";

	public static ModPatchTarget[] GetTargets() =>
		new[] { new ModPatchTarget(typeof(LoadRunLobby), "IsAboutToBeginGame") };

	public static void Postfix(LoadRunLobby __instance, ref bool __result)
	{
		if (__result)
		{
			return;
		}
		try
		{
			ulong self = __instance.NetService.NetId;
			if (__instance.NetService.Type != NetGameType.Host || __instance.Run.Players.Count != 1
				|| __instance.Run.Players[0].NetId != self || LobbyCompat.ConnectingCount(__instance) > 0)
			{
				return;
			}
			HashSet<ulong> connected = LobbyCompat.ConnectedIds(__instance);
			__result = connected.Count == 1 && connected.Contains(self) && LobbyCompat.AllConnectedReady(__instance);
		}
		catch (Exception ex)
		{
			MainFile.Logger.Warn("单人开局检查失败：" + ex.Message);
		}
	}
}
