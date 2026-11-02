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
- 新增插件系统，支持脚本插件与 dll 插件，无需编译即可扩展功能

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
| `.vb` / `.cs` | **脚本插件**，启动器启动时自动编译，不需要安装 SDK，也不需要手动编译（推荐） |
| `.dll` | 预编译插件，适合需要引用其它第三方库的场景 |

`plugin.json` 示例：

```json
{
  "id": "my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "插件说明",
  "entry": "plugin.vb"
}
```

`id` 和 `entry` 为必填项。插件的启用状态保存在 `%AppData%\PCL\Plugins\plugins.json`，在设置页修改开关后需要重启启动器才会生效。

### 写一个脚本插件（不用编译）

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