Imports System.CodeDom.Compiler
Imports System.Diagnostics
Imports System.Reflection
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
    End Class

    ''' <summary>
    ''' 插件管理器。负责扫描、加载、卸载插件，以及转发游戏事件。
    ''' </summary>
    Public Module PluginManager

        Private ReadOnly PluginLock As New Object
        Private PluginEntries As New List(Of PluginEntry)
        Private LoadedPlugins As New List(Of PluginRecord)

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
                    For Each Folder As String In DirectoryUtils.EnumerateDirectories(PluginFolder)
                        LoadPluginFromFolder(Folder, State)
                    Next
                    Logger.Info($"插件扫描完成，共发现 {PluginEntries.Count} 个插件，已加载 {LoadedPlugins.Count} 个")
                End SyncLock
            Catch ex As Exception
                Logger.Warn(ex, "扫描插件目录失败")
            End Try
        End Sub

        ''' <summary>
        ''' 读取插件目录中的 plugin.json 并尝试加载。
        ''' </summary>
        Private Sub LoadPluginFromFolder(Folder As String, State As JObject)
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
                    .EntryFile = EntryFile
                }
                If String.IsNullOrEmpty(Entry.Name) Then Entry.Name = Id
                Entry.Enabled = GetEnabledState(State, Id)
            Catch ex As Exception
                Logger.Warn(ex, $"读取插件信息失败：{MetaFile}")
                Return
            End Try
            PluginEntries.Add(Entry)
            If Entry.Enabled Then LoadPlugin(Entry, Folder)
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
                    Instance = New LuaPlugin(EntryPath, Context)
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

            Private ReadOnly Engine As Script

            '允许插件读写的启动器设置（只放数值型与布尔型，避免误改路径等关键项）
            Private Shared ReadOnly SettingKinds As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
                {"LaunchRamCustom", "int"},
                {"UiLauncherTransparent", "int"},
                {"LaunchArgumentRam", "bool"},
                {"UiBackgroundColorful", "bool"}
            }

            Public Sub New(EntryPath As String, Context As IPluginContext)
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
                Api.Set("GetInstance", DynValue.NewCallback(Function(C, A) ApiGetInstance()))
                Api.Set("Backup", DynValue.NewCallback(Function(C, A) ApiBackup()))
                Api.Set("OpenUrl", DynValue.NewCallback(Function(C, A) ApiOpenUrl(A)))
                Api.Set("OpenFolder", DynValue.NewCallback(Function(C, A) ApiOpenFolder(A)))
                Api.Set("RunCommand", DynValue.NewCallback(Function(C, A) ApiRunCommand(A)))
                Api.Set("GetSettingValue", DynValue.NewCallback(Function(C, A) ApiGetSettingValue(A)))
                Api.Set("SetSettingValue", DynValue.NewCallback(Function(C, A) ApiSetSettingValue(A)))
                Api.Set("ReadFile", DynValue.NewCallback(Function(C, A) ApiReadFile(Context, A)))
                Api.Set("WriteFile", DynValue.NewCallback(Function(C, A) ApiWriteFile(Context, A)))
                Engine.Globals.Set("pcl", DynValue.NewTable(Api))
                Engine.DoFile(EntryPath)
            End Sub

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

            '当前实例信息：名称、加载器、游戏版本、Mod 数量、实例目录
            Private Function ApiGetInstance() As DynValue
                Try
                    Dim Inst As McInstance = McInstanceSelected
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

            '执行外部命令，必须先经过用户确认
            Private Shared Function ApiRunCommand(Args As CallbackArguments) As DynValue
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

            Private Shared Function ApiReadFile(Context As IPluginContext, Args As CallbackArguments) As DynValue
                Dim Full As String = SafePluginPath(Context.DataPath, ArgText(Args))
                If Full = "" OrElse Not FileUtils.Exists(Full) Then Return DynValue.NewString("")
                Try
                    Return DynValue.NewString(FileUtils.ReadAsString(Full))
                Catch ex As Exception
                    Logger.Warn(ex, "插件读取文件失败")
                    Return DynValue.NewString("")
                End Try
            End Function

            Private Shared Function ApiWriteFile(Context As IPluginContext, Args As CallbackArguments) As DynValue
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

            '调用脚本里的全局函数；脚本没定义该函数时直接跳过
            Private Sub CallScript(Name As String, ParamArray Args As Object())
                Dim Target As DynValue = Engine.Globals.Get(Name)
                If Target Is Nothing OrElse Target.Type <> DataType.Function Then Return
                Dim Values As DynValue() = Args.Select(Function(A) DynValue.FromObject(Engine, A)).ToArray()
                Engine.Call(Target, Values)
            End Sub

            Public Sub OnLoad(Context As IPluginContext) Implements IPlugin.OnLoad
                CallScript("OnLoad")
            End Sub

            Public Sub OnUnload() Implements IPlugin.OnUnload
                CallScript("OnUnload")
            End Sub

            Public Sub OnGameLaunching(InstanceName As String) Implements IPlugin.OnGameLaunching
                CallScript("OnGameLaunching", InstanceName)
            End Sub

            Public Sub OnGameExited(ExitCode As Integer) Implements IPlugin.OnGameExited
                CallScript("OnGameExited", ExitCode)
            End Sub

            Public Sub OnGameCrashed(ReportFolder As String) Implements IPluginCrashHandler.OnGameCrashed
                CallScript("OnGameCrashed", ReportFolder)
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
                        .EntryFile = E.EntryFile
                    }).ToList()
            End SyncLock
        End Function

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
