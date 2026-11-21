using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace GetOUT;

/// <summary>在 ESC 暂停菜单里加入“踢出玩家”按钮，沿用原版“保存并退出”的样式。只有多人局的房主看得到。</summary>
internal static class PauseMenuEntry
{
	private const string NodeName = "GetOUTKick";

	public static void Inject(NPauseMenu menu)
	{
		if (RunManager.Instance?.NetService?.Type != NetGameType.Host)
		{
			return;
		}
		Control container = menu.GetNode<Control>("%ButtonContainer");
		if (container.HasNode(NodeName))
		{
			return;
		}
		NPauseMenuButton template = container.GetNode<NPauseMenuButton>("SaveAndQuit");
		// 不带 Signals 标志：原按钮上连着“保存并退出”的回调，不能一起复制过来
		const int flags = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);
		NPauseMenuButton button = (NPauseMenuButton)template.Duplicate(flags);
		button.Name = NodeName;
		TextureRect image = button.GetNode<TextureRect>("ButtonImage");
		if (image.Material != null)
		{
			// 悬停效果是改 shader 参数，材质共用的话两个按钮会一起变亮
			image.Material = (Material)image.Material.Duplicate();
		}
		container.AddChild(button);
		container.MoveChild(button, template.GetIndex());
		if (container is not Container)
		{
			button.Position = template.Position;
			template.Position += new Vector2(0f, template.Size.Y);
		}
		button.GetNode<MegaLabel>("Label").SetTextAutoSize(Texts.Get("pause_button"));
		button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OnPressed()));
		UpdateNavigation(menu);
	}

	/// <summary>键盘/手柄上下切换按钮时的焦点顺序，按当前可见的按钮重建。</summary>
	public static void UpdateNavigation(NPauseMenu menu)
	{
		NPauseMenuButton[] buttons = menu.GetNode<Control>("%ButtonContainer").GetChildren()
			.OfType<NPauseMenuButton>().Where(button => button.Visible).ToArray();
		for (int i = 0; i < buttons.Length; i++)
		{
			NPauseMenuButton button = buttons[i];
			button.FocusNeighborLeft = button.GetPath();
			button.FocusNeighborRight = button.GetPath();
			button.FocusNeighborTop = buttons[(i + buttons.Length - 1) % buttons.Length].GetPath();
			button.FocusNeighborBottom = buttons[(i + 1) % buttons.Length].GetPath();
			button.FocusPrevious = button.FocusNeighborTop;
			button.FocusNext = button.FocusNeighborBottom;
		}
	}

	/// <summary>NPauseMenu.CloseToMenu 的 prefix：原版点“保存并退出”后会禁用所有按钮，我们的按钮也跟着禁用。</summary>
	public static void Disable(NPauseMenu menu)
	{
		menu.GetNodeOrNull<NPauseMenuButton>("%ButtonContainer/" + NodeName)?.Disable();
	}

	private static void OnPressed()
	{
		if (Reloader.Quitting || Reloader.IsBusy)
		{
			return;
		}
		try
		{
			NCapstoneContainer.Instance?.Close();
			NRun.Instance?.GlobalUi.TopBar.Pause.ToggleAnimState();
			KickMenu.Open();
		}
		catch (Exception ex)
		{
			MainFile.Logger.Error("打开踢人列表失败：" + ex);
		}
	}
}
