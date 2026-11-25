# GetOUT!：移除断线玩家，继续多人对局

《杀戮尖塔 2》多人 mod。有人断线、无法回来时，房主可以将其从当前对局和存档中移除，让剩余玩家继续游戏。

## 用法

| 场景 | 操作 |
|---|---|
| 正在对局中 | 房主打开 **ESC → 踢出玩家**，选择已断线玩家，确认后所有在线玩家保持连接并重载最近一次存档 |
| 读取多人存档的大厅 | 房主点击未连接玩家槽位上的 **踢出** 按钮，确认移除；开始游戏后才会写入存档，直接离开大厅则不会保存这次删除 |

只能移除已断线或尚未连接的玩家，不能踢出在线玩家。被移除的玩家不能再加入这一局。

局内踢人会返回最近一次存档的位置：战斗中通常回到战斗开始；战斗已经结束、正在领奖励时，保留战斗胜利后的状态。未被移除的其他断线玩家仍可重连。如果重载失败并返回主菜单，房主可以重新建房读取存档继续。

## 安装与兼容

- 支持游戏正式版 **v0.111.1** 和测试版 **v0.114.0**。其他版本会停用 GetOUT 功能。
- 所有玩家都需要安装 **RitsuLib 0.6.16 或更新版本**。0.6.16 新增了游戏 v0.114.0 的兼容分支。
- 所有玩家必须使用相同游戏版本，并安装相同版本的 GetOUT 和 RitsuLib；GetOUT 停用功能时，也仍需保持各方 mod 一致。
- 界面支持简体中文、繁体中文和英文，其他语言回退英文。

推荐订阅 [GetOUT! 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3832125542) 和 [RitsuLib 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295)。

手动安装时，从本仓库 Releases 下载 `GetOUT-<版本>.zip`，将其中的 `GetOUT` 文件夹放进 `<游戏根目录>/mods/`。文件夹内只有 `GetOUT.dll` 和 `GetOUT.json`，RitsuLib 需要另外安装。**工坊版与手动安装版只保留一种，避免重复加载。**

每个版本的更新内容见 Releases。

## 编译

需要 .NET 9 SDK 和已安装的游戏。NuGet 会还原用于编译的 RitsuLib 引用，运行时使用单独安装的 RitsuLib。

```powershell
dotnet build GetOUT.csproj -c Release "-p:Sts2GameDir=<游戏根目录>" -p:InstallToGame=false
```

也可以设置环境变量 `STS2_GAME_DIR`，省略 `Sts2GameDir` 参数。产物位于 `bin/Release/`，编译不会自动安装到游戏目录。

## 日志

游戏日志位于 `%APPDATA%/SlayTheSpire2/logs/godot.log`，GetOUT 日志带有 `[GetOUT]` 前缀。多人问题请保留所有人的日志，并在重新启动游戏前复制。
