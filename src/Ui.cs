using System;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace GetOUT;

/// <summary>代码搭的控件样式。字体按当前语言做和游戏相同的替换，保证中日韩泰俄等文字能显示。</summary>
internal static class Ui
{
	public static StyleBoxFlat Box(Color color, float corner = 6f, float marginX = 16f, float marginY = 8f)
	{
		int radius = (int)corner;
		return new StyleBoxFlat
		{
			BgColor = color,
			CornerRadiusTopLeft = radius,
			CornerRadiusTopRight = radius,
			CornerRadiusBottomLeft = radius,
			CornerRadiusBottomRight = radius,
			ContentMarginLeft = marginX,
			ContentMarginRight = marginX,
			ContentMarginTop = marginY,
			ContentMarginBottom = marginY
		};
	}

	public static StyleBoxFlat Panel(float marginX, float marginY)
	{
		StyleBoxFlat box = Box(new Color(0.09f, 0.1f, 0.13f, 0.97f), 10f, marginX, marginY);
		box.BorderColor = new Color(0.78f, 0.64f, 0.38f);
		box.BorderWidthLeft = 2;
		box.BorderWidthTop = 2;
		box.BorderWidthRight = 2;
		box.BorderWidthBottom = 2;
		return box;
	}

	public static Label MakeLabel(string text, int size, Color color)
	{
		Label label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		label.AddThemeFontSizeOverride("font_size", size);
		label.AddThemeColorOverride("font_color", color);
		label.ApplyLocaleFontSubstitution(FontType.Regular, "font");
		return label;
	}

	public static Button MakeButton(string text, int size)
	{
		Button button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
		button.AddThemeFontSizeOverride("font_size", size);
		button.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.92f));
		button.AddThemeColorOverride("font_hover_color", new Color(1f, 0.92f, 0.7f));
		button.AddThemeStyleboxOverride("normal", Box(new Color(0.16f, 0.17f, 0.21f)));
		button.AddThemeStyleboxOverride("hover", Box(new Color(0.27f, 0.24f, 0.18f)));
		button.AddThemeStyleboxOverride("pressed", Box(new Color(0.36f, 0.3f, 0.18f)));
		button.ApplyLocaleFontSubstitution(FontType.Regular, "font");
		return button;
	}

	/// <summary>红色的危险操作按钮（踢人）。</summary>
	public static Button MakeDangerButton(string text, int size)
	{
		Button button = MakeButton(text, size);
		button.AddThemeStyleboxOverride("normal", Box(new Color(0.42f, 0.12f, 0.12f), 6f, 12f, 4f));
		button.AddThemeStyleboxOverride("hover", Box(new Color(0.58f, 0.16f, 0.14f), 6f, 12f, 4f));
		button.AddThemeStyleboxOverride("pressed", Box(new Color(0.7f, 0.2f, 0.16f), 6f, 12f, 4f));
		return button;
	}

	/// <summary>
	/// 二次确认。优先用游戏原版的确认弹窗；本地化表没注册上时，用 mod 自己搭的简单对话框。
	/// 文本键约定：&lt;key&gt;.header / &lt;key&gt;.body / &lt;key&gt;.confirm / &lt;key&gt;.cancel。
	/// </summary>
	public static Task<bool> Confirm(string key, params (string Name, object Value)[] args)
	{
		if (Texts.HasLocTable && NModalContainer.Instance != null)
		{
			NGenericPopup? popup = NGenericPopup.Create();
			if (popup != null)
			{
				NModalContainer.Instance.Add(popup);
				(string, object)[] escaped = Array.ConvertAll(args, a => (a.Name, (object)EscapeBbcode(Convert.ToString(a.Value) ?? "")));
				return popup.WaitForConfirmation(Texts.Loc(key + ".body", escaped), Texts.Loc(key + ".header"),
					Texts.Loc(key + ".cancel"), Texts.Loc(key + ".confirm"));
			}
		}
		return SimpleConfirm(Texts.Get(key + ".header"), Texts.Format(key + ".body", args),
			Texts.Get(key + ".confirm"), Texts.Get(key + ".cancel"));
	}

	private static Task<bool> SimpleConfirm(string header, string body, string confirm, string cancel)
	{
		TaskCompletionSource<bool> result = new TaskCompletionSource<bool>();
		CanvasLayer layer = new CanvasLayer { Layer = 130 };
		ColorRect dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.6f), MouseFilter = Control.MouseFilterEnum.Stop };
		dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(dim);
		CenterContainer center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(center);
		PanelContainer panel = new PanelContainer { CustomMinimumSize = new Vector2(640f, 0f) };
		panel.AddThemeStyleboxOverride("panel", Panel(28f, 22f));
		center.AddChild(panel);
		VBoxContainer layout = new VBoxContainer();
		layout.AddThemeConstantOverride("separation", 16);
		panel.AddChild(layout);
		layout.AddChild(MakeLabel(header, 30, new Color(0.95f, 0.85f, 0.6f)));
		layout.AddChild(MakeLabel(body, 20, new Color(0.88f, 0.88f, 0.9f)));
		HBoxContainer buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
		buttons.AddThemeConstantOverride("separation", 12);
		layout.AddChild(buttons);
		Button no = MakeButton(cancel, 22);
		Button yes = MakeDangerButton(confirm, 22);
		buttons.AddChild(no);
		buttons.AddChild(yes);
		void Finish(bool value)
		{
			if (result.TrySetResult(value))
			{
				layer.QueueFree();
			}
		}
		no.Pressed += () => Finish(false);
		yes.Pressed += () => Finish(true);
		NGame.Instance!.AddChildSafely(layer);
		return result.Task;
	}

	/// <summary>玩家名是外部输入，放进富文本前把方括号转义，免得被当成 BBCode 标签。</summary>
	public static string EscapeBbcode(string text)
	{
		StringBuilder sb = new StringBuilder(text.Length);
		foreach (char c in text)
		{
			sb.Append(c switch { '[' => "[lb]", ']' => "[rb]", _ => c.ToString() });
		}
		return sb.ToString();
	}
}
