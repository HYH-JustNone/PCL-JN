Imports System.CodeDom.Compiler
Imports System.Diagnostics
Imports System.Reflection
Imports System.Threading
Imports MoonSharp.Interpreter

Namespace Plugin

    ''' <summary>
    ''' 插件可以使用的上下文，用于读写插件私有设置、输出日志和显示提示。
    ''' </summary>
    Public Interface IPluginContext
        ''' <summary>
        ''' 插件 ID，即 plugin.json 中的 id。
        ''' </summary>
        ReadOnly Property PluginId As String
        ''' <summary>
        ''' 插件私有目录，位于 %AppData%\PCL\Plugins\&lt;id&gt;\。
        ''' </summary>
        ReadOnly Property DataPath As String
        ''' <summary>
        ''' 当前启动器版本。
        ''' </summary>
        ReadOnly Property LauncherVersion As String
        ''' <summary>
        ''' 写入启动器日志。
        ''' </summary>
        Sub Log(Message As String)
        ''' <summary>
        ''' 在启动器左下角显示提示。
        ''' </summary>
        Sub ShowHint(Message As String, Optional IsError As Boolean = False)
        ''' <summary>
        ''' 读取插件私有设置，读取失败时返回空字符串。
        ''' </summary>
        Function GetSetting(Key As String) As String
        ''' <summary>
        ''' 写入插件私有设置。
        ''' </summary>
        Sub SetSetting(Key As String, Value As String)
    End Interface

    ''' <summary>
    ''' 插件需要实现的接口。
    ''' </summary>
    Public Interface IPlugin
        ''' <summary>
        ''' 插件加载完成时调用。
        ''' </summary>
        Sub OnLoad(Context As IPluginContext)
        ''' <summary>
        ''' 插件卸载时调用。
        ''' </summary>
        Sub OnUnload()
        ''' <summary>
        ''' 即将启动游戏时调用。
        ''' </summary>
        Sub OnGameLaunching(InstanceName As String)
        ''' <summary>
        ''' 游戏进程退出后调用。
        ''' </summary>
        Sub OnGameExited(ExitCode As Integer)
    End Interface

    ''' <summary>
    ''' 可选接口：实现后可以收到游戏崩溃事件。
    ''' </summary>
    Public Interface IPluginCrashHandler
        ''' <summary>
        ''' 游戏崩溃并完成分析后调用，参数为崩溃报告所在文件夹。
        ''' </summary>
        Sub OnGameCrashed(ReportFolder As String)
    End Interface

    ''' <summary>
    ''' 可选接口：实现后可以收到页面切换事件。
    ''' </summary>
    Public Interface IPluginPageChanged
        Sub OnPageChanged(PageName As String)
    End Interface

    ''' <summary>
    ''' 可选接口：实现后可以收到启动失败事件。
    ''' </summary>
    Public Interface IPluginLaunchFailed
        Sub OnLaunchFailed(Reason As String)
    End Interface

    ''' <summary>
    ''' 可选接口：实现后可以收到版本安装完成事件。
    ''' </summary>
    Public Interface IPluginVersionInstalled
        Sub OnVersionInstalled(VersionName As String)
    End Interface

    ''' <summary>
    ''' 可选接口：实现后可以收到选中版本变化事件。
    ''' </summary>
    Public Interface IPluginInstanceSelected
        Sub OnInstanceSelected(VersionName As String)
    End Interface

    ''' <summary>
    ''' 供界面显示的插件信息。
    ''' </summary>
    Public Class PluginEntry
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property Version As String = ""
        Public Property Author As String = ""
        Public Property Description As String = ""
        Public Property Enabled As Boolean = True
        Public Property Loaded As Boolean = False
        Public Property ErrorMessage As String = ""
        ''' <summary>
        ''' 插件入口 dll 的文件名，相对于插件目录。
        ''' </summary>
        Public Property EntryFile As String = ""
        ''' <summary>
        ''' 插件声明的权限：command（执行命令）、file（读写插件目录）、network（访问网络）。默认没有任何权限。
        ''' </summary>
        Public Property Permissions As New List(Of String)
        ''' <summary>
        ''' 依赖的其它插件 id。
        ''' </summary>
        Public Property Requires As New List(Of String)
        ''' <summary>
        ''' 更新检查地址，返回形如 {"version":"1.2.0"} 的 JSON。
        ''' </summary>
        Public Property UpdateUrl As String = ""
        ''' <summary>
        ''' 支持的平台，例如 win-x64。为空表示不限制。
        ''' </summary>
        Public Property Platforms As New List(Of String)
        ''' <summary>
        ''' 插件所在文件夹。
        ''' </summary>
        Public Property Folder As String = ""
    End Class

    ''' <summary>
    ''' 插件注册到设置页的菜单项。
    ''' </summary>
    Public Class PluginMenuItem
        Public Property PluginId As String = ""
        Public Property Text As String = ""
        Public Property Callback As Action
    End Class

    ''' <summary>
    ''' 插件市场条目。
    ''' </summary>
    Public Class MarketPlugin
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property Version As String = ""
        Public Property Description As String = ""
        Public Property Author As String = ""
        Public Property DownloadUrl As String = ""
        Public Property Sha256 As String = ""
        Public Property SizeBytes As Long = 0
        Public Property Permissions As New List(Of String)
        Public Property Platforms As New List(Of String)
    End Class

    ''' <summary>
    ''' 插件管理器。负责扫描、加载、卸载插件，以及转发游戏事件。
    ''' </summary>
    Public Module PluginManager

        Private ReadOnly PluginLock As New Object
        '菜单项单独用一把锁：插件在 OnLoad 里注册菜单时，PluginLock 正被加载流程持有
        Private ReadOnly MenuLock As New Object
        Private PluginEntries As New List(Of PluginEntry)
        Private LoadedPlugins As New List(Of PluginRecord)
        Private MenuItems As New List(Of PluginMenuItem)

        Private Class PluginRecord
            Public Entry As PluginEntry
            Public Instance As IPlugin
        End Class

        ''' <summary>
        ''' 插件目录，位于 %AppData%\PCL\Plugins\。
        ''' </summary>
        Public ReadOnly Property PluginFolder As String
            Get
                Return Paths.AppDataThenName & "Plugins\"
            End Get
        End Property

        Private ReadOnly Property StateFile As String
            Get
                Return PluginFolder & "plugins.json"
            End Get
        End Property

        ''' <summary>
        ''' 重新扫描并加载全部插件。单个插件加载失败不会影响启动器。
        ''' </summary>
        Public Sub LoadAll()
            Try
                SyncLock PluginLock
                    '先卸载旧的插件实例，避免重复扫描时残留
                    UnloadAllInternal()
                    PluginEntries.Clear()
                    DirectoryUtils.Create(PluginFolder)
                    Dim State As JObject = ReadJsonObject(StateFile)
                    '第一遍：只读取元信息，这样依赖关系与加载顺序无关
                    For Each Folder As String In DirectoryUtils.EnumerateDirectories(PluginFolder)
                        ReadPluginMeta(Folder, State)
                    Next
                    '第二遍：检查依赖后加载
                    For Each Entry As PluginEntry In PluginEntries.ToList()
                        If Not Entry.Enabled Then Continue For
                        '平台校验
                        If Entry.Platforms.Count > 0 AndAlso Not Entry.Platforms.Any(Function(P) String.Equals(P, CurrentPlatform, StringComparison.OrdinalIgnoreCase)) Then
                            Entry.ErrorMessage = $"该插件不支持当前平台（{CurrentPlatform}）"
                            Logger.Warn($"插件 {Entry.Id} 未加载：{Entry.ErrorMessage}")
                            Continue For
                        End If
                        Dim Missing As New List(Of String)
                        For Each RequiredId As String In Entry.Requires
                            If Not PluginEntries.Any(Function(E) String.Equals(E.Id, RequiredId, StringComparison.OrdinalIgnoreCase)) Then Missing.Add(RequiredId)
                        Next
                        If Missing.Count > 0 Then
                            Entry.ErrorMessage = "缺少依赖插件：" & String.Join("、", Missing)
                            Logger.Warn($"插件 {Entry.Id} 未加载：{Entry.ErrorMessage}")
                            Continue For
                        End If
                        LoadPlugin(Entry, Entry.Folder)
                    Next
                    Logger.Info($"插件扫描完成，共发现 {PluginEntries.Count} 个插件，已加载 {LoadedPlugins.Count} 个")
                End SyncLock
            Catch ex As Exception
                Logger.Warn(ex, "扫描插件目录失败")
            End Try
        End Sub

        ''' <summary>
        ''' 读取插件目录中的 plugin.json，只登记元信息，不加载。
        ''' </summary>
        Private Sub ReadPluginMeta(Folder As String, State As JObject)
            Dim MetaFile As String = Path.Combine(Folder, "plugin.json")
            If Not FileUtils.Exists(MetaFile) Then Return
            Dim Entry As PluginEntry = Nothing
            Try
                Dim Meta As JObject = ReadJsonObject(MetaFile)
                Dim Id As String = GetJsonString(Meta, "id").Trim()
                Dim EntryFile As String = GetJsonString(Meta, "entry").Trim()
                If String.IsNullOrEmpty(Id) OrElse String.IsNullOrEmpty(EntryFile) Then
                    Logger.Warn($"插件无效（缺少 id 或 entry）：{MetaFile}")
                    Return
                End If
                If Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse Id.Contains("\") OrElse Id.Contains("/") Then
                    Logger.Warn($"插件无效（id 包含非法字符）：{MetaFile}")
                    Return
                End If
                Entry = New PluginEntry With {
                    .Id = Id,
                    .Name = GetJsonString(Meta, "name").Trim(),
                    .Version = GetJsonString(Meta, "version").Trim(),
                    .Author = GetJsonString(Meta, "author").Trim(),
                    .Description = GetJsonString(Meta, "description").Trim(),
                    .EntryFile = EntryFile,
                    .Folder = Folder,
                    .UpdateUrl = GetJsonString(Meta, "update").Trim()
                }
                If String.IsNullOrEmpty(Entry.Name) Then Entry.Name = Id
                Entry.Enabled = GetEnabledState(State, Id)
                '读取权限声明
                Dim PermissionToken As JToken = Meta("permissions")
                If PermissionToken IsNot Nothing AndAlso PermissionToken.Type = JTokenType.Array Then
                    For Each Item As JToken In CType(PermissionToken, JArray)
                        Entry.Permissions.Add(Item.ToString().Trim().ToLowerInvariant())
                    Next
                End If
                '读取依赖声明
                Dim RequireToken As JToken = Meta("requires")
                If RequireToken IsNot Nothing AndAlso RequireToken.Type = JTokenType.Array Then
                    For Each Item As JToken In CType(RequireToken, JArray)
                        Entry.Requires.Add(Item.ToString().Trim())
                    Next
                End If
                '读取平台声明
                CollectStrings(Meta("platform"), Entry.Platforms)
            Catch ex As Exception
                Logger.Warn(ex, $"读取插件信息失败：{MetaFile}")
                Return
            End Try
            PluginEntries.Add(Entry)
        End Sub

        ''' <summary>
        ''' 后台检查插件是否有新版本，只提示不自动更新。
        ''' </summary>
        Private Sub CheckPluginUpdate(Entry As PluginEntry)
            If String.IsNullOrEmpty(Entry.UpdateUrl) Then Return
            RunInNewThread(Sub()
                               Try
                                   If Not Entry.UpdateUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) AndAlso
                                      Not Entry.UpdateUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then Return
                                   Using Client As New Net.Http.HttpClient()
                                       Client.Timeout = TimeSpan.FromSeconds(10)
                                       Dim Text As String = Client.GetStringAsync(Entry.UpdateUrl).Result
                                       Dim Token As JToken = Text.DeserializeJson()
                                       Dim Latest As String = GetJsonString(TryCast(Token, JObject), "version").Trim()
                                       If Latest <> "" AndAlso Latest <> Entry.Version Then
                                           Logger.Info($"插件 {Entry.Id} 有新版本：{Entry.Version} → {Latest}")
                                           Hint($"插件「{Entry.Name}」有新版本 {Latest}（当前 {Entry.Version}）", HintType.Blue)
                                       End If
                                   End Using
                               Catch ex As Exception
                                   Logger.Warn(ex, $"检查插件 {Entry.Id} 更新失败")
                               End Try
                           End Sub, "Plugin Update Check")
        End Sub

        ''' <summary>
        ''' 加载单个插件，所有异常均在此处拦截。
        ''' </summary>
        Private Sub LoadPlugin(Entry As PluginEntry, Folder As String)
            Try
                Dim EntryPath As String = Path.Combine(Folder, Entry.EntryFile)
                If Not FileUtils.Exists(EntryPath) Then Throw New FileNotFoundException($"找不到插件入口文件：{EntryPath}")
                Dim Context As New PluginContext(Entry.Id, Path.Combine(PluginFolder, Entry.Id))
                Dim Instance As IPlugin = Nothing
                If Path.GetExtension(EntryPath).ToLowerInvariant() = ".lua" Then
                    'Lua 脚本插件：由内置的 MoonSharp 解释器驱动
                    Instance = New LuaPlugin(Entry.Id, EntryPath, Context, Entry.Permissions)
                Else
                    Dim PluginAssembly As Assembly = LoadPluginAssembly(EntryPath)
                    Dim AssemblyTypes As Type()
                    Try
                        AssemblyTypes = PluginAssembly.GetTypes()
                    Catch ex As ReflectionTypeLoadException
                        '部分类型加载失败时，仍然尝试加载其余类型
                        AssemblyTypes = ex.Types.Where(Function(T) T IsNot Nothing).ToArray()
                    End Try
                    For Each PluginType As Type In AssemblyTypes
                        If PluginType Is Nothing OrElse Not PluginType.IsClass OrElse PluginType.IsAbstract Then Continue For
                        '先判断是否实现了插件接口，避免去实例化 My.MyProject 之类的辅助类型
                        If Not GetType(IPlugin).IsAssignableFrom(PluginType) Then Continue For
                        Dim Created As Object
                        Try
                            Created = Activator.CreateInstance(PluginType)
                        Catch ex As Exception
                            Logger.Warn(ex, $"插件 {Entry.Id} 实例化类型 {PluginType.FullName} 失败")
                            Continue For
                        End Try
                        If TypeOf Created Is IPlugin Then
                            Instance = DirectCast(Created, IPlugin)
                            Exit For
                        End If
                    Next
                    If Instance Is Nothing Then Throw New Exception("未找到实现 PCL.Plugin.IPlugin 的公共类")
                End If
                Instance.OnLoad(Context)
                Entry.Loaded = True
                Entry.ErrorMessage = ""
                LoadedPlugins.Add(New PluginRecord With {.Entry = Entry, .Instance = Instance})
                Logger.Info($"已加载插件：{Entry.Name}（{Entry.Id}，版本 {Entry.Version}）")
                CheckPluginUpdate(Entry)
            Catch ex As Exception
                Entry.Loaded = False
                Entry.ErrorMessage = ex.Message
                Logger.Warn(ex, $"加载插件 {Entry.Id} 失败")
            End Try
        End Sub

        ''' <summary>
        ''' 加载插件入口文件：.dll 直接加载，.vb / .cs 在运行时编译后加载。
        ''' </summary>
        Private Function LoadPluginAssembly(EntryPath As String) As Assembly
            Select Case Path.GetExtension(EntryPath).ToLowerInvariant()
                Case ".dll"
                    Return Assembly.LoadFrom(EntryPath)
                Case ".vb", ".cs"
                    Return CompileScript(EntryPath)
                Case Else
                    Throw New Exception($"不支持的插件入口类型：{Path.GetExtension(EntryPath)}（只支持 .dll / .vb / .cs）")
            End Select
        End Function

        ''' <summary>
        ''' 用 .NET 自带的 CodeDom 在运行时编译脚本插件，使用者不需要安装 SDK 或手动编译。
        ''' </summary>
        Private Function CompileScript(EntryPath As String) As Assembly
            Dim Provider As CodeDomProvider
            Select Case Path.GetExtension(EntryPath).ToLowerInvariant()
                Case ".cs"
                    Provider = New Microsoft.CSharp.CSharpCodeProvider()
                Case Else
                    Provider = New Microsoft.VisualBasic.VBCodeProvider()
            End Select
            Try
                Dim Options As New CompilerParameters With {
                    .GenerateInMemory = True,
                    .GenerateExecutable = False,
                    .IncludeDebugInformation = False,
                    .TreatWarningsAsErrors = False
                }
                '插件必须能引用启动器本体，才能使用 PCL.Plugin 下的接口
                Options.ReferencedAssemblies.Add(GetType(IPlugin).Assembly.Location)
                For Each AssemblyNameText As String In {"System.dll", "System.Core.dll", "System.Xml.dll", "System.Drawing.dll", "System.Windows.Forms.dll"}
                    Options.ReferencedAssemblies.Add(AssemblyNameText)
                Next
                Dim Result As CompilerResults = Provider.CompileAssemblyFromFile(Options, EntryPath)
                If Result.Errors.HasErrors Then
                    Dim Messages As New List(Of String)
                    For Each Item As CompilerError In Result.Errors
                        If Item.IsWarning Then Continue For
                        Messages.Add($"第 {Item.Line} 行：{Item.ErrorText}")
                        If Messages.Count >= 3 Then Exit For
                    Next
                    Throw New Exception("脚本编译失败：" & Messages.Join("；") &
                                        "（提示：脚本使用 .NET Framework 自带的旧版编译器，不支持 ?.、$\"\" 等较新语法）")
                End If
                Logger.Info($"已编译脚本插件：{EntryPath}")
                Return Result.CompiledAssembly
            Finally
                Provider.Dispose()
            End Try
        End Function

        ''' <summary>
        ''' Lua 脚本插件：脚本中定义全局函数 OnLoad / OnGameLaunching / OnGameExited / OnUnload，
        ''' 并通过 pcl 表调用 Log、Hint、GetSetting 等功能。运行在硬沙箱中，无法访问文件系统与命令行。
        ''' </summary>
        Private Class LuaPlugin
            Implements IPlugin
            Implements IPluginCrashHandler
            Implements IPluginPageChanged
            Implements IPluginLaunchFailed
            Implements IPluginVersionInstalled
            Implements IPluginInstanceSelected

            Private ReadOnly Engine As Script
            Private ReadOnly PluginId As String
            Private ReadOnly Perms As List(Of String)
            Private TickTimer As Threading.Timer

            '允许插件读写的启动器设置（只放数值型与布尔型，避免误改路径等关键项）
            Private Shared ReadOnly SettingKinds As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
                {"LaunchRamCustom", "int"},
                {"UiLauncherTransparent", "int"},
                {"LaunchArgumentRam", "bool"},
                {"UiBackgroundColorful", "bool"}
            }

            Public Sub New(Id As String, EntryPath As String, Context As IPluginContext, Permissions As List(Of String))
                PluginId = Id
                Perms = Permissions
                Engine = New Script(CoreModules.Preset_HardSandbox)
                '用纯 Lua 表 + 回调暴露 API，不把 .NET 对象直接丢给脚本，避免反射与类型转换问题
                Dim Api As New Table(Engine)
                Api.Set("Version", DynValue.NewString(Context.LauncherVersion))
                Api.Set("Log", DynValue.NewCallback(Function(C, A) ApiLog(Context, A)))
                Api.Set("Hint", DynValue.NewCallback(Function(C, A) ApiHint(Context, A, False)))
                Api.Set("Warn", DynValue.NewCallback(Function(C, A) ApiHint(Context, A, True)))
                Api.Set("GetSetting", DynValue.NewCallback(Function(C, A) ApiGetSetting(Context, A)))
                Api.Set("SetSetting", DynValue.NewCallback(Function(C, A) ApiSetSetting(Context, A)))
                Api.Set("Now", DynValue.NewCallback(Function(C, A) DynValue.NewString(Date.Now.ToString("yyyy-MM-dd HH:mm:ss"))))
                Api.Set("Tick", DynValue.NewCallback(Function(C, A) DynValue.NewNumber((Date.UtcNow - New Date(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds)))
                Api.Set("GetInstance", DynValue.NewCallback(Function(C, A) ApiGetInstance(A)))
                Api.Set("GetVersions", DynValue.NewCallback(Function(C, A) ApiGetVersions()))
                Api.Set("GetMods", DynValue.NewCallback(Function(C, A) ApiGetMods()))
                Api.Set("GetJavaList", DynValue.NewCallback(Function(C, A) ApiGetJavaList()))
                Api.Set("GetMemoryInfo", DynValue.NewCallback(Function(C, A) ApiGetMemoryInfo()))
                Api.Set("GetBackups", DynValue.NewCallback(Function(C, A) ApiGetBackups()))
                Api.Set("GetCrashReports", DynValue.NewCallback(Function(C, A) ApiGetCrashReports()))
                Api.Set("GetOsInfo", DynValue.NewCallback(Function(C, A) ApiGetOsInfo()))
                Api.Set("HttpGet", DynValue.NewCallback(Function(C, A) ApiHttpGet(A)))
                Api.Set("HttpPost", DynValue.NewCallback(Function(C, A) ApiHttpPost(A)))
                Api.Set("JsonEncode", DynValue.NewCallback(Function(C, A) ApiJsonEncode(A)))
                Api.Set("JsonDecode", DynValue.NewCallback(Function(C, A) ApiJsonDecode(A)))
                Api.Set("Launch", DynValue.NewCallback(Function(C, A) ApiLaunch(A)))
                Api.Set("SelectInstance", DynValue.NewCallback(Function(C, A) ApiSelectInstance(A)))
                Api.Set("StopGame", DynValue.NewCallback(Function(C, A) ApiStopGame()))
                Api.Set("RestoreBackup", DynValue.NewCallback(Function(C, A) ApiRestoreBackup(A)))
                Api.Set("CopyToClipboard", DynValue.NewCallback(Function(C, A) ApiCopyToClipboard(A)))
                Api.Set("ShowDialog", DynValue.NewCallback(Function(C, A) ApiShowDialog(A)))
                Api.Set("SetTickInterval", DynValue.NewCallback(Function(C, A) ApiSetTickInterval(A)))
                Api.Set("Backup", DynValue.NewCallback(Function(C, A) ApiBackup()))
                Api.Set("OpenUrl", DynValue.NewCallback(Function(C, A) ApiOpenUrl(A)))
                Api.Set("OpenFolder", DynValue.NewCallback(Function(C, A) ApiOpenFolder(A)))
                Api.Set("RunCommand", DynValue.NewCallback(Function(C, A) ApiRunCommand(A)))
                Api.Set("GetSettingValue", DynValue.NewCallback(Function(C, A) ApiGetSettingValue(A)))
                Api.Set("SetSettingValue", DynValue.NewCallback(Function(C, A) ApiSetSettingValue(A)))
                Api.Set("ReadFile", DynValue.NewCallback(Function(C, A) ApiReadFile(Context, A)))
                Api.Set("WriteFile", DynValue.NewCallback(Function(C, A) ApiWriteFile(Context, A)))
                Api.Set("AddMenuItem", DynValue.NewCallback(Function(C, A) ApiAddMenuItem(A)))
                Engine.Globals.Set("pcl", DynValue.NewTable(Api))
                Engine.DoFile(EntryPath)
            End Sub

            '是否声明了某项权限
            Private Function HasPermission(Name As String) As Boolean
                Return Perms IsNot Nothing AndAlso Perms.Any(Function(P) String.Equals(P, Name, StringComparison.OrdinalIgnoreCase))
            End Function

            Private Shared Function ArgText(Args As CallbackArguments) As String
                If Args.Count <= 0 Then Return ""
                Return Args(0).ToPrintString()
            End Function
            Private Shared Function ApiLog(Context As IPluginContext, Args As CallbackArguments) As DynValue
                Context.Log(ArgText(Args))
                Return DynValue.Nil
            End Function
            Private Shared Function ApiHint(Context As IPluginContext, Args As CallbackArguments, IsError As Boolean) As DynValue
                Context.ShowHint(ArgText(Args), IsError)
                Return DynValue.Nil
            End Function
            Private Shared Function ApiGetSetting(Context As IPluginContext, Args As CallbackArguments) As DynValue
                Return DynValue.NewString(Context.GetSetting(ArgText(Args)))
            End Function
            Private Shared Function ApiSetSetting(Context As IPluginContext, Args As CallbackArguments) As DynValue
                If Args.Count >= 2 Then Context.SetSetting(Args(0).ToPrintString(), Args(1).ToPrintString())
                Return DynValue.Nil
            End Function

            '实例信息：不传参数时取当前选中实例，传版本名时取指定实例
            Private Function ApiGetInstance(Args As CallbackArguments) As DynValue
                Try
                    Dim Inst As McInstance = McInstanceSelected
                    Dim TargetName As String = If(Args.Count > 0, Args(0).ToPrintString().Trim(), "")
                    If TargetName <> "" Then Inst = FindInstanceByName(TargetName)
                    If Inst Is Nothing Then Return DynValue.NewString("")
                    Dim Loader As String = "原版"
                    Dim GameVersion As String = ""
                    Try
                        If Inst.Version.ModLoaders.HasFlagF(ModLoaders.Fabric) Then
                            Loader = "fabric"
                        ElseIf Inst.Version.ModLoaders.HasFlagF(ModLoaders.NeoForge) Then
                            Loader = "neoforge"
                        ElseIf Inst.Version.ModLoaders.HasFlagF(ModLoaders.Forge) Then
                            Loader = "forge"
                        End If
                        GameVersion = Inst.Version.VanillaName
                        If GameVersion = "Unknown" Then GameVersion = ""
                    Catch ex As Exception
                        Logger.Warn(ex, "插件读取实例信息失败")
                    End Try
                    Dim ModCount As Integer = 0
                    Try
                        Dim ModFolder As String = Inst.PathIndie & "mods\"
                        If DirectoryUtils.Exists(ModFolder) Then
                            ModCount = DirectoryUtils.EnumerateFiles(ModFolder, False).Count(Function(F) F.ToLowerInvariant().EndsWith(".jar"))
                        End If
                    Catch ex As Exception
                        Logger.Warn(ex, "插件统计 Mod 数量失败")
                    End Try
                    Dim Result As New Table(Engine)
                    Result.Set("name", DynValue.NewString(Inst.Name))
                    Result.Set("loader", DynValue.NewString(Loader))
                    Result.Set("game_version", DynValue.NewString(GameVersion))
                    Result.Set("mods", DynValue.NewNumber(ModCount))
                    Result.Set("path", DynValue.NewString(Inst.PathIndie))
                    Return DynValue.NewTable(Result)
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取实例信息失败")
                    Return DynValue.NewString("")
                End Try
            End Function

            '立即备份当前实例的配置与存档，返回备份目录
            Private Shared Function ApiBackup() As DynValue
                Try
                    Dim Inst As McInstance = McInstanceSelected
                    If Inst Is Nothing Then Return DynValue.NewString("")
                    Return DynValue.NewString(ModBackup.ForceBackup(Inst.PathIndie))
                Catch ex As Exception
                    Logger.Warn(ex, "插件请求备份失败")
                    Return DynValue.NewString("")
                End Try
            End Function

            Private Shared Function ApiOpenUrl(Args As CallbackArguments) As DynValue
                Dim Url As String = ArgText(Args)
                If Not (Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Then
                    Return DynValue.False
                End If
                RunInUi(Sub() OpenWebsite(Url))
                Return DynValue.True
            End Function

            Private Shared Function ApiOpenFolder(Args As CallbackArguments) As DynValue
                Dim Folder As String = ArgText(Args)
                If Folder = "" Then Return DynValue.False
                RunInUi(Sub() OpenExplorer(Folder))
                Return DynValue.True
            End Function

            '执行外部命令，必须先声明 command 权限并经过用户确认
            Private Function ApiRunCommand(Args As CallbackArguments) As DynValue
                If Not HasPermission("command") Then
                    Logger.Warn($"插件 {PluginId} 未声明 command 权限，已拒绝执行命令")
                    Return DynValue.False
                End If
                Dim Command As String = ArgText(Args)
                If Command = "" Then Return DynValue.False
                Dim Allowed As Boolean = False
                RunInUiWait(Sub()
                                Allowed = MyMsgBox("插件请求执行以下命令：" & vbCrLf & vbCrLf & Command & vbCrLf & vbCrLf & "确定要执行吗？",
                                                   "插件请求执行命令", "执行", "取消") = 1
                            End Sub)
                If Not Allowed Then Return DynValue.False
                Try
                    Process.Start(New ProcessStartInfo("cmd.exe", "/c " & Command) With {.CreateNoWindow = True, .UseShellExecute = False})
                    Return DynValue.True
                Catch ex As Exception
                    Logger.Warn(ex, "插件执行命令失败")
                    Return DynValue.False
                End Try
            End Function

            Private Shared Function ApiGetSettingValue(Args As CallbackArguments) As DynValue
                Dim Key As String = ArgText(Args)
                Dim Kind As String = Nothing
                If Not SettingKinds.TryGetValue(Key, Kind) Then Return DynValue.NewString("")
                Try
                    If Kind = "int" Then Return DynValue.NewString(Settings.Get(Of Integer)(Key).ToString())
                    Return DynValue.NewString(If(Settings.Get(Of Boolean)(Key), "true", "false"))
                Catch ex As Exception
                    Logger.Warn(ex, $"插件读取设置 {Key} 失败")
                    Return DynValue.NewString("")
                End Try
            End Function

            Private Shared Function ApiSetSettingValue(Args As CallbackArguments) As DynValue
                If Args.Count < 2 Then Return DynValue.False
                Dim Key As String = Args(0).ToPrintString()
                Dim Text As String = Args(1).ToPrintString()
                Dim Kind As String = Nothing
                If Not SettingKinds.TryGetValue(Key, Kind) Then Return DynValue.False
                Try
                    If Kind = "int" Then
                        Dim Number As Integer
                        If Not Integer.TryParse(Text, Number) Then Return DynValue.False
                        Settings.Set(Key, Number)
                    Else
                        Settings.Set(Key, Text.Equals("true", StringComparison.OrdinalIgnoreCase) OrElse Text = "1")
                    End If
                    Return DynValue.True
                Catch ex As Exception
                    Logger.Warn(ex, $"插件修改设置 {Key} 失败")
                    Return DynValue.False
                End Try
            End Function

            '插件只能读写自己目录下的文件，防止越权
            Private Shared Function SafePluginPath(DataPath As String, Relative As String) As String
                Try
                    Dim Root As String = Path.GetFullPath(DataPath)
                    Dim Full As String = Path.GetFullPath(Path.Combine(Root, Relative))
                    If Not Full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) Then Return ""
                    Return Full
                Catch
                    Return ""
                End Try
            End Function

            Private Function ApiReadFile(Context As IPluginContext, Args As CallbackArguments) As DynValue
                If Not HasPermission("file") Then Return DynValue.NewString("")
                Dim Full As String = SafePluginPath(Context.DataPath, ArgText(Args))
                If Full = "" OrElse Not FileUtils.Exists(Full) Then Return DynValue.NewString("")
                Try
                    Return DynValue.NewString(FileUtils.ReadAsString(Full))
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取文件失败")
                    Return DynValue.NewString("")
                End Try
            End Function

            Private Function ApiWriteFile(Context As IPluginContext, Args As CallbackArguments) As DynValue
                If Not HasPermission("file") Then Return DynValue.False
                If Args.Count < 2 Then Return DynValue.False
                Dim Full As String = SafePluginPath(Context.DataPath, Args(0).ToPrintString())
                If Full = "" Then Return DynValue.False
                Try
                    DirectoryUtils.Create(Path.GetDirectoryName(Full))
                    FileUtils.Write(Full, Args(1).ToPrintString())
                    Return DynValue.True
                Catch ex As Exception
                    Logger.Warn(ex, "插件写入文件失败")
                    Return DynValue.False
                End Try
            End Function

            '所有已安装版本
            Private Function ApiGetVersions() As DynValue
                Dim Result As New Table(Engine)
                Try
                    Dim Index As Integer = 1
                    For Each Group As KeyValuePair(Of McInstanceCardType, List(Of McInstance)) In McInstanceList.ToArray()
                        For Each Inst As McInstance In Group.Value
                            Dim Item As New Table(Engine)
                            Item.Set("name", DynValue.NewString(Inst.Name))
                            Dim Loader As String = "原版"
                            Dim GameVersion As String = ""
                            Try
                                If Inst.Version.ModLoaders.HasFlagF(ModLoaders.Fabric) Then
                                    Loader = "fabric"
                                ElseIf Inst.Version.ModLoaders.HasFlagF(ModLoaders.NeoForge) Then
                                    Loader = "neoforge"
                                ElseIf Inst.Version.ModLoaders.HasFlagF(ModLoaders.Forge) Then
                                    Loader = "forge"
                                End If
                                GameVersion = Inst.Version.VanillaName
                                If GameVersion = "Unknown" Then GameVersion = ""
                            Catch
                            End Try
                            Item.Set("loader", DynValue.NewString(Loader))
                            Item.Set("game_version", DynValue.NewString(GameVersion))
                            Result.Set(Index, DynValue.NewTable(Item))
                            Index += 1
                        Next
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取版本列表失败")
                End Try
                Return DynValue.NewTable(Result)
            End Function

            '当前实例的 Mod 列表
            Private Function ApiGetMods() As DynValue
                Dim Result As New Table(Engine)
                Try
                    Dim Inst As McInstance = McInstanceSelected
                    If Inst Is Nothing Then Return DynValue.NewTable(Result)
                    Dim Folder As String = Inst.PathIndie & "mods\"
                    If Not DirectoryUtils.Exists(Folder) Then Return DynValue.NewTable(Result)
                    Dim Index As Integer = 1
                    For Each FilePath As String In DirectoryUtils.EnumerateFiles(Folder, False)
                        If Not FilePath.ToLowerInvariant().EndsWith(".jar") Then Continue For
                        Dim Info As ModChecker.ModInfo = ModChecker.ReadInfo(FilePath)
                        Dim Item As New Table(Engine)
                        Item.Set("file", DynValue.NewString(Path.GetFileName(FilePath)))
                        If Info IsNot Nothing Then
                            Item.Set("id", DynValue.NewString(If(Info.Id, "")))
                            Item.Set("name", DynValue.NewString(If(Info.Name, "")))
                            Item.Set("version", DynValue.NewString(If(Info.Version, "")))
                            Item.Set("loader", DynValue.NewString(If(Info.Loader, "")))
                        Else
                            Item.Set("id", DynValue.NewString(""))
                            Item.Set("name", DynValue.NewString(""))
                            Item.Set("version", DynValue.NewString(""))
                            Item.Set("loader", DynValue.NewString(""))
                        End If
                        Result.Set(Index, DynValue.NewTable(Item))
                        Index += 1
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取 Mod 列表失败")
                End Try
                Return DynValue.NewTable(Result)
            End Function

            '已识别的 Java 列表
            Private Function ApiGetJavaList() As DynValue
                Dim Result As New Table(Engine)
                Try
                    Dim Index As Integer = 1
                    For Each Entry In Configs.JavaList.Get()
                        Dim Item As New Table(Engine)
                        Item.Set("path", DynValue.NewString(Entry.Folder))
                        Dim VersionText As String = ""
                        If Entry.Version IsNot Nothing Then VersionText = Entry.Version.ToString()
                        Item.Set("version", DynValue.NewString(VersionText))
                        Result.Set(Index, DynValue.NewTable(Item))
                        Index += 1
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取 Java 列表失败")
                End Try
                Return DynValue.NewTable(Result)
            End Function

            '系统内存信息（MB）
            Private Function ApiGetMemoryInfo() As DynValue
                Dim Result As New Table(Engine)
                Try
                    Result.Set("total_mb", DynValue.NewNumber(My.Computer.Info.TotalPhysicalMemory / 1024 / 1024))
                    Result.Set("available_mb", DynValue.NewNumber(My.Computer.Info.AvailablePhysicalMemory / 1024 / 1024))
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取内存信息失败")
                End Try
                Return DynValue.NewTable(Result)
            End Function

            '当前实例的备份列表
            Private Function ApiGetBackups() As DynValue
                Dim Result As New Table(Engine)
                Try
                    Dim Inst As McInstance = McInstanceSelected
                    If Inst Is Nothing Then Return DynValue.NewTable(Result)
                    Dim Root As String = ModBackup.BackupRoot(Inst.PathIndie)
                    If Not DirectoryUtils.Exists(Root) Then Return DynValue.NewTable(Result)
                    Dim Index As Integer = 1
                    For Each Folder As String In DirectoryUtils.EnumerateDirectories(Root)
                        Dim Item As New Table(Engine)
                        Item.Set("name", DynValue.NewString(Path.GetFileName(Folder)))
                        Item.Set("path", DynValue.NewString(Folder))
                        Try
                            Item.Set("time", DynValue.NewString(DirectoryUtils.GetInfo(Folder).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")))
                        Catch
                            Item.Set("time", DynValue.NewString(""))
                        End Try
                        Result.Set(Index, DynValue.NewTable(Item))
                        Index += 1
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取备份列表失败")
                End Try
                Return DynValue.NewTable(Result)
            End Function

            '最近 10 份崩溃报告
            Private Function ApiGetCrashReports() As DynValue
                Dim Result As New Table(Engine)
                Try
                    Dim Inst As McInstance = McInstanceSelected
                    If Inst Is Nothing Then Return DynValue.NewTable(Result)
                    Dim Folder As String = Inst.PathIndie & "crash-reports\"
                    If Not DirectoryUtils.Exists(Folder) Then Return DynValue.NewTable(Result)
                    Dim Files As New List(Of String)
                    For Each FilePath As String In DirectoryUtils.EnumerateFiles(Folder, False)
                        Files.Add(FilePath)
                    Next
                    Files = Files.OrderByDescending(Function(F) If(FileUtils.Exists(F), FileUtils.GetInfo(F).LastWriteTime, Date.MinValue)).Take(10).ToList()
                    Dim Index As Integer = 1
                    For Each FilePath As String In Files
                        Dim Item As New Table(Engine)
                        Item.Set("file", DynValue.NewString(Path.GetFileName(FilePath)))
                        Item.Set("path", DynValue.NewString(FilePath))
                        Try
                            Item.Set("time", DynValue.NewString(FileUtils.GetInfo(FilePath).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")))
                        Catch
                            Item.Set("time", DynValue.NewString(""))
                        End Try
                        Result.Set(Index, DynValue.NewTable(Item))
                        Index += 1
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取崩溃报告失败")
                End Try
                Return DynValue.NewTable(Result)
            End Function

            '系统信息
            Private Function ApiGetOsInfo() As DynValue
                Dim Result As New Table(Engine)
                Try
                    Result.Set("os", DynValue.NewString(Environment.OSVersion.VersionString))
                    Result.Set("cpu_count", DynValue.NewNumber(Environment.ProcessorCount))
                    Result.Set("total_mb", DynValue.NewNumber(My.Computer.Info.TotalPhysicalMemory / 1024 / 1024))
                    Result.Set("is_64bit", DynValue.NewBoolean(Environment.Is64BitProcess))
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取系统信息失败")
                End Try
                Return DynValue.NewTable(Result)
            End Function

            Private Shared Function IsHttpUrl(Url As String) As Boolean
                Return Not String.IsNullOrWhiteSpace(Url) AndAlso
                       (Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse
                        Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            End Function

            '发起 GET 请求（需要 network 权限）
            Private Function ApiHttpGet(Args As CallbackArguments) As DynValue
                If Not HasPermission("network") Then
                    Logger.Warn($"插件 {PluginId} 未声明 network 权限，已拒绝网络请求")
                    Return DynValue.NewString("")
                End If
                Dim Url As String = ArgText(Args)
                If Not IsHttpUrl(Url) Then Return DynValue.NewString("")
                Try
                    Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
                    Using Client As New Net.Http.HttpClient()
                        Client.Timeout = TimeSpan.FromSeconds(15)
                        Client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-JN-Plugin/1.0")
                        Return DynValue.NewString(Client.GetStringAsync(Url).Result)
                    End Using
                Catch ex As Exception
                    Logger.Warn(ex, $"插件请求 {Url} 失败：{ex.GetBaseException().Message}")
                    Return DynValue.NewString("")
                End Try
            End Function

            '发起 POST 请求（需要 network 权限）
            Private Function ApiHttpPost(Args As CallbackArguments) As DynValue
                If Not HasPermission("network") Then Return DynValue.NewString("")
                If Args.Count < 2 Then Return DynValue.NewString("")
                Dim Url As String = Args(0).ToPrintString()
                Dim Body As String = Args(1).ToPrintString()
                If Not IsHttpUrl(Url) Then Return DynValue.NewString("")
                Try
                    Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
                    Using Client As New Net.Http.HttpClient()
                        Client.Timeout = TimeSpan.FromSeconds(15)
                        Client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-JN-Plugin/1.0")
                        Dim Content As New Net.Http.StringContent(Body, Text.Encoding.UTF8, "application/json")
                        Return DynValue.NewString(Client.PostAsync(Url, Content).Result.Content.ReadAsStringAsync().Result)
                    End Using
                Catch ex As Exception
                    Logger.Warn(ex, $"插件请求 {Url} 失败：{ex.GetBaseException().Message}")
                    Return DynValue.NewString("")
                End Try
            End Function

            'JSON 文本 -> Lua 表
            Private Function ApiJsonDecode(Args As CallbackArguments) As DynValue
                Try
                    Return JsonTokenToLua(ArgText(Args).DeserializeJson())
                Catch ex As Exception
                    Logger.Warn(ex, "插件解析 JSON 失败")
                    Return DynValue.Nil
                End Try
            End Function

            'Lua 表 -> JSON 文本
            Private Shared Function ApiJsonEncode(Args As CallbackArguments) As DynValue
                If Args.Count <= 0 Then Return DynValue.NewString("")
                Try
                    Return DynValue.NewString(LuaToJson(Args(0)).ToString(Newtonsoft.Json.Formatting.None))
                Catch ex As Exception
                    Return DynValue.NewString("")
                End Try
            End Function

            Private Function JsonTokenToLua(Token As JToken) As DynValue
                If Token Is Nothing Then Return DynValue.Nil
                Select Case Token.Type
                    Case JTokenType.Object
                        Dim Result As New Table(Engine)
                        For Each PropertyItem As JProperty In CType(Token, JObject).Properties
                            Result.Set(PropertyItem.Name, JsonTokenToLua(PropertyItem.Value))
                        Next
                        Return DynValue.NewTable(Result)
                    Case JTokenType.Array
                        Dim Result As New Table(Engine)
                        Dim Index As Integer = 1
                        For Each Item As JToken In CType(Token, JArray)
                            Result.Set(Index, JsonTokenToLua(Item))
                            Index += 1
                        Next
                        Return DynValue.NewTable(Result)
                    Case JTokenType.Integer, JTokenType.Float
                        Return DynValue.NewNumber(Token.ToObject(Of Double)())
                    Case JTokenType.Boolean
                        Return DynValue.NewBoolean(Token.ToObject(Of Boolean)())
                    Case JTokenType.Null, JTokenType.Undefined
                        Return DynValue.Nil
                    Case Else
                        Return DynValue.NewString(Token.ToString())
                End Select
            End Function

            Private Shared Function LuaToJson(Value As DynValue) As JToken
                If Value Is Nothing Then Return JValue.CreateNull()
                Select Case Value.Type
                    Case DataType.Nil, DataType.Void
                        Return JValue.CreateNull()
                    Case DataType.Boolean
                        Return New JValue(Value.Boolean)
                    Case DataType.Number
                        '整数就不要输出成 3.0 了
                        If Value.Number = Math.Floor(Value.Number) AndAlso Math.Abs(Value.Number) < 1.0E+15 Then
                            Return New JValue(CLng(Value.Number))
                        End If
                        Return New JValue(Value.Number)
                    Case DataType.String
                        Return New JValue(Value.String)
                    Case DataType.Table
                        Dim Source As Table = Value.Table
                        If Source.Length > 0 Then
                            Dim Array As New JArray
                            For Index As Integer = 1 To Source.Length
                                Array.Add(LuaToJson(Source.Get(Index)))
                            Next
                            Return Array
                        End If
                        Dim Obj As New JObject
                        For Each Pair As TablePair In Source.Pairs
                            Obj(Pair.Key.ToPrintString()) = LuaToJson(Pair.Value)
                        Next
                        Return Obj
                    Case Else
                        Return New JValue(Value.ToPrintString())
                End Select
            End Function

            '启动指定版本（需用户确认）
            Private Shared Function ApiLaunch(Args As CallbackArguments) As DynValue
                Dim Name As String = ArgText(Args)
                If Name = "" Then Return DynValue.False
                Dim Inst As McInstance = FindInstanceByName(Name)
                If Inst Is Nothing Then Return DynValue.False
                Dim Allowed As Boolean = False
                RunInUiWait(Sub() Allowed = MyMsgBox("插件请求启动版本：" & vbCrLf & vbCrLf & Inst.Name, "插件请求启动游戏", "启动", "取消") = 1)
                If Not Allowed Then Return DynValue.False
                Dim Started As Boolean = False
                RunInUiWait(Sub() Started = McLaunchStart(New McLaunchOptions With {.Instance = Inst}))
                Return DynValue.NewBoolean(Started)
            End Function

            '切换当前选中的版本
            Private Shared Function ApiSelectInstance(Args As CallbackArguments) As DynValue
                Dim Name As String = ArgText(Args)
                If Name = "" Then Return DynValue.False
                Dim Inst As McInstance = FindInstanceByName(Name)
                If Inst Is Nothing Then Return DynValue.False
                RunInUiWait(Sub()
                                McInstanceSelected = Inst
                                If FrmLaunchLeft IsNot Nothing Then
                                    FrmLaunchLeft.RefreshButtonsUI()
                                    FrmLaunchLeft.RefreshPage(False, False)
                                End If
                            End Sub)
                Return DynValue.True
            End Function

            '停止启动或关闭游戏
            Private Shared Function ApiStopGame() As DynValue
                RunInUi(Sub()
                            Try
                                If McLaunchLoader IsNot Nothing AndAlso McLaunchLoader.State = LoadState.Loading Then McLaunchLoader.State = LoadState.Canceled
                            Catch
                            End Try
                            For Each Item In McWatcherList.ToList()
                                Try
                                    Item.Kill()
                                Catch
                                End Try
                            Next
                        End Sub)
                Return DynValue.True
            End Function

            '回滚到指定备份（需用户确认）
            Private Shared Function ApiRestoreBackup(Args As CallbackArguments) As DynValue
                Dim Folder As String = ArgText(Args)
                Dim Inst As McInstance = McInstanceSelected
                If Inst Is Nothing OrElse Folder = "" Then Return DynValue.NewString("")
                Dim Allowed As Boolean = False
                RunInUiWait(Sub() Allowed = MyMsgBox("插件请求回滚备份：" & vbCrLf & vbCrLf & Folder & vbCrLf & vbCrLf & "当前文件会被替换，确定继续吗？", "插件请求回滚备份", "回滚", "取消") = 1)
                If Not Allowed Then Return DynValue.NewString("")
                Try
                    Return DynValue.NewString(ModBackup.Restore(Inst.PathIndie, Folder, True))
                Catch ex As Exception
                    Logger.Warn(ex, "插件回滚备份失败")
                    Return DynValue.NewString("")
                End Try
            End Function

            Private Shared Function ApiCopyToClipboard(Args As CallbackArguments) As DynValue
                Dim Message As String = ArgText(Args)
                If Message = "" Then Return DynValue.False
                Try
                    RunInUiWait(Sub() System.Windows.Clipboard.SetText(Message))
                    Return DynValue.True
                Catch ex As Exception
                    Logger.Warn(ex, "插件写入剪贴板失败")
                    Return DynValue.False
                End Try
            End Function

            Private Shared Function ApiShowDialog(Args As CallbackArguments) As DynValue
                Dim Title As String = If(Args.Count > 0, Args(0).ToPrintString(), "插件提示")
                Dim Message As String = If(Args.Count > 1, Args(1).ToPrintString(), "")
                RunInUiWait(Sub() MyMsgBox(Message, Title, "确定"))
                Return DynValue.Nil
            End Function

            '设置 OnTick 的触发间隔，传 0 关闭
            Private Function ApiSetTickInterval(Args As CallbackArguments) As DynValue
                Dim Seconds As Double = 0
                If Args.Count > 0 Then Seconds = Args(0).Number
                Try
                    If TickTimer IsNot Nothing Then
                        TickTimer.Dispose()
                        TickTimer = Nothing
                    End If
                    If Seconds <= 0 Then Return DynValue.True
                    Dim Interval As Integer = CInt(Math.Max(1, Seconds) * 1000)
                    TickTimer = New Threading.Timer(Sub(State)
                                                        RunWithTimeout("OnTick", Sub() CallScript("OnTick"), 10000)
                                                    End Sub, Nothing, Interval, Interval)
                    Return DynValue.True
                Catch ex As Exception
                    Logger.Warn(ex, "插件设置定时器失败")
                    Return DynValue.False
                End Try
            End Function

            Private Shared Function FindInstanceByName(Name As String) As McInstance
                Try
                    For Each Group As KeyValuePair(Of McInstanceCardType, List(Of McInstance)) In McInstanceList.ToArray()
                        For Each Inst As McInstance In Group.Value
                            If String.Equals(Inst.Name, Name, StringComparison.OrdinalIgnoreCase) Then Return Inst
                        Next
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, "插件查找版本失败")
                End Try
                Return Nothing
            End Function

            '在设置页插件卡片里注册一个按钮
            Private Function ApiAddMenuItem(Args As CallbackArguments) As DynValue
                If Args.Count < 2 Then Return DynValue.False
                Dim Text As String = Args(0).ToPrintString()
                Dim Target As DynValue = Args(1)
                If Target.Type <> DataType.Function Then Return DynValue.False
                RegisterMenuItem(PluginId, Text, Sub()
                                                      RunWithTimeout("菜单项 " & Text,
                                                                     Sub() Engine.Call(Target, New DynValue() {}), 15000)
                                                  End Sub)
                Return DynValue.True
            End Function

            ''' <summary>
            ''' 在后台线程执行脚本，超时则强制中断，避免插件死循环卡死启动器。
            ''' </summary>
            Private Function RunWithTimeout(Label As String, Action As Action, Optional TimeoutMs As Integer = 10000) As Boolean
                Dim Worker As New Thread(Sub()
                                             Try
                                                 Action()
                                             Catch ex As Exception
                                                 Logger.Warn(ex, $"插件 {PluginId} 执行 {Label} 失败")
                                             End Try
                                         End Sub) With {.IsBackground = True}
                Worker.Start()
                If Worker.Join(TimeoutMs) Then Return True
                Logger.Warn($"插件 {PluginId} 的 {Label} 超过 {TimeoutMs \ 1000} 秒未结束，已强制中断")
                Try
                    Worker.Abort()
                Catch
                End Try
                Return False
            End Function

            '调用脚本里的全局函数；脚本没定义该函数时直接跳过
            Private Sub CallScript(Name As String, ParamArray Args As Object())
                Dim Target As DynValue = Engine.Globals.Get(Name)
                If Target Is Nothing OrElse Target.Type <> DataType.Function Then Return
                Dim Values As DynValue() = Args.Select(Function(A) DynValue.FromObject(Engine, A)).ToArray()
                Engine.Call(Target, Values)
            End Sub

            Public Sub OnLoad(Context As IPluginContext) Implements IPlugin.OnLoad
                RunWithTimeout("OnLoad", Sub() CallScript("OnLoad"), 5000)
            End Sub

            Public Sub OnUnload() Implements IPlugin.OnUnload
                Try
                    If TickTimer IsNot Nothing Then
                        TickTimer.Dispose()
                        TickTimer = Nothing
                    End If
                Catch
                End Try
                RunWithTimeout("OnUnload", Sub() CallScript("OnUnload"), 3000)
            End Sub

            Public Sub OnGameLaunching(InstanceName As String) Implements IPlugin.OnGameLaunching
                RunWithTimeout("OnGameLaunching", Sub() CallScript("OnGameLaunching", InstanceName), 15000)
            End Sub

            Public Sub OnGameExited(ExitCode As Integer) Implements IPlugin.OnGameExited
                RunWithTimeout("OnGameExited", Sub() CallScript("OnGameExited", ExitCode), 15000)
            End Sub

            Public Sub OnGameCrashed(ReportFolder As String) Implements IPluginCrashHandler.OnGameCrashed
                RunWithTimeout("OnGameCrashed", Sub() CallScript("OnGameCrashed", ReportFolder), 15000)
            End Sub

            Public Sub OnPageChanged(PageName As String) Implements IPluginPageChanged.OnPageChanged
                RunWithTimeout("OnPageChanged", Sub() CallScript("OnPageChanged", PageName), 10000)
            End Sub

            Public Sub OnLaunchFailed(Reason As String) Implements IPluginLaunchFailed.OnLaunchFailed
                RunWithTimeout("OnLaunchFailed", Sub() CallScript("OnLaunchFailed", Reason), 10000)
            End Sub

            Public Sub OnVersionInstalled(VersionName As String) Implements IPluginVersionInstalled.OnVersionInstalled
                RunWithTimeout("OnVersionInstalled", Sub() CallScript("OnVersionInstalled", VersionName), 15000)
            End Sub

            Public Sub OnInstanceSelected(VersionName As String) Implements IPluginInstanceSelected.OnInstanceSelected
                RunWithTimeout("OnInstanceSelected", Sub() CallScript("OnInstanceSelected", VersionName), 10000)
            End Sub
        End Class

        ''' <summary>
        ''' 设置插件是否启用。仅在状态文件中记录，需要重启后才会生效。
        ''' </summary>
        Public Sub SetPluginEnabled(Id As String, Enabled As Boolean)
            Try
                SyncLock PluginLock
                    Dim Entry As PluginEntry = PluginEntries.FirstOrDefault(Function(E) String.Equals(E.Id, Id, StringComparison.OrdinalIgnoreCase))
                    If Entry IsNot Nothing Then Entry.Enabled = Enabled
                    Dim State As JObject = ReadJsonObject(StateFile)
                    State(Id) = New JValue(Enabled)
                    DirectoryUtils.Create(PluginFolder)
                    FileUtils.Write(StateFile, State.ToString(Newtonsoft.Json.Formatting.None))
                End SyncLock
                Logger.Info($"已设置插件 {Id} 的启用状态：{Enabled}，重启后生效")
            Catch ex As Exception
                Logger.Warn(ex, $"保存插件 {Id} 的启用状态失败")
            End Try
        End Sub

        ''' <summary>
        ''' 获取全部已发现的插件，包括未启用的插件。
        ''' </summary>
        Public Function GetPlugins() As List(Of PluginEntry)
            SyncLock PluginLock
                Return PluginEntries.Select(
                    Function(E) New PluginEntry With {
                        .Id = E.Id,
                        .Name = E.Name,
                        .Version = E.Version,
                        .Author = E.Author,
                        .Description = E.Description,
                        .Enabled = E.Enabled,
                        .Loaded = E.Loaded,
                        .ErrorMessage = E.ErrorMessage,
                        .EntryFile = E.EntryFile,
                        .Permissions = New List(Of String)(E.Permissions),
                        .Requires = New List(Of String)(E.Requires),
                        .UpdateUrl = E.UpdateUrl,
                        .Folder = E.Folder,
                        .Platforms = New List(Of String)(E.Platforms)
                    }).ToList()
            End SyncLock
        End Function

        ''' <summary>
        ''' 由插件注册一个显示在设置页的菜单项。
        ''' </summary>
        Public Sub RegisterMenuItem(PluginId As String, Text As String, Callback As Action)
            If String.IsNullOrWhiteSpace(Text) OrElse Callback Is Nothing Then Return
            SyncLock MenuLock
                MenuItems.Add(New PluginMenuItem With {.PluginId = PluginId, .Text = Text.Trim(), .Callback = Callback})
            End SyncLock
        End Sub

        ''' <summary>
        ''' 获取某个插件注册的菜单项。
        ''' </summary>
        Public Function GetMenuItems(PluginId As String) As List(Of PluginMenuItem)
            SyncLock MenuLock
                Return MenuItems.Where(Function(M) String.Equals(M.PluginId, PluginId, StringComparison.OrdinalIgnoreCase)).ToList()
            End SyncLock
        End Function

        ''' <summary>
        ''' 当前平台标识，用于插件包兼容性判断。
        ''' </summary>
        Public ReadOnly Property CurrentPlatform As String
            Get
                Try
                    Dim Arch As String = Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
                    If Arch = "arm64" Then Return "win-arm64"
                    If Arch = "x86" Then Return "win-x86"
                    Return "win-x64"
                Catch
                    Return If(Environment.Is64BitProcess, "win-x64", "win-x86")
                End Try
            End Get
        End Property

        Private Sub CollectStrings(Token As JToken, Target As List(Of String))
            If Token Is Nothing OrElse Token.Type <> JTokenType.Array Then Return
            For Each Item As JToken In CType(Token, JArray)
                Target.Add(Item.ToString().Trim())
            Next
        End Sub

        ''' <summary>
        ''' 插件市场索引地址。
        ''' </summary>
        Public Const MarketUrl As String = "https://raw.githubusercontent.com/HYH-JustNone/PCL-JN/dev/plugins/index.json"

        Private ReadOnly Property MarketCacheFile As String
            Get
                Return PluginFolder & ".market-cache.json"
            End Get
        End Property

        ''' <summary>
        ''' 获取插件市场列表。默认优先使用 6 小时内的缓存，ForceRefresh 为 True 时强制联网。
        ''' </summary>
        Public Function GetMarketPlugins(Optional ForceRefresh As Boolean = False) As List(Of MarketPlugin)
            Dim Result As New List(Of MarketPlugin)
            Try
                Dim Text As String = ""
                If Not ForceRefresh AndAlso FileUtils.Exists(MarketCacheFile) Then
                    Try
                        If Date.Now - FileUtils.GetInfo(MarketCacheFile).LastWriteTime < TimeSpan.FromHours(6) Then
                            Text = FileUtils.ReadAsString(MarketCacheFile)
                        End If
                    Catch
                    End Try
                End If
                If String.IsNullOrWhiteSpace(Text) Then
                    Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
                    Using Client As New Net.Http.HttpClient()
                        Client.Timeout = TimeSpan.FromSeconds(15)
                        Client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-JN/1.0")
                        Text = Client.GetStringAsync(MarketUrl).Result
                    End Using
                    Try
                        DirectoryUtils.Create(PluginFolder)
                        FileUtils.Write(MarketCacheFile, Text)
                    Catch
                    End Try
                End If
                Dim Root As JObject = TryCast(Text.DeserializeJson(), JObject)
                Dim Items As JArray = Nothing
                If Root IsNot Nothing Then Items = TryCast(Root("plugins"), JArray)
                If Items Is Nothing Then Return Result
                For Each Token As JToken In Items
                    Dim Item As JObject = TryCast(Token, JObject)
                    If Item Is Nothing Then Continue For
                    Dim Entry As New MarketPlugin With {
                        .Id = GetJsonString(Item, "id").Trim(),
                        .Name = GetJsonString(Item, "name").Trim(),
                        .Version = GetJsonString(Item, "version").Trim(),
                        .Description = GetJsonString(Item, "description").Trim(),
                        .Author = GetJsonString(Item, "author").Trim(),
                        .DownloadUrl = GetJsonString(Item, "download").Trim(),
                        .Sha256 = GetJsonString(Item, "sha256").Trim()
                    }
                    If Entry.Id = "" OrElse Entry.DownloadUrl = "" Then Continue For
                    If String.IsNullOrEmpty(Entry.Name) Then Entry.Name = Entry.Id
                    CollectStrings(Item("permissions"), Entry.Permissions)
                    CollectStrings(Item("platform"), Entry.Platforms)
                    Try
                        Dim SizeToken As JToken = Item("size")
                        If SizeToken IsNot Nothing Then Entry.SizeBytes = SizeToken.ToObject(Of Long)()
                    Catch
                    End Try
                    Result.Add(Entry)
                Next
            Catch ex As Exception
                Logger.Warn(ex, "获取插件市场列表失败")
            End Try
            Return Result
        End Function

        ''' <summary>
        ''' 从市场条目下载并安装插件，返回插件 id。失败抛出带说明的异常。
        ''' </summary>
        Public Function InstallFromMarket(Item As MarketPlugin) As String
            If Item Is Nothing OrElse Item.DownloadUrl = "" Then Throw New Exception("插件地址为空")
            Dim TempFile As String = Path.Combine(Path.GetTempPath(), "PCL-JN-" & Guid.NewGuid().ToString("N") & ".pjnp")
            Try
                Net.ServicePointManager.SecurityProtocol = Net.SecurityProtocolType.Tls12
                Using Client As New Net.Http.HttpClient()
                    Client.Timeout = TimeSpan.FromSeconds(120)
                    Client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-JN/1.0")
                    System.IO.File.WriteAllBytes(TempFile, Client.GetByteArrayAsync(Item.DownloadUrl).Result)
                End Using
                If Item.Sha256 <> "" Then
                    Dim Actual As String = CryptographyUtils.ComputeFileHash(TempFile, CryptographyUtils.HashMethod.Sha256)
                    If Not String.Equals(Actual, Item.Sha256, StringComparison.OrdinalIgnoreCase) Then
                        Throw New Exception("SHA256 校验失败，文件可能被篡改")
                    End If
                End If
                Return InstallPluginPackage(TempFile)
            Finally
                Try
                    If System.IO.File.Exists(TempFile) Then System.IO.File.Delete(TempFile)
                Catch
                End Try
            End Try
        End Function

        ''' <summary>
        ''' 安装 .pjnp 插件包（zip 结构），返回插件 id。失败抛出带说明的异常。
        ''' </summary>
        Public Function InstallPluginPackage(PackagePath As String) As String
            If Not FileUtils.Exists(PackagePath) Then Throw New Exception("找不到插件包：" & PackagePath)
            Dim Extension As String = Path.GetExtension(PackagePath).ToLowerInvariant()
            If Extension <> ".pjnp" AndAlso Extension <> ".zip" Then Throw New Exception("不支持的插件包格式：" & Extension & "（应为 .pjnp）")
            DirectoryUtils.Create(PluginFolder)
            Dim TempFolder As String = Path.Combine(Path.GetTempPath(), "PCL-JN-Plugin-" & Guid.NewGuid().ToString("N"))
            Try
                DirectoryUtils.Create(TempFolder)
                FileUtils.ExtractToDirectory(PackagePath, TempFolder)
                Dim MetaFile As String = Path.Combine(TempFolder, "plugin.json")
                If Not FileUtils.Exists(MetaFile) Then
                    '兼容压缩包里多套一层文件夹的情况
                    Dim SubFolders As List(Of String) = DirectoryUtils.EnumerateDirectories(TempFolder).ToList()
                    If SubFolders.Count = 1 AndAlso FileUtils.Exists(Path.Combine(SubFolders(0), "plugin.json")) Then
                        TempFolder = SubFolders(0)
                        MetaFile = Path.Combine(TempFolder, "plugin.json")
                    Else
                        Throw New Exception("插件包内缺少 plugin.json")
                    End If
                End If
                Dim Meta As JObject = ReadJsonObject(MetaFile)
                Dim Id As String = GetJsonString(Meta, "id").Trim()
                If Id = "" Then Throw New Exception("plugin.json 缺少 id")
                If Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then Throw New Exception("插件 id 含有非法字符")
                Dim Platforms As New List(Of String)
                CollectStrings(Meta("platform"), Platforms)
                If Platforms.Count > 0 AndAlso Not Platforms.Any(Function(P) String.Equals(P, CurrentPlatform, StringComparison.OrdinalIgnoreCase)) Then
                    Throw New Exception($"该插件不支持当前平台（{CurrentPlatform}）")
                End If
                Dim Target As String = Path.Combine(PluginFolder, Id)
                If DirectoryUtils.Exists(Target) Then Throw New Exception("已存在同名插件，请先删除旧版本")
                System.IO.Directory.Move(TempFolder, Target)
                TempFolder = "" '已移动，无需清理
                Logger.Info($"已安装插件包：{Id}")
                Return Id
            Finally
                If TempFolder <> "" AndAlso DirectoryUtils.Exists(TempFolder) Then
                    Try
                        System.IO.Directory.Delete(TempFolder, True)
                    Catch
                    End Try
                End If
            End Try
        End Function

        ''' <summary>
        ''' 删除插件目录（不可恢复），同时卸载其运行时实例。
        ''' </summary>
        Public Sub RemovePlugin(Id As String)
            SyncLock PluginLock
                Dim Entry As PluginEntry = PluginEntries.FirstOrDefault(Function(E) String.Equals(E.Id, Id, StringComparison.OrdinalIgnoreCase))
                If Entry Is Nothing Then Return
                Dim Record As PluginRecord = LoadedPlugins.FirstOrDefault(Function(R) String.Equals(R.Entry.Id, Id, StringComparison.OrdinalIgnoreCase))
                If Record IsNot Nothing Then
                    Try
                        Record.Instance.OnUnload()
                    Catch
                    End Try
                    LoadedPlugins.Remove(Record)
                End If
                PluginEntries.Remove(Entry)
                SyncLock MenuLock
                    MenuItems.RemoveAll(Function(M) String.Equals(M.PluginId, Id, StringComparison.OrdinalIgnoreCase))
                End SyncLock
                If DirectoryUtils.Exists(Entry.Folder) Then System.IO.Directory.Delete(Entry.Folder, True)
            End SyncLock
            Logger.Info($"已删除插件：{Id}")
        End Sub

        ''' <summary>
        ''' 转发游戏启动事件。
        ''' </summary>
        Public Sub RaiseGameLaunching(InstanceName As String)
            Dim Snapshot As PluginRecord()
            SyncLock PluginLock
                Snapshot = LoadedPlugins.Where(Function(R) R.Entry.Enabled).ToArray()
            End SyncLock
            For Each Record As PluginRecord In Snapshot
                Try
                    Record.Instance.OnGameLaunching(InstanceName)
                Catch ex As Exception
                    Logger.Warn(ex, $"插件 {Record.Entry.Id} 处理游戏启动事件失败")
                End Try
            Next
        End Sub

        ''' <summary>
        ''' 转发游戏退出事件。
        ''' </summary>
        Public Sub RaiseGameExited(ExitCode As Integer)
            Dim Snapshot As PluginRecord()
            SyncLock PluginLock
                Snapshot = LoadedPlugins.Where(Function(R) R.Entry.Enabled).ToArray()
            End SyncLock
            For Each Record As PluginRecord In Snapshot
                Try
                    Record.Instance.OnGameExited(ExitCode)
                Catch ex As Exception
                    Logger.Warn(ex, $"插件 {Record.Entry.Id} 处理游戏退出事件失败")
                End Try
            Next
        End Sub

        ''' <summary>
        ''' 转发游戏崩溃事件（仅实现 IPluginCrashHandler 的插件会收到）。
        ''' </summary>
        Public Sub RaiseGameCrashed(ReportFolder As String)
            Dim Snapshot As PluginRecord()
            SyncLock PluginLock
                Snapshot = LoadedPlugins.Where(Function(R) R.Entry.Enabled).ToArray()
            End SyncLock
            For Each Record As PluginRecord In Snapshot
                Dim Handler As IPluginCrashHandler = TryCast(Record.Instance, IPluginCrashHandler)
                If Handler Is Nothing Then Continue For
                Try
                    Handler.OnGameCrashed(ReportFolder)
                Catch ex As Exception
                    Logger.Warn(ex, $"插件 {Record.Entry.Id} 处理崩溃事件失败")
                End Try
            Next
        End Sub

        ''' <summary>
        ''' 通用的事件转发：只调用实现了对应接口的插件。
        ''' </summary>
        Private Sub RaiseToHandlers(Of THandler As Class)(EventName As String, Invoke As Action(Of THandler))
            Dim Snapshot As PluginRecord()
            SyncLock PluginLock
                Snapshot = LoadedPlugins.Where(Function(R) R.Entry.Enabled).ToArray()
            End SyncLock
            For Each Record As PluginRecord In Snapshot
                Dim Handler As THandler = TryCast(Record.Instance, THandler)
                If Handler Is Nothing Then Continue For
                Try
                    Invoke(Handler)
                Catch ex As Exception
                    Logger.Warn(ex, $"插件 {Record.Entry.Id} 处理 {EventName} 事件失败")
                End Try
            Next
        End Sub

        ''' <summary>转发页面切换事件。</summary>
        Public Sub RaisePageChanged(PageName As String)
            RaiseToHandlers(Of IPluginPageChanged)("页面切换", Sub(H) H.OnPageChanged(PageName))
        End Sub

        ''' <summary>转发启动失败事件。</summary>
        Public Sub RaiseLaunchFailed(Reason As String)
            RaiseToHandlers(Of IPluginLaunchFailed)("启动失败", Sub(H) H.OnLaunchFailed(Reason))
        End Sub

        ''' <summary>转发版本安装完成事件。</summary>
        Public Sub RaiseVersionInstalled(VersionName As String)
            RaiseToHandlers(Of IPluginVersionInstalled)("安装完成", Sub(H) H.OnVersionInstalled(VersionName))
        End Sub

        ''' <summary>转发选中版本变化事件。</summary>
        Public Sub RaiseInstanceSelected(VersionName As String)
            RaiseToHandlers(Of IPluginInstanceSelected)("版本切换", Sub(H) H.OnInstanceSelected(VersionName))
        End Sub

        ''' <summary>
        ''' 卸载全部插件。
        ''' </summary>
        Public Sub UnloadAll()
            SyncLock PluginLock
                UnloadAllInternal()
            End SyncLock
        End Sub

        Private Sub UnloadAllInternal()
            For Each Record As PluginRecord In LoadedPlugins.ToList()
                Try
                    Record.Instance.OnUnload()
                Catch ex As Exception
                    Logger.Warn(ex, $"卸载插件 {Record.Entry.Id} 失败")
                End Try
                Record.Entry.Loaded = False
            Next
            LoadedPlugins.Clear()
            SyncLock MenuLock
                MenuItems.Clear()
            End SyncLock
        End Sub

        ''' <summary>
        ''' 读取 JSON 对象。文件不存在、内容为空或格式错误时返回空对象。
        ''' </summary>
        Private Function ReadJsonObject(FilePath As String) As JObject
            Try
                If Not FileUtils.Exists(FilePath) Then Return New JObject()
                Dim Text As String = FileUtils.TryReadAsString(FilePath)
                If String.IsNullOrWhiteSpace(Text) Then Return New JObject()
                Dim Token As JToken = Text.DeserializeJson()
                If TypeOf Token Is JObject Then Return DirectCast(Token, JObject)
            Catch ex As Exception
                Logger.Warn(ex, $"读取 JSON 文件失败：{FilePath}")
            End Try
            Return New JObject()
        End Function

        Private Function GetJsonString(Json As JObject, Key As String) As String
            If Json Is Nothing Then Return ""
            Dim Token As JToken = Json(Key)
            If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return ""
            Return Token.ToString()
        End Function

        Private Function GetEnabledState(State As JObject, Id As String) As Boolean
            If State Is Nothing Then Return True
            Dim Token As JToken = State(Id)
            If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return True
            Try
                Return Token.Value(Of Boolean)()
            Catch
                Return True
            End Try
        End Function

        ''' <summary>
        ''' 插件上下文实现，负责插件私有设置与日志、提示。
        ''' </summary>
        Private Class PluginContext
            Implements IPluginContext

            Private ReadOnly _PluginId As String
            Private ReadOnly _DataPath As String
            Private ReadOnly _LauncherVersion As String
            Public ReadOnly Property PluginId As String Implements IPluginContext.PluginId
                Get
                    Return _PluginId
                End Get
            End Property
            Public ReadOnly Property DataPath As String Implements IPluginContext.DataPath
                Get
                    Return _DataPath
                End Get
            End Property
            Public ReadOnly Property LauncherVersion As String Implements IPluginContext.LauncherVersion
                Get
                    Return _LauncherVersion
                End Get
            End Property
            Private ReadOnly SettingsFile As String
            Private ReadOnly SettingsJson As JObject
            Private ReadOnly SettingsLock As New Object

            Public Sub New(Id As String, DataFolder As String)
                _PluginId = Id
                _DataPath = DataFolder & "\"
                _LauncherVersion = VersionDisplay
                DirectoryUtils.Create(_DataPath)
                SettingsFile = _DataPath & "settings.json"
                SettingsJson = ReadJsonObject(SettingsFile)
            End Sub

            Public Sub Log(Message As String) Implements IPluginContext.Log
                Logger.Info($"[插件 {PluginId}] {Message}")
                '额外写入插件自己的日志文件，方便排查
                Try
                    DirectoryUtils.Create(_DataPath)
                    Dim LogFile As String = _DataPath & "log.txt"
                    Dim FileInfoObj As New System.IO.FileInfo(LogFile)
                    If FileInfoObj.Exists AndAlso FileInfoObj.Length > 262144 Then System.IO.File.Delete(LogFile)
                    System.IO.File.AppendAllText(LogFile, $"[{Date.Now:yyyy-MM-dd HH:mm:ss}] {Message}{vbCrLf}")
                Catch
                End Try
            End Sub

            Public Sub ShowHint(Message As String, Optional IsError As Boolean = False) Implements IPluginContext.ShowHint
                Try
                    Dim HintTypeValue As HintType = If(IsError, HintType.Red, HintType.Blue)
                    If RunInUi() Then
                        Hint(Message, HintTypeValue)
                    Else
                        RunInUi(Sub() Hint(Message, HintTypeValue))
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, $"插件 {PluginId} 显示提示失败")
                End Try
            End Sub

            Public Function GetSetting(Key As String) As String Implements IPluginContext.GetSetting
                Try
                    SyncLock SettingsLock
                        Dim Token As JToken = SettingsJson(Key)
                        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return ""
                        Return Token.ToString()
                    End SyncLock
                Catch ex As Exception
                    Logger.Warn(ex, $"插件 {PluginId} 读取设置 {Key} 失败")
                    Return ""
                End Try
            End Function

            Public Sub SetSetting(Key As String, Value As String) Implements IPluginContext.SetSetting
                Try
                    SyncLock SettingsLock
                        SettingsJson(Key) = New JValue(If(Value, ""))
                        FileUtils.Write(SettingsFile, SettingsJson.ToString(Newtonsoft.Json.Formatting.None))
                    End SyncLock
                Catch ex As Exception
                    Logger.Warn(ex, $"插件 {PluginId} 保存设置 {Key} 失败")
                End Try
            End Sub

        End Class

    End Module

End Namespace
