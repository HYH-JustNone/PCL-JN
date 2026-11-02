# PCL-JN

PCL-JN 是基于 [Plain Craft Launcher 2（PCL）](https://github.com/Meloong-Git/PCL/) 开源代码的个人修改版本，由 **just_none** 维护，与 PCL 官方无关。

## 本版本的主要改动
- 默认关闭自动更新提示
- 移除主题切换功能，界面固定为龙猫蓝配色
- 主界面新增「第三方」登录入口（Authlib-Injector）
- 设置页新增「账号与密钥」卡片：微软登录 Client ID、CurseForge API Key 由使用者自行填写
- 补齐「百宝箱」页面：今日人品、内存优化、清理游戏垃圾、回声洞、自定义文件下载等
- 标题栏徽标改为「PCL · JN」
- 「关于与鸣谢」中补充本版本说明
- 新增插件系统，支持 Lua 脚本、VB / C# 脚本与 dll 插件，无需编译即可扩展功能

## 构建方式

需要 Visual Studio（含 .NET Framework 4.8 Developer Pack）以及 .NET SDK 8，然后使用 MSBuild 编译 `Plain Craft Launcher 2.vbproj`。

```
MSBuild "Plain Craft Launcher 2.vbproj" -p:Configuration=Release
```

## 版权与许可

本项目采用**分层许可**，两部分须同时遵守：

| 范围 | 对应文件 | 说明 |
| --- | --- | --- |
| 来自 PCL 的全部代码 | [LICENCE](./LICENCE) | 《PCL 分发有限许可》与《PCL 存储库合理使用指南》|
| PCL-JN 新增/修改的部分 | [LICENSE-PCL-JN](./LICENSE-PCL-JN) | MIT |

- 原项目：**Plain Craft Launcher（PCL）**，作者 **龙腾猫跃（LTCat）**，仓库：<https://github.com/Meloong-Git/PCL/>
- 本项目属于《PCL 存储库合理使用指南》中的**重度使用**：基于原仓库修改，与 PCL 官方无关，名称以 PCL 开头并带 `-JN` 后缀以示区分。
- 本项目完整保留原项目的版权声明、许可条款，以及「关于与鸣谢」页面中的原作者署名。
- 二次分发本版本时，请同样遵守 `LICENCE`：保留署名、公开源代码、保留许可声明、界面主色相保持蓝色、不加入赞助解锁类功能、保留 Minecraft 正版购买劝导。
- `LICENSE-PCL-JN`（MIT）只适用于本项目新添加的模块，不能用于替代或放宽 `LICENCE`。

## 插件开发

插件放在 `%AppData%\PCL\Plugins\<插件id>\`，一个插件就是一个文件夹：`plugin.json` + 入口文件。

入口文件支持三种：

| 扩展名 | 说明 |
| --- | --- |
| `.lua` | **Lua 脚本**，语法简单，运行在沙箱中，推荐普通用户使用 |
| `.vb` / `.cs` | VB / C# 脚本，启动时自动编译，不需要安装 SDK（语法级别较旧） |
| `.dll` | 预编译插件，适合需要引用其它第三方库的场景 |

`plugin.json` 示例：

```json
{
  "id": "my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "插件说明",
  "entry": "plugin.lua",
  "permissions": ["network", "file"],
  "platform": ["win-x64"],
  "requires": [],
  "min_launcher_version": "0.0.1.0",
  "update": "https://example.com/my-plugin/version.json"
}
```

`id` 和 `entry` 为必填项。插件的启用状态保存在 `%AppData%\PCL\Plugins\plugins.json`，在设置页修改开关后需要重启启动器才会生效。

`permissions` 是权限声明，会显示在设置页的插件列表里：

| 权限 | 允许做的事 |
| --- | --- |
| `network` | 调用 `pcl.HttpGet` / `pcl.HttpPost` 访问网络 |
| `file` | 读写插件自己目录下的文件 |
| `command` | 调用 `pcl.RunCommand` 执行外部命令（仍需用户逐次确认） |

不声明就调不通对应接口——这是给用户看的"这个插件想干什么"。

`requires` 可声明依赖的其它插件 id，缺少依赖时会拒绝加载并在设置页显示原因。`update` 填一个返回 `{"version":"1.2.0"}` 的地址，启动时会自动检查并提示新版本（不会自动更新）。`platform` 可限定平台（`win-x64` / `win-arm64` / `win-x86`），留空表示不限制。`min_launcher_version` 声明需要的最低启动器版本，版本不够时插件不会加载并在列表里说明原因。

每个插件还有自己的日志：`Plugins\<id>\log.txt`，`pcl.Log` 会同时写进去。

插件列表里加载成功的插件后面有「重载」按钮：改完 `.lua` / `.vb` / `.cs` 脚本点一下就能立即生效，不用重启启动器（`.dll` 插件仍然需要重启）。

### 插件包与插件市场

插件可以打包成 `.pjnp` 文件（本质是 zip），结构如下：

```
my-plugin.pjnp
├─ plugin.json
├─ plugin.lua
└─ （其它资源）
```

设置页「安装插件包」选择 `.pjnp` 即可自动解压到 `Plugins\<id>\`；「浏览插件」会读取插件市场索引，支持搜索与一键安装（下载后校验 SHA256）。

市场索引是一个 JSON：

```json
{
  "plugins": [
    {
      "id": "startup-counter",
      "name": "启动计数器",
      "version": "1.0.0",
      "author": "just_none",
      "description": "记录每个版本启动了多少次",
      "permissions": ["file"],
      "platform": ["win-x64"],
      "download": "https://example.com/startup-counter.pjnp",
      "sha256": "……",
      "size": 12345
    }
  ]
}
```

默认索引地址是 `https://raw.githubusercontent.com/HYH-JustNone/PCL-JN/dev/plugins/index.json`，结果会缓存 6 小时，可在界面里点「刷新」强制更新。市场里已安装且有更新版本的插件会显示「更新」按钮，升级时会保留原来的 `settings.json` 与 `log.txt`（DLL 插件因为文件被占用，需要关掉启动器再手动替换）。

仓库里的 [`examples/startup-counter`](./examples/startup-counter) 是一个可以直接跑起来的示例插件，[`examples/startup-counter.pjnp`](./examples/startup-counter.pjnp) 是它的打包结果。

插件加载失败时，列表里会给出「禁用 / 删除 / 打开目录」三个操作，方便直接处理坏掉的插件。

### 写一个 Lua 脚本插件（推荐）

新建 `plugin.lua`，用记事本写就行：

```lua
function OnLoad()
    pcl.Log("插件已加载，启动器版本 " .. pcl.Version)
    pcl.Hint("欢迎回来！")
end

function OnGameLaunching(name)
    pcl.Log("即将启动 " .. name)
end

function OnGameExited(code)
    pcl.Log("游戏已退出，退出码 " .. code)
end
```

Lua 插件运行在 **MoonSharp 硬沙箱**中：没有 `io`、`os`、`require`，不能直接碰文件系统和命令行，只能调用下面这些受控接口。

| 接口 | 说明 |
| --- | --- |
| `pcl.Log(文本)` | 写入启动器日志 |
| `pcl.Hint(文本)` / `pcl.Warn(文本)` | 左下角提示（Warn 为红色） |
| `pcl.GetSetting(键)` / `pcl.SetSetting(键, 值)` | 插件私有设置，存盘，重启后还在 |
| `pcl.GetInstance()` | 返回 `{name, loader, game_version, mods, path}`，没选中实例时返回空字符串 |
| `pcl.GetVersions()` | 所有已安装版本，每项含 `name` / `loader` / `game_version` |
| `pcl.GetMods()` | 当前实例的 Mod 列表，每项含 `file` / `id` / `name` / `version` / `loader` |
| `pcl.GetJavaList()` | 已识别的 Java，每项含 `path` / `version` |
| `pcl.GetMemoryInfo()` | 返回 `{total_mb, available_mb}` |
| `pcl.GetBackups()` | 当前实例的备份列表，每项含 `name` / `path` / `time` |
| `pcl.GetCrashReports()` | 最近 10 份崩溃报告，每项含 `file` / `path` / `time` |
| `pcl.GetOsInfo()` | 返回 `{os, cpu_count, total_mb, is_64bit}` |
| `pcl.HttpGet(网址)` / `pcl.HttpPost(网址, 内容)` | 同步 HTTP 请求，需要 `network` 权限 |
| `pcl.JsonEncode(值)` / `pcl.JsonDecode(文本)` | Lua 表与 JSON 互转 |
| `pcl.Launch(版本名)` | 弹窗确认后启动指定版本 |
| `pcl.SelectInstance(版本名)` | 切换当前选中的版本 |
| `pcl.StopGame()` | 停止启动或关闭正在运行的游戏 |
| `pcl.RestoreBackup(目录)` | 弹窗确认后回滚备份 |
| `pcl.CopyToClipboard(文本)` / `pcl.ShowDialog(标题, 内容)` | 复制到剪贴板 / 弹出对话框 |
| `pcl.SetTickInterval(秒)` | 以指定间隔触发 `OnTick`，传 0 关闭 |
| `pcl.AddMenuItem(文本, 函数)` | 在设置页该插件下方加一个按钮，点击执行函数 |
| `pcl.GetLaunchArgs()` | 返回当前实例的启动参数 `{instance, jvm, game, title, info}` |
| `pcl.AddLaunchArg(类别, 参数)` | 往当前实例追加启动参数，类别为 `"jvm"` 或 `"game"`，下次启动生效，需要 `command` 权限 |
| `pcl.SetInstanceSetting(键, 值)` | 写入版本级设置（如 `VersionArgumentTitle`、`VersionRamCustom`），只允许白名单内的键，需要 `command` 权限 |
| `pcl.Now()` / `pcl.Tick()` | 当前时间文本 / Unix 毫秒时间戳 |
| `pcl.Backup()` | 立即备份当前实例的配置与存档，返回备份目录 |
| `pcl.OpenUrl(网址)` / `pcl.OpenFolder(路径)` | 打开网页 / 文件夹 |
| `pcl.RunCommand(命令)` | 执行外部命令，**会先弹窗让用户确认**，返回是否执行 |
| `pcl.GetSettingValue(键)` / `pcl.SetSettingValue(键, 值)` | 读写启动器设置，仅支持 `LaunchRamCustom`、`UiLauncherTransparent`、`LaunchArgumentRam`、`UiBackgroundColorful` |
| `pcl.ReadFile(相对路径)` / `pcl.WriteFile(相对路径, 内容)` | 读写插件自己目录下的文件，越权路径会被拒绝 |
| `pcl.Version` | 启动器版本 |

可以按需定义这些全局函数，没写的会被跳过：

| 回调 | 触发时机 | 参数 |
| --- | --- | --- |
| `OnLoad` | 插件加载完成 | 无 |
| `OnGameLaunching` | 游戏启动前 | 版本名 |
| `OnGameExited` | 游戏进程退出 | 退出码 |
| `OnGameCrashed` | 崩溃并完成分析 | 崩溃报告目录 |
| `OnPageChanged` | 切换页面 | 页面名 |
| `OnLaunchFailed` | 启动失败 | 错误信息 |
| `OnVersionInstalled` | 版本安装完成 | 加载器名 |
| `OnInstanceSelected` | 选中版本变化 | 版本名 |
| `OnTick` | `pcl.SetTickInterval` 设置的定时器 | 无 |
| `OnUnload` | 插件卸载或启动器关闭 | 无 |

需要界面上有个按钮时，用 `pcl.AddMenuItem` 注册（按钮会出现在设置页该插件下方）：

```lua
pcl.AddMenuItem("导出 Mod 清单", function()
    local text = ""
    for _, mod in ipairs(pcl.GetMods()) do
        text = text .. mod.name .. " " .. mod.version .. "\n"
    end
    pcl.WriteFile("mods.txt", text)   -- 需要 file 权限
    pcl.Hint("已导出，共 " .. #pcl.GetMods() .. " 个")
end)
```

> `OnLoad` 执行时启动器还没扫描完版本，`pcl.GetInstance()` 和 `pcl.GetVersions()` 可能返回空；需要这些信息请在 `OnGameLaunching` 或菜单函数里取。
>
> 每个回调都有超时保护（`OnLoad` 5 秒，其余 15 秒），脚本里写 `while true do end` 会被强制中断，不会卡死启动器。

### 写一个 VB / C# 脚本插件

新建 `plugin.vb`，直接用记事本写就行：

```vb
Imports PCL.Plugin

Public Class MyPlugin
    Implements IPlugin

    Private Context As IPluginContext

    Public Sub OnLoad(Context As IPluginContext) Implements IPlugin.OnLoad
        Me.Context = Context
        Context.Log("我的插件已加载")
        Context.ShowHint("我的插件已加载！")
    End Sub

    Public Sub OnUnload() Implements IPlugin.OnUnload
    End Sub

    Public Sub OnGameLaunching(InstanceName As String) Implements IPlugin.OnGameLaunching
        If Context IsNot Nothing Then Context.Log("即将启动：" & InstanceName)
    End Sub

    Public Sub OnGameExited(ExitCode As Integer) Implements IPlugin.OnGameExited
        If Context IsNot Nothing Then Context.Log("游戏已退出，退出码：" & ExitCode)
    End Sub

End Class
```

也可以写成 `plugin.cs`（C#）：

```csharp
using PCL.Plugin;

public class MyPlugin : IPlugin
{
    private IPluginContext _context;

    public void OnLoad(IPluginContext context)
    {
        _context = context;
        context.Log("我的插件已加载");
    }

    public void OnUnload() { }
    public void OnGameLaunching(string instanceName) { }
    public void OnGameExited(int exitCode) { }
}
```

保存后重启启动器即可生效。脚本编译失败时不会影响启动器，错误信息（第几行、什么错）会显示在 设置 → 启动 → 插件 里。

> 脚本由 .NET Framework 自带的编译器编译，语法级别相当于 **C# 5 / VB 12**：不要使用 `?.`、`$""` 字符串插值、`nameof`、`async/await` 之外的新特性，否则会编译失败。需要现代语法或引用其它第三方库时，请改用预编译的 `.dll` 插件。

### 可用的回调

- `OnLoad`：插件加载完成时调用
- `OnGameLaunching`：游戏正式启动前调用，参数为版本名
- `OnGameExited`：游戏进程退出后调用，参数为退出码
- `OnUnload`：插件卸载或启动器退出时调用

### IPluginContext 提供的能力

- `PluginId`：插件 ID
- `DataPath`：插件私有目录，可用于存放配置或缓存
- `LauncherVersion`：当前启动器版本
- `Log`：写入启动器日志
- `ShowHint`：在启动器左下角显示提示
- `GetSetting` / `SetSetting`：读写插件私有设置，保存在插件目录的 `settings.json` 中

**警告：插件没有沙箱，拥有与本程序相同的权限。请只安装来源可信的插件，不要加载来路不明的脚本或 dll。**

## 相关链接

- [PCL-JN 仓库](https://github.com/HYH-JustNone/PCL-JN)
- [PCL 官方仓库](https://github.com/Meloong-Git/PCL/)
- [PCL 官方下载](https://meloong.com/pcl)
- [帮助文档库](https://github.com/LTCatt/PCL2Help)