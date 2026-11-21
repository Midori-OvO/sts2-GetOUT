using System;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using STS2RitsuLib;
using STS2RitsuLib.Patching.Core;

namespace GetOUT;

[ModInitializer("Initialize")]
public static class MainFile
{
	public const string ModId = "GetOUT";

	/// <summary>输出自带 [GetOUT] 前缀。</summary>
	public static Logger Logger { get; private set; } = null!;

	public static void Initialize()
	{
		if (!VersionGate.IsSupported(out string version))
		{
			GD.Print($"[GetOUT] 游戏版本 {version} 未经验证（支持：{VersionGate.SupportedList}），GetOUT! 已停用全部功能。请等待 mod 更新。");
			return;
		}
		try
		{
			Logger = RitsuLibFramework.CreateLogger(ModId);
			Texts.Init();

			ModPatcher core = RitsuLibFramework.CreatePatcher(ModId, "core", "core");
			Patches.RegisterCore(core);
			if (!RitsuLibFramework.ApplyRequiredPatcher(core, () => Logger.Error("核心补丁失败，GetOUT! 已停用")))
			{
				return;
			}

			// 下面两组失败只影响各自的功能
			ModPatcher lobby = RitsuLibFramework.CreatePatcher(ModId, "lobby", "lobby");
			Patches.RegisterLobby(lobby);
			if (!lobby.PatchAll())
			{
				Logger.Warn("读档大厅的踢人按钮不可用");
			}
			ModPatcher extras = RitsuLibFramework.CreatePatcher(ModId, "extras", "extras");
			Patches.RegisterExtras(extras);
			extras.PatchAll();

			Logger.Info($"已加载（游戏版本 {version}）");
		}
		catch (Exception ex)
		{
			GD.PrintErr("[GetOUT] 初始化失败：" + ex);
		}
	}
}
