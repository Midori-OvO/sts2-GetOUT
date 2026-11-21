using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Saves.Runs;
using STS2RitsuLib;
using STS2RitsuLib.Ui.Toast;
using STS2RitsuLib.Utils;

namespace GetOUT;

/// <summary>
/// 文本放在 dll 内嵌的 localization/&lt;语言&gt;.json，由 RitsuLib 的 I18N 按游戏语言加载，缺失时回退英文。
/// 同一份文本还注册成游戏的虚拟 LocTable，给只收 LocString 的原版弹窗用。
/// 占位符统一写成 {name}：自己显示时用 Format 替换，交给 LocString 时用 Add 赋值。
/// </summary>
internal static class Texts
{
	private static I18N? _i18n;
	private static string _tableId = "";

	public static void Init()
	{
		_i18n = new I18N("GetOUT", null, new[] { "GetOUT.localization" }, null, typeof(Texts).Assembly);
		if (RitsuLibFramework.RegisterI18NLocTableBridge(MainFile.ModId, _i18n))
		{
			_tableId = RitsuLibFramework.GetI18NLocTableId(MainFile.ModId);
		}
		else
		{
			MainFile.Logger.Warn("注册本地化表失败，确认弹窗改用 mod 自己的界面");
		}
	}

	public static string Get(string key)
	{
		return _i18n?.Get(key, key) ?? key;
	}

	public static string Format(string key, params (string Name, object Value)[] args)
	{
		string text = Get(key);
		foreach ((string name, object value) in args)
		{
			text = text.Replace("{" + name + "}", Convert.ToString(value));
		}
		return text;
	}

	public static bool HasLocTable => _tableId.Length > 0;

	public static LocString Loc(string key, params (string Name, object Value)[] args)
	{
		LocString loc = new LocString(_tableId, key);
		foreach ((string name, object value) in args)
		{
			loc.Add(name, Convert.ToString(value) ?? "");
		}
		return loc;
	}

	public static void Info(string body)
	{
		Toast(RitsuToastLevel.Info, body);
	}

	public static void Warn(string body)
	{
		Toast(RitsuToastLevel.Warning, body);
	}

	private static void Toast(RitsuToastLevel level, string body)
	{
		try
		{
			string title = Get("title");
			if (level == RitsuToastLevel.Warning)
			{
				RitsuToastService.ShowWarning(body, title, null);
			}
			else
			{
				RitsuToastService.ShowInfo(body, title, null);
			}
		}
		catch (Exception ex)
		{
			MainFile.Logger.Warn("显示提示失败：" + ex.Message);
		}
	}

	/// <summary>玩家名（纯文本）。读不到时退回 id。</summary>
	public static string PlayerName(PlatformType platform, ulong playerId)
	{
		try
		{
			string name = ReadPlayerName(platform, playerId);
			return string.IsNullOrWhiteSpace(name) ? playerId.ToString() : name;
		}
		catch
		{
			return playerId.ToString();
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static string ReadPlayerName(PlatformType platform, ulong playerId)
	{
		return PlatformUtil.GetPlayerNameRaw(platform, playerId);
	}

	public static string CharacterName(SerializablePlayer player)
	{
		try
		{
			return ModelDb.GetById<CharacterModel>(player.CharacterId!).Title.GetFormattedText();
		}
		catch
		{
			return player.CharacterId?.ToString() ?? "?";
		}
	}

	/// <summary>"名字（角色）"，多个人用顿号或逗号连起来。</summary>
	public static string Describe(PlatformType platform, IEnumerable<SerializablePlayer> players)
	{
		List<string> parts = new List<string>();
		foreach (SerializablePlayer player in players)
		{
			parts.Add(Format("player_desc", ("name", PlayerName(platform, player.NetId)), ("character", CharacterName(player))));
		}
		return string.Join(Get("list_sep"), parts);
	}
}
