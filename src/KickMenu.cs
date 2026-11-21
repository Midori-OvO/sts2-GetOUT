using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace GetOUT;

/// <summary>从 ESC 暂停菜单打开的断线玩家列表。点“踢出”并确认后，房主发起局内踢人。</summary>
internal static class KickMenu
{
	private static CanvasLayer? _layer;

	public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer);

	public static void Close()
	{
		if (IsOpen)
		{
			_layer!.QueueFree();
		}
		_layer = null;
	}

	public static void Open()
	{
		if (IsOpen || !Reloader.CanKick(out NetHostGameService host))
		{
			return;
		}
		SerializableRun? save = RunSaves.Latest;
		if (save == null)
		{
			Texts.Warn(Texts.Get("no_save"));
			return;
		}
		List<SerializablePlayer> offline = Reloader.OfflinePlayers(host, save);
		if (offline.Count == 0)
		{
			Texts.Info(Texts.Get("nobody_offline"));
			return;
		}

		_layer = new CanvasLayer { Layer = 120 };

		ColorRect dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
		dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_layer.AddChild(dim);

		CenterContainer center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_layer.AddChild(center);

		PanelContainer panel = new PanelContainer { CustomMinimumSize = new Vector2(760f, 0f) };
		panel.AddThemeStyleboxOverride("panel", Ui.Panel(28f, 22f));
		center.AddChild(panel);

		VBoxContainer layout = new VBoxContainer();
		layout.AddThemeConstantOverride("separation", 12);
		panel.AddChild(layout);

		layout.AddChild(Ui.MakeLabel(Texts.Get("menu_title"), 34, new Color(0.95f, 0.85f, 0.6f)));
		layout.AddChild(Ui.MakeLabel(Texts.Get("menu_hint"), 19, new Color(0.7f, 0.7f, 0.72f)));

		VBoxContainer list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		list.AddThemeConstantOverride("separation", 8);
		layout.AddChild(list);

		foreach (SerializablePlayer player in offline)
		{
			ulong id = player.NetId;
			string name = Texts.PlayerName(host.Platform, id);
			string character = Texts.CharacterName(player);

			PanelContainer row = new PanelContainer();
			row.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.16f, 0.17f, 0.21f), 6f, 16f, 10f));
			HBoxContainer line = new HBoxContainer();
			line.AddThemeConstantOverride("separation", 16);
			row.AddChild(line);

			string text = Texts.Format("player_desc", ("name", name), ("character", character)) + "\n"
				+ Texts.Format("player_stats", ("hp", player.CurrentHp), ("maxHp", player.MaxHp), ("gold", player.Gold));
			Label label = Ui.MakeLabel(text, 22, new Color(0.92f, 0.92f, 0.92f));
			label.AutowrapMode = TextServer.AutowrapMode.Off;
			label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			line.AddChild(label);

			Button kick = Ui.MakeDangerButton(Texts.Get("kick_button"), 22);
			kick.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
			kick.Pressed += () => TaskHelper.RunSafely(ConfirmAndKick(id, name, character));
			line.AddChild(kick);

			list.AddChild(row);
		}

		Button cancel = Ui.MakeButton(Texts.Get("cancel"), 22);
		cancel.CustomMinimumSize = new Vector2(0f, 52f);
		cancel.Pressed += Close;
		layout.AddChild(cancel);

		NGame.Instance!.AddChildSafely(_layer);
	}

	private static async Task ConfirmAndKick(ulong id, string name, string character)
	{
		// 先关列表，确认弹窗才能接收点击
		Close();
		bool ok = await Ui.Confirm("confirm_run", ("name", name), ("character", character));
		if (ok)
		{
			Reloader.RequestKick(new[] { id });
		}
	}
}
