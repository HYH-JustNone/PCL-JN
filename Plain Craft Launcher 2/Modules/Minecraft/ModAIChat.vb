''' <summary>
''' PCL-JN：主页 AI 聊天助手。
''' 复用 ModAI 的平台、接口地址、Key 与模型配置，通过 OpenAI 兼容接口进行多轮对话，
''' 并支持以函数调用真实操作启动器（只读工具直接执行，改动类工具需要用户确认）。
''' </summary>
Public Module ModAIChat

    ''' <summary>
    ''' 聊天消息的角色，用于界面区分显示。
    ''' </summary>
    Public Enum AiChatRole
        User
        Assistant
        Tool
    End Enum

    '持久化的单条聊天记录
    Private Class ChatEntry
        Public Role As String
        Public Content As String
    End Class

    '一段完整的对话（点「新对话」时把上一段归档）
    Private Class ChatSession
        Public Time As Date = Date.Now
        Public Messages As New List(Of ChatEntry)
    End Class

#Region "历史记录"

    Private ReadOnly HistoryLock As New Object
    Private History As New List(Of ChatEntry)
    '已归档的历史对话
    Private ArchivedSessions As New List(Of ChatSession)
    Private Const MaxHistoryCount As Integer = 100
    Private Const MaxArchivedSessions As Integer = 20
    Private Const MaxContextCount As Integer = 24

    ''' <summary>
    ''' 聊天记录文件路径。
    ''' </summary>
    Private ReadOnly Property HistoryPath As String
        Get
            Return Paths.AppDataThenName & "AIChat.json"
        End Get
    End Property

    ''' <summary>
    ''' 启动时加载聊天记录；文件损坏或不存在时静默重建，不抛出异常。
    ''' </summary>
    Public Sub InitHistory()
        SyncLock HistoryLock
            History = New List(Of ChatEntry)
            ArchivedSessions = New List(Of ChatSession)
            Try
                If Not FileUtils.Exists(HistoryPath) Then Return
                Dim Text As String = FileUtils.TryReadAsString(HistoryPath)
                If String.IsNullOrWhiteSpace(Text) Then Return
                Dim Token As JToken = JToken.Parse(Text)
                If Token.Type = JTokenType.Array Then
                    '兼容旧格式：整个文件就是一段对话
                    History = ParseEntries(CType(Token, JArray))
                ElseIf Token.Type = JTokenType.Object Then
                    Dim Root As JObject = CType(Token, JObject)
                    Dim Sessions As JArray = TryCast(Root("sessions"), JArray)
                    If Sessions IsNot Nothing Then
                        For Each Item As JToken In Sessions
                            '新格式为 {time, messages}，旧格式直接是消息数组
                            Dim Session As New ChatSession()
                            Dim Obj As JObject = TryCast(Item, JObject)
                            If Obj IsNot Nothing Then
                                Session.Time = ParseTime(JStr(Obj, "time"))
                                Session.Messages = ParseEntries(TryCast(Obj("messages"), JArray))
                            ElseIf Item.Type = JTokenType.Array Then
                                Session.Messages = ParseEntries(CType(Item, JArray))
                            Else
                                Continue For
                            End If
                            ArchivedSessions.Add(Session)
                        Next
                    End If
                    History = ParseEntries(TryCast(Root("current"), JArray))
                End If
                Do While ArchivedSessions.Count > MaxArchivedSessions
                    ArchivedSessions.RemoveAt(0)
                Loop
                TrimHistory()
            Catch ex As Exception
                Logger.Warn(ex, "读取 AI 聊天记录失败，已重置")
                History = New List(Of ChatEntry)
            End Try
        End SyncLock
    End Sub

    '把 JSON 数组解析为对话条目
    Private Function ParseEntries(Arr As JArray) As List(Of ChatEntry)
        Dim Result As New List(Of ChatEntry)
        If Arr Is Nothing Then Return Result
        For Each Token As JToken In Arr
            Dim Obj As JObject = TryCast(Token, JObject)
            If Obj Is Nothing Then Continue For
            Dim Role As String = JStr(Obj, "role")
            Dim Content As String = JStr(Obj, "content")
            If (Role = "user" OrElse Role = "assistant") AndAlso Content <> "" Then
                Result.Add(New ChatEntry With {.Role = Role, .Content = Content})
            End If
        Next
        Return Result
    End Function

    '只保留最近若干条
    Private Sub TrimHistory()
        If History.Count <= MaxHistoryCount Then Return
        History.RemoveRange(0, History.Count - MaxHistoryCount)
    End Sub

    '对话条目的序列化
    Private Function EntriesToArray(Entries As List(Of ChatEntry)) As JArray
        Dim Arr As New JArray
        For Each Entry As ChatEntry In Entries
            Arr.Add(New JObject From {{"role", Entry.Role}, {"content", Entry.Content}})
        Next
        Return Arr
    End Function

    Private Sub SaveHistory()
        Try
            SyncLock HistoryLock
                Dim Root As New JObject
                Dim Sessions As New JArray
                For Each Arch As ChatSession In ArchivedSessions
                    Sessions.Add(New JObject From {
                        {"time", SessionTimeText(Arch.Time)},
                        {"messages", EntriesToArray(Arch.Messages)}
                    })
                Next
                Root("sessions") = Sessions
                Root("current") = EntriesToArray(History)
                FileUtils.Write(HistoryPath, Root.ToString(Formatting.None))
            End SyncLock
        Catch ex As Exception
            Logger.Warn(ex, "保存 AI 聊天记录失败")
        End Try
    End Sub

    ''' <summary>
    ''' 开始新对话：把当前对话归档，并清空上下文。
    ''' </summary>
    Public Sub NewSession()
        SyncLock HistoryLock
            If History.Count > 0 Then
                ArchivedSessions.Add(New ChatSession With {.Time = Date.Now, .Messages = New List(Of ChatEntry)(History)})
                Do While ArchivedSessions.Count > MaxArchivedSessions
                    ArchivedSessions.RemoveAt(0)
                Loop
                History = New List(Of ChatEntry)
            End If
        End SyncLock
        SaveHistory()
    End Sub

    ''' <summary>
    ''' 清空全部聊天记录（包含已归档的旧对话）。
    ''' </summary>
    Public Sub ClearHistory()
        SyncLock HistoryLock
            History = New List(Of ChatEntry)
            ArchivedSessions.Clear()
        End SyncLock
        SaveHistory()
    End Sub

    ''' <summary>
    ''' 获取历史会话摘要，Key 为归档索引，Value 为显示文本（最新的在前）。
    ''' </summary>
    Public Function GetArchivedSessions() As List(Of KeyValuePair(Of Integer, String))
        SyncLock HistoryLock
            Dim Result As New List(Of KeyValuePair(Of Integer, String))
            For i As Integer = ArchivedSessions.Count - 1 To 0 Step -1
                Result.Add(New KeyValuePair(Of Integer, String)(i, DescribeSession(ArchivedSessions(i))))
            Next
            Return Result
        End SyncLock
    End Function

    ''' <summary>
    ''' 载入某段历史会话：先把当前对话归档，再把该段设为当前对话。
    ''' </summary>
    Public Sub ResumeSession(Index As Integer)
        SyncLock HistoryLock
            If Index < 0 OrElse Index >= ArchivedSessions.Count Then Return
            Dim Target As ChatSession = ArchivedSessions(Index)
            If History.Count > 0 Then
                ArchivedSessions.Add(New ChatSession With {.Time = Date.Now, .Messages = New List(Of ChatEntry)(History)})
                Do While ArchivedSessions.Count > MaxArchivedSessions
                    ArchivedSessions.RemoveAt(0)
                Loop
            End If
            History = New List(Of ChatEntry)(Target.Messages)
            ArchivedSessions.Remove(Target)
            TrimHistory()
        End SyncLock
        SaveHistory()
    End Sub

    '历史会话的显示文本：时间 + 首句提问 + 条数
    Private Function DescribeSession(Session As ChatSession) As String
        Dim Preview As String = ""
        For Each Entry As ChatEntry In Session.Messages
            If Entry.Role = "user" Then
                Preview = Entry.Content.Replace(vbCrLf, " ").Replace(vbLf, " ")
                Exit For
            End If
        Next
        If Preview.Length > 26 Then Preview = Preview.Substring(0, 26) & "…"
        If Preview = "" Then Preview = "（空对话）"
        Return Session.Time.ToString("MM-dd HH:mm") & "　" & Preview & "（" & Session.Messages.Count & " 条）"
    End Function

    Private Function SessionTimeText(Time As Date) As String
        Return Time.ToString("yyyy-MM-dd HH:mm:ss")
    End Function

    Private Function ParseTime(Text As String) As Date
        Dim Result As Date
        If Date.TryParseExact(Text, "yyyy-MM-dd HH:mm:ss", Globalization.CultureInfo.InvariantCulture,
                              Globalization.DateTimeStyles.None, Result) Then Return Result
        Return Date.Now
    End Function

    ''' <summary>
    ''' 读取已保存的聊天记录，供主页加载时回显。
    ''' </summary>
    Public Function GetHistoryForDisplay() As List(Of KeyValuePair(Of String, String))
        Dim Result As New List(Of KeyValuePair(Of String, String))
        SyncLock HistoryLock
            For Each Entry As ChatEntry In History
                Result.Add(New KeyValuePair(Of String, String)(Entry.Role, Entry.Content))
            Next
        End SyncLock
        Return Result
    End Function

#End Region

#Region "发送流程"

    Private _IsBusy As Boolean = False

    ''' <summary>
    ''' 是否正在等待 AI 回复。
    ''' </summary>
    Public ReadOnly Property IsBusy As Boolean
        Get
            Return _IsBusy
        End Get
    End Property

    ''' <summary>
    ''' 发送一条用户消息。Append 用于把内容追加到聊天界面，可在任意线程调用。
    ''' </summary>
    Public Sub SendAsync(UserText As String, Append As Action(Of String, AiChatRole))
        If String.IsNullOrWhiteSpace(UserText) Then Return
        SyncLock HistoryLock
            If _IsBusy Then Return
            _IsBusy = True
            History.Add(New ChatEntry With {.Role = "user", .Content = UserText})
            TrimHistory()
        End SyncLock
        SaveHistory()

        RunInNewThread(
        Sub()
            Try
                SendCore(Append)
            Catch ex As Exception
                Logger.Warn(ex, "AI 聊天请求失败")
                Append("✗ 失败：" & ex.Message, AiChatRole.Tool)
            Finally
                _IsBusy = False
            End Try
        End Sub, "AI Chat")
    End Sub

    Private Sub SendCore(Append As Action(Of String, AiChatRole))
        Dim Messages As JArray = BuildMessages()
        Dim UseTools As Boolean = True
        Dim ToolRounds As Integer = 0
        Do
            Dim Result As ChatResponse = PostChat(Messages, UseTools)
            If Result.ErrorMessage <> "" Then
                '模型不支持工具调用时退化为普通对话
                If UseTools AndAlso Result.StatusCode = 400 AndAlso Result.Raw.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    UseTools = False
                    Append("✗ 当前模型不支持工具调用，仅能聊天", AiChatRole.Tool)
                    Continue Do
                End If
                Append("✗ 失败：" & Result.ErrorMessage, AiChatRole.Tool)
                Return
            End If
            If Result.Message Is Nothing Then
                Append("✗ 失败：AI 返回的内容为空或格式异常", AiChatRole.Tool)
                Return
            End If

            Dim ToolCalls As JArray = TryCast(Result.Message("tool_calls"), JArray)
            If ToolCalls Is Nothing OrElse ToolCalls.Count = 0 Then
                Dim Content As String = ContentText(Result.Message)
                If Content = "" Then Content = "（模型没有返回内容）"
                Append("AI：" & Content, AiChatRole.Assistant)
                SyncLock HistoryLock
                    History.Add(New ChatEntry With {.Role = "assistant", .Content = Content})
                    TrimHistory()
                End SyncLock
                SaveHistory()
                Return
            End If

            '把带 tool_calls 的助手消息加入上下文
            Messages.Add(New JObject From {
                {"role", "assistant"},
                {"content", ContentText(Result.Message)},
                {"tool_calls", ToolCalls}
            })
            '逐个执行工具
            For Each Token As JToken In ToolCalls
                Dim CallObj As JObject = TryCast(Token, JObject)
                If CallObj Is Nothing Then Continue For
                Dim CallId As String = JStr(CallObj, "id")
                Dim Func As JObject = TryCast(CallObj("function"), JObject)
                Dim ToolName As String = JStr(Func, "name")
                Dim Args As JObject = Nothing
                Try
                    Dim ArgsText As String = JStr(Func, "arguments")
                    Args = If(String.IsNullOrWhiteSpace(ArgsText), New JObject, CType(JObject.Parse(ArgsText), JObject))
                Catch ex As Exception
                    Logger.Warn(ex, $"解析工具参数失败：{ToolName}")
                    Args = New JObject
                End Try
                Dim ToolResult As String = ExecuteTool(ToolName, Args, Append)
                Messages.Add(New JObject From {
                    {"role", "tool"},
                    {"tool_call_id", CallId},
                    {"content", ToolResult}
                })
            Next
            ToolRounds += 1
            If ToolRounds >= 6 Then
                Append("✗ 工具调用轮数已达上限，已停止本轮操作", AiChatRole.Tool)
                Return
            End If
        Loop
    End Sub

    '构造请求消息：系统提示词 + 最近若干条历史
    Private Function BuildMessages() As JArray
        Dim Messages As New JArray From {
            New JObject From {{"role", "system"}, {"content", SystemPrompt}}
        }
        SyncLock HistoryLock
            Dim StartIndex As Integer = Math.Max(0, History.Count - MaxContextCount)
            For i As Integer = StartIndex To History.Count - 1
                Messages.Add(New JObject From {{"role", History(i).Role}, {"content", History(i).Content}})
            Next
        End SyncLock
        Return Messages
    End Function

    Private ReadOnly Property SystemPrompt As String
        Get
            Return "你是 PCL-JN（Plain Craft Launcher）的助手，运行在启动器主页右侧的聊天框中。" & vbCrLf &
                "要求：" & vbCrLf &
                "1. 始终用中文回答，语气简洁直接。" & vbCrLf &
                "2. 先理解用户的需求；能通过工具直接查询的信息（版本、Mod、备份、崩溃日志、设置、Modrinth 搜索结果等）应先调用只读工具查询，不要凭空猜测。" & vbCrLf &
                "3. 涉及任何改动（启动/停止游戏、切换版本、修改设置、安装版本或 Mod、备份或回滚、清理日志、切换页面等）时，必须调用对应的工具；工具执行前会自动向用户弹出确认框，等待用户确认，不要跳过工具直接声称已完成。" & vbCrLf &
                "4. 绝对不要编造执行结果。工具返回失败时如实说明原因，并给出可行的下一步建议。" & vbCrLf &
                "5. 用户问与启动器无关的问题可以正常回答，但不要假装执行了启动器操作。" & vbCrLf &
                "6. 用户描述模糊时，先调用 launcher_status 或 list_versions 了解当前状态，再决定下一步。" & vbCrLf &
                "7. 任何安装、下载类操作（install_version、install_mod、build_modpack、backup_instance、restore_backup 等）之前，必须先调用 get_instance_info 或 list_versions 确认目标实例的加载器与游戏版本，并在回复中写明目标实例、加载器与游戏版本。" & vbCrLf &
                "8. 用户指定的目标实例必须原样传给工具参数。若目标实例不存在，先说明并引导安装，不得拿当前选中的实例顶替；工具返回找不到实例时必须如实告诉用户。" & vbCrLf &
                "9. 工具返回中若提示加载器不匹配、版本不匹配或没有找到匹配版本，必须如实报告失败，不得宣称安装成功。" & vbCrLf &
                "10. 安装或下载完成后，核对工具返回的实际加载器与版本号（可用 get_instance_info 或 list_mods 复查），并在回复中写明。安装 Mod 时优先使用 Modrinth 搜索。"
        End Get
    End Property

    Private Class ChatResponse
        Public Message As JObject
        Public ErrorMessage As String = ""
        Public StatusCode As Integer = 0
        Public Raw As String = ""
    End Class

    '发起一次 Chat Completions 请求，超时 60 秒
    Private Function PostChat(Messages As JArray, UseTools As Boolean) As ChatResponse
        Dim Result As New ChatResponse
        If Not ModAI.IsConfigured() Then
            Result.ErrorMessage = "尚未配置 AI 接口与 API Key"
            Return Result
        End If
        If String.IsNullOrWhiteSpace(ModAI.Model) Then
            Result.ErrorMessage = "尚未配置 AI 模型"
            Return Result
        End If

        Dim Body As New JObject From {
            {"model", ModAI.Model},
            {"messages", Messages},
            {"temperature", 0.2}
        }
        If UseTools Then
            Body("tools") = GetToolDefinitions()
            Body("tool_choice") = "auto"
        End If

        Try
            Using Client As New HttpClient
                Client.Timeout = TimeSpan.FromSeconds(60)
                Using Request As New HttpRequestMessage(HttpMethod.Post, ModAI.ApiUrl)
                    Request.Headers.TryAddWithoutValidation("Authorization", "Bearer " & ModAI.ApiKey)
                    Request.Content = New StringContent(Body.ToString(Formatting.None), Encoding.UTF8, "application/json")
                    Using Response = Client.SendAsync(Request).GetAwaiter().GetResult()
                        Result.StatusCode = CInt(Response.StatusCode)
                        Result.Raw = Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        If Not Response.IsSuccessStatusCode Then
                            Result.ErrorMessage = $"请求失败（HTTP {Result.StatusCode}）：{Result.Raw.Substring(0, Math.Min(Result.Raw.Length, 300))}"
                            Logger.Warn($"AI 聊天请求失败：HTTP {Result.StatusCode}")
                            Return Result
                        End If
                        Dim Json As JObject = JObject.Parse(Result.Raw)
                        Dim Choices As JToken = Json("choices")
                        Dim First As JObject = TryCast(If(Choices Is Nothing, Nothing, Choices.FirstOrDefault()), JObject)
                        Result.Message = If(First Is Nothing, Nothing, TryCast(First("message"), JObject))
                        If Result.Message Is Nothing Then Result.ErrorMessage = "AI 返回的 choices 为空"
                        Return Result
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Result.ErrorMessage = ex.Message
            Return Result
        End Try
    End Function

    '兼容 content 为字符串或分段数组的返回格式
    Private Function ContentText(Message As JObject) As String
        If Message Is Nothing Then Return ""
        Dim Token As JToken = Message("content")
        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return ""
        If Token.Type = JTokenType.Array Then
            Dim Builder As New StringBuilder
            For Each Item As JToken In CType(Token, JArray)
                Dim Obj As JObject = TryCast(Item, JObject)
                If Obj IsNot Nothing AndAlso JStr(Obj, "text") <> "" Then Builder.Append(JStr(Obj, "text"))
            Next
            Return Builder.ToString().Trim()
        End If
        Return Token.ToString().Trim()
    End Function

#End Region

#Region "工具执行"

    '执行一个工具，返回给模型的 JSON 字符串结果
    Private Function ExecuteTool(Name As String, Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Try
            '手动审批模式下，连只读工具也要先确认
            If GetPermissionLevel() = 0 AndAlso IsReadOnlyTool(Name) Then
                If Not AskConfirm("执行只读查询：" & Name) Then Return RejectedWith(Append)
            End If
            Select Case Name
                Case "get_instance_info" : Return ToolGetInstanceInfo(Args, Append)
                Case "list_versions" : Return ToolListVersions(Append)
                Case "launcher_status" : Return ToolLauncherStatus(Append)
                Case "list_mods" : Return ToolListMods(Args, Append)
                Case "get_setting" : Return ToolGetSetting(Args, Append)
                Case "search_mods" : Return ToolSearchMods(Args, Append)
                Case "check_mod_updates" : Return ToolCheckModUpdates(Args, Append)
                Case "mod_selfcheck" : Return ToolModSelfCheck(Args, Append)
                Case "read_latest_crash" : Return ToolReadLatestCrash(Args, Append)
                Case "list_backups" : Return ToolListBackups(Args, Append)
                Case "launch_game" : Return ToolLaunchGame(Args, Append)
                Case "stop_game" : Return ToolStopGame(Args, Append)
                Case "switch_version" : Return ToolSwitchVersion(Args, Append)
                Case "set_setting" : Return ToolSetSetting(Args, Append)
                Case "install_version" : Return ToolInstallVersion(Args, Append)
                Case "install_mod" : Return ToolInstallMod(Args, Append)
                Case "build_modpack" : Return ToolBuildModpack(Args, Append)
                Case "backup_instance" : Return ToolBackupInstance(Args, Append)
                Case "restore_backup" : Return ToolRestoreBackup(Args, Append)
                Case "clean_logs" : Return ToolCleanLogs(Args, Append)
                Case "open_page" : Return ToolOpenPage(Args, Append)
                Case Else
                    Return ResultJson(False, "未知工具：" & Name)
            End Select
        Catch ex As Exception
            Logger.Warn(ex, $"AI 工具执行失败：{Name}")
            Append("✗ 失败：" & Name & "：" & ex.Message, AiChatRole.Tool)
            Return ResultJson(False, ex.Message)
        End Try
    End Function

#End Region

#Region "只读工具"

    Private Function ToolListVersions(Append As Action(Of String, AiChatRole)) As String
        Dim Arr As New JArray
        For Each Inst As McInstance In GetAllInstances()
            Dim Available As Boolean = Inst.State <> McInstance.McInstanceState.Error
            Arr.Add(New JObject From {
                {"name", Inst.Name},
                {"loader", Inst.VersionDisplayName()},
                {"game_version", GameVersionOf(Inst)},
                {"available", Available},
                {"selected", McInstanceSelected IsNot Nothing AndAlso PathUtils.IsSame(Inst.PathVersion, McInstanceSelected.PathVersion)},
                {"info", Inst.Info}
            })
        Next
        Append("✓ 已完成：读取已安装版本列表", AiChatRole.Tool)
        Return ResultJson(True, Arr.ToString(Formatting.None))
    End Function

    Private Function ToolLauncherStatus(Append As Action(Of String, AiChatRole)) As String
        Dim Launching As Boolean = McLaunchLoader.State = LoadState.Loading OrElse
            (McLaunchLoaderReal IsNot Nothing AndAlso McLaunchLoaderReal.State = LoadState.Loading)
        Dim Status As New JObject From {
            {"launcher_version", VersionDisplay},
            {"build_type", BuildTypeDisplay},
            {"current_version", If(McInstanceSelected?.Name, "无")},
            {"current_version_display", If(McInstanceSelected IsNot Nothing, McInstanceSelected.VersionDisplayName(), "无")},
            {"launching", Launching},
            {"running", HasRunningMinecraft},
            {"ram_mode", If(Settings.Get(Of Integer)("LaunchRamType") = 1, "自定义", "自动")},
            {"ram_custom", Settings.Get(Of Integer)("LaunchRamCustom")},
            {"ram_optimize", Settings.Get(Of Boolean)("LaunchArgumentRam")},
            {"ai_platform", ModAI.Platform.Name},
            {"ai_model", ModAI.Model}
        }
        Append("✓ 已完成：读取启动器状态", AiChatRole.Tool)
        Return ResultJson(True, Status.ToString(Formatting.None))
    End Function

    Private Function ToolGetInstanceInfo(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "读取实例信息", InstanceMissingMessage(VersionName))
        Dim Loader As String = "", GameVersion As String = ""
        GetModEnv(Inst, Loader, GameVersion)
        Dim LoaderVersion As String = ""
        Dim LoaderInstalled As Boolean = (Loader = "")
        Try
            Select Case Loader
                Case "fabric"
                    LoaderInstalled = Inst.Version.ModLoaders.HasFlagF(ModLoaders.Fabric)
                    If LoaderInstalled Then LoaderVersion = Inst.Version.ModLoaderVersions(ModLoaders.Fabric)
                Case "neoforge"
                    LoaderInstalled = Inst.Version.ModLoaders.HasFlagF(ModLoaders.NeoForge)
                    If LoaderInstalled Then LoaderVersion = Inst.Version.ModLoaderVersions(ModLoaders.NeoForge)
                Case "forge"
                    LoaderInstalled = Inst.Version.ModLoaders.HasFlagF(ModLoaders.Forge)
                    If LoaderInstalled Then LoaderVersion = Inst.Version.ModLoaderVersions(ModLoaders.Forge)
            End Select
        Catch ex As Exception
            Logger.Warn(ex, "读取实例加载器版本失败")
        End Try
        Dim ModCount As Integer = DirectoryUtils.EnumerateFiles(Inst.PathIndie & "mods\", False, "*.jar").Count()
        Append("✓ 已完成：读取实例信息（" & Inst.Name & "）", AiChatRole.Tool)
        Return ResultJson(True, New JObject From {
            {"name", Inst.Name},
            {"selected", McInstanceSelected IsNot Nothing AndAlso PathUtils.IsSame(Inst.PathVersion, McInstanceSelected.PathVersion)},
            {"loader", If(Loader = "", "vanilla", Loader)},
            {"loader_version", LoaderVersion},
            {"loader_installed", LoaderInstalled},
            {"game_version", GameVersion},
            {"mod_count", ModCount},
            {"display", Inst.VersionDisplayName()}
        }.ToString(Formatting.None))
    End Function

    Private Function ToolListMods(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "找不到指定版本", InstanceMissingMessage(VersionName))
        Dim Arr As New JArray
        Dim ModsFolder As String = Inst.PathIndie & "mods\"
        For Each FilePath As String In DirectoryUtils.EnumerateFiles(ModsFolder, False, "*.jar").Take(100)
            Dim Info As ModChecker.ModInfo = ModChecker.ReadInfo(FilePath)
            If Info Is Nothing Then
                Arr.Add(New JObject From {{"file", Path.GetFileName(FilePath)}})
            Else
                Arr.Add(New JObject From {
                    {"file", Path.GetFileName(FilePath)},
                    {"id", Info.Id},
                    {"name", Info.Name},
                    {"version", Info.Version},
                    {"loader", Info.Loader}
                })
            End If
        Next
        Append("✓ 已完成：读取 Mod 列表", AiChatRole.Tool)
        Return ResultJson(True, New JObject From {
            {"instance", Inst.Name},
            {"count", Arr.Count},
            {"mods", Arr}
        }.ToString(Formatting.None))
    End Function

    Private Function ToolGetSetting(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim Key As String = ArgStr(Args, "key")
        Dim Success As Boolean = False
        Dim Value As Object = ReadWhitelistSetting(Key, Success)
        If Not Success Then Return FailWith(Append, "无法读取设置", "不支持读取该设置项：" & Key)
        Append("✓ 已完成：读取设置 " & Key, AiChatRole.Tool)
        Return ResultJson(True, New JObject From {{"key", Key}, {"value", Value}}.ToString(Formatting.None))
    End Function

    Private Function ToolSearchMods(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim Query As String = ArgStr(Args, "query")
        If Query = "" Then Return FailWith(Append, "搜索 Mod", "缺少搜索关键词")
        Dim Loader As String = ArgStr(Args, "loader").ToLowerInvariant()
        Dim GameVersion As String = ArgStr(Args, "game_version")
        Dim Limit As Integer = ArgInt(Args, "limit", 8).Clamp(1, 20)
        Dim Hits As JArray = SearchModrinth(Query, Loader, GameVersion, Limit)
        Dim Arr As New JArray
        For Each Token As JToken In Hits
            Dim Obj As JObject = TryCast(Token, JObject)
            If Obj Is Nothing Then Continue For
            Arr.Add(New JObject From {
                {"slug", JStr(Obj, "slug")},
                {"title", JStr(Obj, "title")},
                {"downloads", JInt(Obj, "downloads", 0)},
                {"loaders", Obj("categories")},
                {"versions", Obj("versions")},
                {"description", JStr(Obj, "description")}
            })
        Next
        Append("✓ 已完成：搜索 Modrinth（" & Query & "）", AiChatRole.Tool)
        Return ResultJson(True, Arr.ToString(Formatting.None))
    End Function

    Private Function ToolCheckModUpdates(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "检查 Mod 更新", InstanceMissingMessage(VersionName))
        Dim Report As String = ModUpdate.CheckUpdates(Inst.PathIndie)
        If String.IsNullOrWhiteSpace(Report) Then Report = "没有可用的检查结果"
        If Report.Length > 2000 Then Report = Report.Substring(0, 2000) & "…"
        Append("✓ 已完成：检查 Mod 更新", AiChatRole.Tool)
        Return ResultJson(True, Report)
    End Function

    Private Function ToolModSelfCheck(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "Mod 自检", InstanceMissingMessage(VersionName))
        Dim Missing As List(Of String) = Nothing
        Dim Report As String = ModChecker.RunCheck(Inst.PathIndie, AutoFix:=False, KeepReports:=0, MissingDeps:=Missing)
        If String.IsNullOrWhiteSpace(Report) Then Report = "未发现问题"
        Append("✓ 已完成：Mod 自检（只报告，未改动文件）", AiChatRole.Tool)
        Return ResultJson(True, New JObject From {
            {"instance", Inst.Name},
            {"report", Report},
            {"missing_dependencies", If(Missing Is Nothing, New JArray, New JArray(Missing.ToArray()))}
        }.ToString(Formatting.None))
    End Function

    Private Function ToolReadLatestCrash(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "读取崩溃日志", InstanceMissingMessage(VersionName))
        Dim Target As String = ""
        Dim CrashFiles As List(Of String) = DirectoryUtils.EnumerateFiles(Inst.PathIndie & "crash-reports\", False, "*.txt").ToList()
        If CrashFiles.Count > 0 Then
            Target = CrashFiles.OrderByDescending(Function(F) FileUtils.GetInfo(F).LastWriteTime).First()
        Else
            Dim LatestLog As String = Inst.PathIndie & "logs\latest.log"
            If FileUtils.Exists(LatestLog) Then Target = LatestLog
        End If
        If Target = "" Then Return FailWith(Append, "读取崩溃日志", "没有找到崩溃报告或 latest.log")
        Dim Content As String = If(FileUtils.TryReadAsString(Target), "")
        If Content.Length > 4000 Then Content = "（仅显示最后 4000 个字符）" & vbCrLf & Content.Substring(Content.Length - 4000)
        Append("✓ 已完成：读取崩溃日志 " & Path.GetFileName(Target), AiChatRole.Tool)
        Return ResultJson(True, New JObject From {
            {"file", Target},
            {"content", Content}
        }.ToString(Formatting.None))
    End Function

    Private Function ToolListBackups(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "读取备份列表", InstanceMissingMessage(VersionName))
        Dim Arr As New JArray
        For Each Folder As String In DirectoryUtils.EnumerateDirectories(ModBackup.BackupRoot(Inst.PathIndie)).OrderByDescending(Function(F) F)
            Dim ModCount As Integer = DirectoryUtils.EnumerateFiles(Path.Combine(Folder, "mods"), False, "*.jar").Count()
            Arr.Add(New JObject From {
                {"name", PathUtils.GetLastPart(Folder)},
                {"time", FileUtils.GetInfo(Folder).LastWriteTime.ToString("yyyy-MM-dd HH:mm")},
                {"mod_count", ModCount},
                {"has_config", FileUtils.Exists(Path.Combine(Folder, "配置与存档备份.zip"))}
            })
        Next
        Append("✓ 已完成：读取备份列表", AiChatRole.Tool)
        Return ResultJson(True, Arr.ToString(Formatting.None))
    End Function

#End Region

#Region "改动类工具"

    Private Function ToolLaunchGame(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "启动游戏", InstanceMissingMessage(VersionName))
        If Not AskConfirm("启动游戏：" & Inst.Name) Then Return RejectedWith(Append)
        Dim Started As Boolean = RunInUiWait(Function() McLaunchStart(New McLaunchOptions With {.Instance = Inst}))
        If Started Then
            Append("✓ 已完成：开始启动 " & Inst.Name, AiChatRole.Tool)
            Return ResultJson(True, "已开始启动 " & Inst.Name)
        End If
        Append("✗ 失败：启动 " & Inst.Name & " 未能开始（详见启动日志）", AiChatRole.Tool)
        Return ResultJson(False, "启动失败，可能已有游戏正在启动或版本不可用")
    End Function

    Private Function ToolStopGame(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim WasActive As Boolean = HasRunningMinecraft OrElse McLaunchLoader.State = LoadState.Loading OrElse
            (McLaunchLoaderReal IsNot Nothing AndAlso McLaunchLoaderReal.State = LoadState.Loading)
        If Not WasActive AndAlso McWatcherList.Count = 0 Then
            Append("✓ 已完成：当前没有运行中的游戏", AiChatRole.Tool)
            Return ResultJson(True, "当前没有正在启动或运行中的游戏")
        End If
        If Not AskConfirm("停止正在启动或运行中的游戏") Then Return RejectedWith(Append)
        Dim Killed As Integer = 0
        RunInUiWait(Sub()
            If McLaunchLoaderReal IsNot Nothing Then McLaunchLoaderReal.Cancel()
            If McLaunchLoader.State = LoadState.Loading Then McLaunchLoader.Cancel()
            For Each W As Watcher In McWatcherList.ToList()
                Try
                    W.Kill()
                    Killed += 1
                Catch ex As Exception
                    Logger.Warn(ex, "停止游戏进程失败")
                End Try
            Next
        End Sub)
        If WasActive Then
            Append("✓ 已完成：已停止游戏", AiChatRole.Tool)
            Return ResultJson(True, "已停止当前启动或运行中的游戏，关闭进程数：" & Killed)
        End If
        Append("✓ 已完成：已关闭 " & Killed & " 个残留游戏进程", AiChatRole.Tool)
        Return ResultJson(True, "已关闭 " & Killed & " 个残留游戏进程")
    End Function

    Private Function ToolSwitchVersion(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "切换版本", InstanceMissingMessage(VersionName))
        If Inst.State = McInstance.McInstanceState.Error Then
            Return FailWith(Append, "切换版本", "该版本不可用：" & Inst.Info)
        End If
        If Not AskConfirm("切换当前版本到：" & Inst.Name) Then Return RejectedWith(Append)
        RunInUiWait(Sub()
            McInstanceSelected = Inst
            If FrmLaunchLeft IsNot Nothing Then
                FrmLaunchLeft.RefreshButtonsUI()
                FrmLaunchLeft.RefreshPage(False, False)
            End If
        End Sub)
        Append("✓ 已完成：切换到版本 " & Inst.Name, AiChatRole.Tool)
        Return ResultJson(True, "已切换到版本 " & Inst.Name)
    End Function

    Private Function ToolSetSetting(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim Key As String = ArgStr(Args, "key")
        Dim Value As String = ArgStr(Args, "value")
        If Key = "" Then Return FailWith(Append, "修改设置", "缺少设置键名")
        If Value = "" Then Return FailWith(Append, "修改设置", "缺少目标值")
        If Not IsWritableSettingKey(Key) Then Return FailWith(Append, "修改设置", "不允许修改设置项：" & Key)
        If Not AskConfirm("把设置 " & Key & " 修改为 " & Value) Then Return RejectedWith(Append)
        '设置项可能带有会刷新界面的 OnChanged 回调，因此改到 UI 线程执行
        Dim ErrorText As String = ""
        RunInUiWait(Sub() ErrorText = SetWhitelistSetting(Key, Value))
        If ErrorText <> "" Then Return FailWith(Append, "修改设置", ErrorText)
        Append("✓ 已完成：设置 " & Key & " 已改为 " & Value, AiChatRole.Tool)
        Return ResultJson(True, "已将 " & Key & " 设为 " & Value)
    End Function

    Private Function ToolInstallVersion(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim GameVersion As String = ArgStr(Args, "game_version")
        Dim Loader As String = ArgStr(Args, "loader").ToLowerInvariant()
        Dim LoaderVersion As String = ArgStr(Args, "loader_version")
        If GameVersion = "" Then Return FailWith(Append, "安装版本", "缺少 game_version")
        If Loader = "" Then Loader = "vanilla"
        If Loader = "quilt" Then
            Return FailWith(Append, "安装版本", "启动器暂不支持直接安装 Quilt，请改用 Fabric，或手动安装")
        End If
        Dim Desc As String = "安装 Minecraft " & GameVersion
        If Loader <> "vanilla" Then Desc &= " + " & Loader & If(LoaderVersion = "", "", " " & LoaderVersion)
        If Not AskConfirm(Desc) Then Return RejectedWith(Append)

        Dim JsonUrl As String = DlClientListGet(GameVersion)
        If JsonUrl = "" Then Return FailWith(Append, "安装版本", "无法获取 " & GameVersion & " 的版本信息，请确认版本号是否正确")

        Dim Components As McComponents
        Dim ForgeEntry As DlForgeVersionEntry = Nothing
        Dim NeoForgeEntry As DlNeoForgeListEntry = Nothing
        Select Case Loader
            Case "vanilla"
                Components = New McComponents(GameVersion, New Dictionary(Of ModLoaders, String))
            Case "fabric"
                Dim FabricVersion As String = If(LoaderVersion <> "", LoaderVersion, GetLatestFabricLoader())
                If FabricVersion = "" Then Return FailWith(Append, "安装版本", "无法获取 Fabric 加载器版本")
                Components = New McComponents(GameVersion, New Dictionary(Of ModLoaders, String) From {{ModLoaders.Fabric, FabricVersion}})
            Case "forge"
                Dim ForgeList As List(Of DlForgeVersionEntry) = GetForgeVersions(GameVersion)
                If ForgeList.Count = 0 Then Return FailWith(Append, "安装版本", "找不到适配 " & GameVersion & " 的 Forge 版本")
                If LoaderVersion <> "" Then
                    ForgeEntry = ForgeList.FirstOrDefault(Function(E) E.VersionName.Equals(LoaderVersion, StringComparison.OrdinalIgnoreCase))
                    If ForgeEntry Is Nothing Then
                        Return FailWith(Append, "安装版本", "Forge 没有版本「" & LoaderVersion & "」（Minecraft " & GameVersion & "）。可选版本：" & VersionChoicesText(ForgeList.Select(Function(E) E.VersionName)))
                    End If
                Else
                    ForgeEntry = ForgeList.FirstOrDefault(Function(E) E.IsRecommended)
                    If ForgeEntry Is Nothing Then ForgeEntry = ForgeList.First()
                End If
                Components = New McComponents(GameVersion, New Dictionary(Of ModLoaders, String) From {{ModLoaders.Forge, ForgeEntry.VersionName}})
            Case "neoforge"
                Dim NeoList As List(Of DlNeoForgeListEntry) = GetNeoForgeVersions(GameVersion)
                If NeoList.Count = 0 Then Return FailWith(Append, "安装版本", "找不到适配 " & GameVersion & " 的 NeoForge 版本")
                If LoaderVersion <> "" Then
                    NeoForgeEntry = NeoList.FirstOrDefault(Function(E) E.VersionName.Equals(LoaderVersion, StringComparison.OrdinalIgnoreCase))
                    If NeoForgeEntry Is Nothing Then
                        Return FailWith(Append, "安装版本", "NeoForge 没有版本「" & LoaderVersion & "」（Minecraft " & GameVersion & "）。可选版本：" & VersionChoicesText(NeoList.Select(Function(E) E.VersionName)))
                    End If
                Else
                    NeoForgeEntry = NeoList.FirstOrDefault(Function(E) Not E.IsBeta)
                    If NeoForgeEntry Is Nothing Then NeoForgeEntry = NeoList.First()
                End If
                Components = New McComponents(GameVersion, New Dictionary(Of ModLoaders, String) From {{ModLoaders.NeoForge, NeoForgeEntry.VersionName}})
            Case Else
                Return FailWith(Append, "安装版本", "未知的加载器：" & Loader)
        End Select

        Dim InstanceName As String = GameVersion
        If Loader <> "vanilla" Then InstanceName &= "-" & Loader.Substring(0, 1).ToUpperInvariant() & Loader.Substring(1)
        InstanceName = GetUnusedInstanceName(InstanceName)
        Dim Request As New McInstallRequest With {
            .NewInstanceName = InstanceName,
            .InstanceFolder = McFolderSelected & "versions\" & InstanceName & "\",
            .Components = Components,
            .MinecraftJson = JsonUrl,
            .ForgeEntry = ForgeEntry,
            .NeoForgeEntry = NeoForgeEntry
        }
        Dim Started As Boolean = RunInUiWait(Function() McInstallStart(Request))
        If Started Then
            Dim LoaderVersionText As String = ""
            If Components.ModLoaderVersions.Count > 0 Then LoaderVersionText = String.Join("、", Components.ModLoaderVersions.Values)
            Dim SuccessText As String = "已开始安装 " & InstanceName & If(LoaderVersionText = "", "", "（" & Loader & " " & LoaderVersionText & "）")
            Append("✓ 已完成：" & SuccessText, AiChatRole.Tool)
            Return ResultJson(True, New JObject From {
                {"instance", InstanceName},
                {"game_version", GameVersion},
                {"loader", Loader},
                {"loader_version", LoaderVersionText},
                {"message", SuccessText}
            }.ToString(Formatting.None))
        End If
        Append("✗ 失败：安装 " & InstanceName & " 未能开始", AiChatRole.Tool)
        Return ResultJson(False, "安装未能开始，详见日志")
    End Function

    Private Function ToolInstallMod(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim TargetName As String = ArgStr(Args, "target_version")
        Dim Inst As McInstance = ResolveInstance(TargetName)
        If Inst Is Nothing Then Return FailWith(Append, "安装 Mod", InstanceMissingMessage(TargetName))
        Dim Loader As String = "", GameVersion As String = ""
        GetModEnv(Inst, Loader, GameVersion)
        If Loader = "" OrElse GameVersion = "" Then
            Return FailWith(Append, "安装 Mod", "无法确定实例 " & Inst.Name & " 的加载器或游戏版本，请先用 get_instance_info 确认")
        End If
        Dim ProjectId As String = ArgStr(Args, "project_id")
        Dim Query As String = ArgStr(Args, "query")
        Dim VersionHint As String = ArgStr(Args, "version")
        If ProjectId = "" AndAlso Query = "" Then Return FailWith(Append, "安装 Mod", "需要提供 query 或 project_id")
        Dim Title As String = ProjectId
        Dim Best As JObject = Nothing
        If ProjectId = "" Then
            Dim Hits As JArray = SearchModrinth(Query, Loader, GameVersion, 5)
            If Hits.Count = 0 Then Return FailWith(Append, "安装 Mod", "Modrinth 上没有找到与 " & Loader & " / " & GameVersion & " 匹配的 Mod：" & Query)
            '逐个尝试搜索结果，避免把不相关或没有匹配版本的项目当成目标
            For Each Token As JToken In Hits
                Dim Hit As JObject = TryCast(Token, JObject)
                If Hit Is Nothing Then Continue For
                Dim HitId As String = JStr(Hit, "project_id")
                If HitId = "" Then HitId = JStr(Hit, "slug")
                If HitId = "" Then Continue For
                Dim HitBest As JObject = FindBestVersion(GetProjectVersions(HitId), Loader, GameVersion, VersionHint)
                If HitBest Is Nothing Then Continue For
                ProjectId = HitId
                Best = HitBest
                Title = JStr(Hit, "title")
                If Title = "" Then Title = Query
                Exit For
            Next
            If Best Is Nothing Then
                Return FailWith(Append, "安装 Mod", "搜索结果中没有适配 " & Loader & " / " & GameVersion & " 的版本：" & Query)
            End If
        Else
            Best = FindBestVersion(GetProjectVersions(ProjectId), Loader, GameVersion, VersionHint)
            If Best Is Nothing Then Return FailWith(Append, "安装 Mod", "项目 " & ProjectId & " 没有适配 " & Loader & " / " & GameVersion & " 的版本")
        End If
        Dim FileObj As JObject = GetPrimaryFile(Best)
        If FileObj Is Nothing Then Return FailWith(Append, "安装 Mod", "没有找到可下载的文件")
        If Not AskConfirm("安装 Mod：" & Title & "（" & JStr(Best, "version_number") & "，" & Loader & " / " & GameVersion & "）到实例 " & Inst.Name) Then
            Return RejectedWith(Append)
        End If
        Dim FileName As String = DownloadModFile(FileObj, Inst.PathIndie & "mods\")
        Dim LocalPath As String = Inst.PathIndie & "mods\" & FileName
        Dim ActualLoader As String = ""
        Dim VerifyError As String = VerifyDownloadedModLoader(Inst, LocalPath, Loader, ActualLoader)
        If VerifyError <> "" Then
            Append("✗ 失败：" & VerifyError, AiChatRole.Tool)
            Return ResultJson(False, VerifyError)
        End If
        Append("✓ 已完成：已下载 " & FileName & "（" & If(ActualLoader = "", "无法识别加载器", ActualLoader) & "）到 " & Inst.Name, AiChatRole.Tool)
        Return ResultJson(True, New JObject From {
            {"instance", Inst.Name},
            {"loader", Loader},
            {"game_version", GameVersion},
            {"file", FileName},
            {"mod_version", JStr(Best, "version_number")},
            {"actual_loader", ActualLoader}
        }.ToString(Formatting.None))
    End Function

    Private Function ToolBuildModpack(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim TargetName As String = ArgStr(Args, "target_version")
        Dim Inst As McInstance = ResolveInstance(TargetName)
        If Inst Is Nothing Then Return FailWith(Append, "批量安装 Mod", InstanceMissingMessage(TargetName))
        Dim KeywordsToken As JToken = Args("keywords")
        If KeywordsToken Is Nothing OrElse KeywordsToken.Type <> JTokenType.Array Then
            Return FailWith(Append, "批量安装 Mod", "缺少 keywords 数组")
        End If
        Dim Loader As String = "", GameVersion As String = ""
        GetModEnv(Inst, Loader, GameVersion)
        If Loader = "" OrElse GameVersion = "" Then
            Return FailWith(Append, "批量安装 Mod", "无法确定实例 " & Inst.Name & " 的加载器或游戏版本，请先用 get_instance_info 确认")
        End If

        Dim Keywords As New List(Of String)
        For Each Token As JToken In CType(KeywordsToken, JArray)
            If Token.ToString().Trim() <> "" Then Keywords.Add(Token.ToString().Trim())
        Next
        If Keywords.Count = 0 Then Return FailWith(Append, "批量安装 Mod", "keywords 为空")
        If Keywords.Count > 10 Then Keywords = Keywords.Take(10).ToList()

        '先解析出将要安装的清单：按 project_id 去重，且只接受真正有匹配版本的搜索结果
        Dim Plans As New List(Of(FileObj As JObject, Title As String, VersionName As String))
        Dim Skipped As New List(Of String)
        Dim UsedProjects As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each Keyword As String In Keywords
            Try
                Dim Hits As JArray = SearchModrinth(Keyword, Loader, GameVersion, 5)
                If Hits.Count = 0 Then
                    Skipped.Add(Keyword & "（没有搜索结果）")
                    Continue For
                End If
                Dim ChosenHit As JObject = Nothing
                Dim ChosenBest As JObject = Nothing
                Dim ChosenFile As JObject = Nothing
                Dim ChosenId As String = ""
                Dim DuplicateName As String = ""
                For Each Token As JToken In Hits
                    Dim Hit As JObject = TryCast(Token, JObject)
                    If Hit Is Nothing Then Continue For
                    Dim ProjectId As String = JStr(Hit, "project_id")
                    If ProjectId = "" Then ProjectId = JStr(Hit, "slug")
                    If ProjectId = "" Then Continue For
                    If UsedProjects.Contains(ProjectId) Then
                        DuplicateName = JStr(Hit, "title")
                        Continue For
                    End If
                    Dim Best As JObject = FindBestVersion(GetProjectVersions(ProjectId), Loader, GameVersion, "")
                    If Best Is Nothing Then Continue For
                    Dim FileObj As JObject = GetPrimaryFile(Best)
                    If FileObj Is Nothing Then Continue For
                    ChosenHit = Hit
                    ChosenBest = Best
                    ChosenFile = FileObj
                    ChosenId = ProjectId
                    Exit For
                Next
                If ChosenFile Is Nothing Then
                    If DuplicateName <> "" Then
                        Skipped.Add(Keyword & "（与已选 Mod 重复：" & DuplicateName & "）")
                    Else
                        Skipped.Add(Keyword & "（没有 " & Loader & " / " & GameVersion & " 的匹配版本）")
                    End If
                    Continue For
                End If
                UsedProjects.Add(ChosenId)
                Plans.Add((ChosenFile, JStr(ChosenHit, "title"), JStr(ChosenBest, "version_number")))
            Catch ex As Exception
                Logger.Warn(ex, "解析批量安装清单失败：" & Keyword)
                Skipped.Add(Keyword & "（解析失败）")
            End Try
        Next
        If Plans.Count = 0 Then
            Return FailWith(Append, "批量安装 Mod", "没有任何可安装的 Mod。原因：" & String.Join("；", Skipped))
        End If

        '列出具体清单让用户确认
        Dim ListText As String = String.Join(vbCrLf & "- ", Plans.Select(Function(P) P.Title & " " & P.VersionName))
        If Not AskConfirm("为实例 " & Inst.Name & "（" & Loader & " / " & GameVersion & "）安装以下 " & Plans.Count & " 个 Mod：{v0}- {v1}".Replace("{v0}", vbCrLf).Replace("{v1}", ListText)) Then
            Return RejectedWith(Append)
        End If

        '逐个下载，并在下载后校验 jar 的实际加载器
        Dim Success As New JArray
        Dim Failed As New List(Of String)
        For Each Plan In Plans
            Try
                Dim FileName As String = DownloadModFile(Plan.FileObj, Inst.PathIndie & "mods\")
                Dim ActualLoader As String = ""
                Dim VerifyError As String = VerifyDownloadedModLoader(Inst, Inst.PathIndie & "mods\" & FileName, Loader, ActualLoader)
                If VerifyError <> "" Then
                    Failed.Add(Plan.Title & "：" & VerifyError)
                    Continue For
                End If
                Success.Add(New JObject From {
                    {"name", Plan.Title},
                    {"file", FileName},
                    {"version", Plan.VersionName},
                    {"actual_loader", ActualLoader}
                })
            Catch ex As Exception
                Logger.Warn(ex, "下载 Mod 失败：" & Plan.Title)
                Failed.Add(Plan.Title & "：" & ex.Message)
            End Try
        Next
        Failed.AddRange(Skipped)

        If Success.Count > 0 Then
            Append("✓ 已完成：已安装 " & Success.Count & " 个 Mod 到 " & Inst.Name, AiChatRole.Tool)
        End If
        If Failed.Count > 0 Then
            Append("✗ 失败：" & String.Join("；", Failed), AiChatRole.Tool)
        End If
        Return ResultJson(Success.Count > 0 AndAlso Failed.Count = 0, New JObject From {
            {"instance", Inst.Name},
            {"loader", Loader},
            {"game_version", GameVersion},
            {"installed", Success},
            {"failed", New JArray(Failed.ToArray())}
        }.ToString(Formatting.None))
    End Function

    Private Function ToolBackupInstance(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "备份实例", InstanceMissingMessage(VersionName))
        If Not AskConfirm("立即备份版本 " & Inst.Name & " 的配置与存档") Then Return RejectedWith(Append)
        Dim NewFolder As String = ModBackup.ForceBackup(Inst.PathIndie)
        If NewFolder IsNot Nothing AndAlso NewFolder <> "" Then
            Append("✓ 已完成：已创建备份 " & PathUtils.GetLastPart(NewFolder), AiChatRole.Tool)
            Return ResultJson(True, "已创建备份：" & NewFolder)
        End If
        Append("✓ 已完成：没有可备份的内容，未生成新备份", AiChatRole.Tool)
        Return ResultJson(True, "没有可备份的内容，未生成新备份")
    End Function

    Private Function ToolRestoreBackup(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "回滚备份", InstanceMissingMessage(VersionName))
        Dim BackupName As String = ArgStr(Args, "backup")
        If BackupName = "" Then Return FailWith(Append, "回滚备份", "缺少备份名称")
        Dim BackupFolder As String = ""
        For Each Folder As String In DirectoryUtils.EnumerateDirectories(ModBackup.BackupRoot(Inst.PathIndie))
            If PathUtils.GetLastPart(Folder).Equals(BackupName, StringComparison.OrdinalIgnoreCase) Then
                BackupFolder = Folder
                Exit For
            End If
        Next
        If BackupFolder = "" Then Return FailWith(Append, "回滚备份", "找不到备份：" & BackupName)
        Dim RestoreContent As Boolean = ArgBool(Args, "restore_content", True)
        If Not AskConfirm("用备份 " & BackupName & " 回滚版本 " & Inst.Name & If(RestoreContent, "（含配置与存档）", "（仅 Mod）")) Then
            Return RejectedWith(Append)
        End If
        Dim Summary As String = ModBackup.Restore(Inst.PathIndie, BackupFolder, RestoreContent)
        Append("✓ 已完成：" & Summary, AiChatRole.Tool)
        Return ResultJson(True, Summary)
    End Function

    Private Function ToolCleanLogs(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim VersionName As String = ArgStr(Args, "version")
        Dim Inst As McInstance = ResolveInstance(VersionName)
        If Inst Is Nothing Then Return FailWith(Append, "清理日志", InstanceMissingMessage(VersionName))
        If Not AskConfirm("清理版本 " & Inst.Name & " 的旧日志与旧崩溃报告（保留最近 5 份并移入备份目录）") Then
            Return RejectedWith(Append)
        End If
        Dim Stamp As String = Date.Now.ToString("yyyyMMdd-HHmmss")
        Dim Target As String = ModBackup.BackupRoot(Inst.PathIndie) & "logs-" & Stamp & "\"
        Dim MovedLogs As Integer = MoveOldFiles(Inst.PathIndie & "logs\", Target & "logs\", 5)
        Dim MovedCrashes As Integer = MoveOldFiles(Inst.PathIndie & "crash-reports\", Target & "crash-reports\", 5)
        If MovedLogs + MovedCrashes = 0 Then
            Append("✓ 已完成：没有需要清理的旧日志", AiChatRole.Tool)
            Return ResultJson(True, "没有需要清理的旧日志")
        End If
        Append($"✓ 已完成：已移出 {MovedLogs} 份日志与 {MovedCrashes} 份崩溃报告", AiChatRole.Tool)
        Return ResultJson(True, $"已移出 {MovedLogs} 份日志与 {MovedCrashes} 份崩溃报告，原文件保存在 {Target}")
    End Function

    Private Function ToolOpenPage(Args As JObject, Append As Action(Of String, AiChatRole)) As String
        Dim Page As String = ArgStr(Args, "page").ToLowerInvariant()
        Dim PageName As String = "", SubType As FormMain.PageSubType = FormMain.PageSubType.Default
        Select Case Page
            Case "launch", "home", "start"
                PageName = "启动"
            Case "download", "install"
                PageName = "下载"
                SubType = FormMain.PageSubType.DownloadInstall
            Case "setup", "settings"
                PageName = "设置"
                SubType = FormMain.PageSubType.SetupLaunch
            Case "instance", "select"
                PageName = "版本选择"
            Case "other", "more"
                PageName = "更多"
                SubType = FormMain.PageSubType.OtherHelp
            Case "link", "multiplayer"
                PageName = "联机"
            Case "task", "tasks"
                PageName = "任务管理"
            Case Else
                Return FailWith(Append, "切换页面", "不支持的页面：" & Page)
        End Select
        If Not AskConfirm("切换到页面：" & PageName) Then Return RejectedWith(Append)
        Dim ErrorText As String = ""
        RunInUiWait(Sub()
            If FrmMain Is Nothing Then
                ErrorText = "主窗口尚未加载，无法切换页面"
                Return
            End If
            Select Case Page
                Case "launch", "home", "start" : FrmMain.PageChange(FormMain.PageType.Launch)
                Case "download", "install" : FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall)
                Case "setup", "settings" : FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLaunch)
                Case "instance", "select" : FrmMain.PageChange(FormMain.PageType.InstanceSelect)
                Case "other", "more" : FrmMain.PageChange(FormMain.PageType.Other, FormMain.PageSubType.OtherHelp)
                Case "link", "multiplayer" : FrmMain.PageChange(FormMain.PageType.Link)
                Case "task", "tasks" : FrmMain.PageChange(FormMain.PageType.TaskManager)
            End Select
        End Sub)
        If ErrorText <> "" Then Return FailWith(Append, "切换页面", ErrorText)
        Append("✓ 已完成：已切换到" & PageName & "页面", AiChatRole.Tool)
        Return ResultJson(True, "已切换到" & PageName & "页面")
    End Function

#End Region

#Region "工具清单"

    Private Function GetToolDefinitions() As JArray
        Dim Tools As New JArray
        Tools.Add(ToolDef("list_versions", "列出启动器中已安装的所有 Minecraft 版本，包括名称、加载器、游戏版本、是否可用与是否为当前选中版本。", New JObject, New String() {}))
        Tools.Add(ToolDef("launcher_status", "查看启动器版本、当前选中的版本、是否正在启动或运行游戏、内存设置，以及 AI 平台与模型。", New JObject, New String() {}))
        Tools.Add(ToolDef("get_instance_info", "读取指定实例（留空为当前选中实例）的名称、加载器、加载器版本、游戏版本、Mod 数量与加载器是否已安装。安装或下载任何内容前必须先调用本工具确认目标。",
            New JObject From {{"version", SchemaProp("string", "版本文件夹名称（实例名），留空表示当前选中的版本")}}, New String() {}))
        Tools.Add(ToolDef("list_mods", "列出指定版本（留空为当前版本）mods 目录中的每个 jar 的 id、名称、版本与加载器。",
            New JObject From {{"version", SchemaProp("string", "版本文件夹名称，留空表示当前选中的版本")}}, New String() {}))
        Tools.Add(ToolDef("get_setting", "读取一个白名单内的启动器设置的当前值。",
            New JObject From {{"key", SchemaProp("string", "设置键名，例如 LaunchRamCustom、UiHomeAiChat")}}, New String() {"key"}))
        Tools.Add(ToolDef("search_mods", "在 Modrinth 搜索 Mod，返回 slug、标题、下载数、支持的加载器与游戏版本、简述。",
            New JObject From {
                {"query", SchemaProp("string", "搜索关键词")},
                {"loader", SchemaProp("string", "加载器过滤，如 fabric、forge、neoforge、quilt，可留空")},
                {"game_version", SchemaProp("string", "游戏版本过滤，如 1.20.1，可留空")},
                {"limit", SchemaProp("integer", "返回数量，默认 8，最大 20")}
            }, New String() {"query"}))
        Tools.Add(ToolDef("check_mod_updates", "检查指定版本（留空为当前版本）已安装 Mod 的可用更新，返回更新报告。",
            New JObject From {{"version", SchemaProp("string", "版本文件夹名称，留空表示当前选中的版本")}}, New String() {}))
        Tools.Add(ToolDef("mod_selfcheck", "对指定版本（留空为当前版本）执行 Mod 自检，只报告重复 Mod、缺少前置、加载器混装等问题，不会移动任何文件。",
            New JObject From {{"version", SchemaProp("string", "版本文件夹名称，留空表示当前选中的版本")}}, New String() {}))
        Tools.Add(ToolDef("read_latest_crash", "读取指定版本（留空为当前版本）最新的崩溃报告，没有崩溃报告时读取 logs/latest.log 的末尾部分。",
            New JObject From {{"version", SchemaProp("string", "版本文件夹名称，留空表示当前选中的版本")}}, New String() {}))
        Tools.Add(ToolDef("list_backups", "列出指定版本（留空为当前版本）在 PCL\ModBackup 下已有的备份目录、时间与 Mod 数量。",
            New JObject From {{"version", SchemaProp("string", "版本文件夹名称，留空表示当前选中的版本")}}, New String() {}))
        Tools.Add(ToolDef("launch_game", "按名称启动指定的 Minecraft 版本。找不到版本时会返回错误。",
            New JObject From {{"version", SchemaProp("string", "要启动的版本文件夹名称")}}, New String() {"version"}))
        Tools.Add(ToolDef("stop_game", "取消正在进行的启动，并强制关闭当前运行中的 Minecraft。", New JObject, New String() {}))
        Tools.Add(ToolDef("switch_version", "切换启动器当前选中的 Minecraft 版本并刷新界面。",
            New JObject From {{"version", SchemaProp("string", "要切换到的版本文件夹名称")}}, New String() {"version"}))
        Tools.Add(ToolDef("set_setting", "修改一个白名单内、具有明确数值语义的启动器设置。未知键或越界值会被拒绝。",
            New JObject From {
                {"key", SchemaProp("string", "设置键名，例如 LaunchRamCustom、LaunchRamType、UiLauncherLogo")},
                {"value", SchemaProp("string", "目标值，布尔用 true/false，数字直接填写")}
            }, New String() {"key", "value"}))
        Tools.Add(ToolDef("install_version", "安装新的 Minecraft 版本，可带 Forge、NeoForge 或 Fabric 加载器。复用启动器官方安装流程。",
            New JObject From {
                {"game_version", SchemaProp("string", "Minecraft 游戏版本，例如 1.20.1")},
                {"loader", SchemaProp("string", "加载器：vanilla、forge、neoforge、fabric、quilt")},
                {"loader_version", SchemaProp("string", "加载器版本，可留空以自动选择推荐版本；填写时必须真实存在，否则直接失败")}
            }, New String() {"game_version", "loader"}))
        Tools.Add(ToolDef("install_mod", "从 Modrinth 搜索并下载一个与目标实例加载器、游戏版本匹配的 Mod 到它的 mods 目录；下载后会校验 jar 的实际加载器，不匹配则移入备份并报告失败。",
            New JObject From {
                {"query", SchemaProp("string", "搜索关键词，与 project_id 二选一")},
                {"project_id", SchemaProp("string", "Modrinth 项目 ID 或 slug，可留空")},
                {"version", SchemaProp("string", "期望的 Mod 版本号，可留空")},
                {"target_version", SchemaProp("string", "目标实例名称；留空表示当前选中的实例。若非空则必须精确匹配，找不到会失败")}
            }, New String() {}))
        Tools.Add(ToolDef("build_modpack", "为指定版本批量搜索并安装一组 Mod，用于按用户需求自动搭建整合包。会先列出清单让用户确认，再逐个下载。",
            New JObject From {
                {"target_version", SchemaProp("string", "目标版本文件夹名称")},
                {"keywords", New JObject From {{"type", "array"}, {"items", New JObject From {{"type", "string"}}}, {"description", "要安装的 Mod 关键词数组，最多 10 个"}}}
            }, New String() {"target_version", "keywords"}))
        Tools.Add(ToolDef("backup_instance", "立即对指定版本（留空为当前版本）执行一次配置与存档备份。",
            New JObject From {{"version", SchemaProp("string", "版本文件夹名称，留空表示当前选中的版本")}}, New String() {}))
        Tools.Add(ToolDef("restore_backup", "用 PCL\ModBackup 下的备份目录回滚指定版本（留空为当前版本）的 Mod 与配置。",
            New JObject From {
                {"version", SchemaProp("string", "版本文件夹名称，留空表示当前选中的版本")},
                {"backup", SchemaProp("string", "备份目录名称，可从 list_backups 获取")},
                {"restore_content", SchemaProp("boolean", "是否同时还原配置与存档，默认 true")}
            }, New String() {"backup"}))
        Tools.Add(ToolDef("clean_logs", "清理指定版本（留空为当前版本）的旧 logs 与 crash-reports，保留最近 5 份，其余移动到备份目录而不是删除。",
            New JObject From {{"version", SchemaProp("string", "版本文件夹名称，留空表示当前选中的版本")}}, New String() {}))
        Tools.Add(ToolDef("open_page", "切换到启动器中的指定页面。支持 launch、download、setup、instance、other、link、task。",
            New JObject From {{"page", SchemaProp("string", "页面名：launch、download、setup、instance、other、link、task")}}, New String() {"page"}))
        Return Tools
    End Function

    Private Function SchemaProp(TypeName As String, Description As String) As JObject
        Return New JObject From {{"type", TypeName}, {"description", Description}}
    End Function

    Private Function ToolDef(Name As String, Description As String, Properties As JObject, Required As String()) As JObject
        Dim RequiredArray As New JArray
        For Each Item As String In Required
            RequiredArray.Add(Item)
        Next
        Return New JObject From {
            {"type", "function"},
            {"function", New JObject From {
                {"name", Name},
                {"description", Description},
                {"parameters", New JObject From {
                    {"type", "object"},
                    {"properties", Properties},
                    {"required", RequiredArray}
                }}
            }}
        }
    End Function

#End Region

#Region "设置白名单"

    '可读取的设置项
    Private Function ReadWhitelistSetting(Key As String, ByRef Success As Boolean) As Object
        Success = True
        Select Case Key
            Case "UiHomeAiChat" : Return Settings.Get(Of Boolean)(Key)
            Case "UiLauncherLogo" : Return Settings.Get(Of Boolean)(Key)
            Case "UiLauncherTheme" : Return Settings.Get(Of Integer)(Key)
            Case "LaunchArgumentRam" : Return Settings.Get(Of Boolean)(Key)
            Case "LaunchRamType" : Return Settings.Get(Of Integer)(Key)
            Case "LaunchRamCustom" : Return Settings.Get(Of Integer)(Key)
            Case "LaunchModCheck" : Return Settings.Get(Of Boolean)(Key)
            Case "LaunchModAutoFix" : Return Settings.Get(Of Boolean)(Key)
            Case "LaunchBackup" : Return Settings.Get(Of Boolean)(Key)
            Case "AutoDownloadMissingDeps" : Return Settings.Get(Of Boolean)(Key)
            Case "CrashAiAnalyze" : Return Settings.Get(Of Boolean)(Key)
            Case "KeepCrashReports" : Return Settings.Get(Of Integer)(Key)
            Case "ToolDownloadThread" : Return Settings.Get(Of Integer)(Key)
            Case "ToolDownloadSpeed" : Return Settings.Get(Of Integer)(Key)
            Case "ToolDownloadMod" : Return Settings.Get(Of Integer)(Key)
            Case "ToolDownloadVersion" : Return Settings.Get(Of Integer)(Key)
            Case "UiMusicVolume" : Return Settings.Get(Of Integer)(Key)
            Case "LaunchArgumentWindowWidth" : Return Settings.Get(Of Integer)(Key)
            Case "LaunchArgumentWindowHeight" : Return Settings.Get(Of Integer)(Key)
            Case "AiPlatform" : Return Settings.Get(Of Integer)(Key)
            Case "AiModel" : Return Settings.Get(Of String)(Key)
            Case Else
                Success = False
                Return Nothing
        End Select
    End Function

    '只允许修改有明确数值语义的设置；返回空字符串表示成功
    Private Function SetWhitelistSetting(Key As String, Value As String) As String
        Dim BoolValue As Boolean = False
        Dim IntValue As Integer = 0
        Select Case Key
            Case "UiHomeAiChat", "UiLauncherLogo", "LaunchArgumentRam", "LaunchModCheck", "LaunchModAutoFix", "LaunchBackup", "AutoDownloadMissingDeps", "CrashAiAnalyze"
                If Not ParseBool(Value, BoolValue) Then Return "值必须是 true 或 false"
                Settings.Set(Key, BoolValue)
            Case "LaunchRamType"
                If Not Integer.TryParse(Value, IntValue) OrElse IntValue < 0 OrElse IntValue > 1 Then Return "LaunchRamType 只能是 0（自动）或 1（自定义）"
                Settings.Set(Key, IntValue)
            Case "LaunchRamCustom"
                If Not Integer.TryParse(Value, IntValue) OrElse IntValue < 1 OrElse IntValue > 49 Then Return "LaunchRamCustom 必须在 1 到 49 之间"
                Settings.Set(Key, IntValue)
            Case "KeepCrashReports"
                If Not Integer.TryParse(Value, IntValue) OrElse Not {0, 5, 10, 20}.Contains(IntValue) Then Return "KeepCrashReports 只能是 0、5、10、20"
                Settings.Set(Key, IntValue)
            Case "UiMusicVolume"
                If Not Integer.TryParse(Value, IntValue) OrElse IntValue < 0 OrElse IntValue > 1000 Then Return "UiMusicVolume 必须在 0 到 1000 之间"
                Settings.Set(Key, IntValue)
            Case "LaunchArgumentWindowWidth", "LaunchArgumentWindowHeight"
                If Not Integer.TryParse(Value, IntValue) OrElse IntValue < 1 OrElse IntValue > 99999 Then Return "窗口尺寸必须在 1 到 99999 之间"
                Settings.Set(Key, IntValue)
            Case "ToolDownloadThread"
                If Not Integer.TryParse(Value, IntValue) OrElse IntValue < 1 OrElse IntValue > 256 Then Return "ToolDownloadThread 必须在 1 到 256 之间"
                Settings.Set(Key, IntValue)
            Case "ToolDownloadSpeed"
                If Not Integer.TryParse(Value, IntValue) OrElse IntValue < 1 OrElse IntValue > 100 Then Return "ToolDownloadSpeed 必须在 1 到 100 之间"
                Settings.Set(Key, IntValue)
            Case Else
                Return "不允许修改设置项：" & Key
        End Select
        Return ""
    End Function

    '可在确认前先校验设置键名是否存在，避免对无效键弹出确认框
    Private Function IsWritableSettingKey(Key As String) As Boolean
        Select Case Key
            Case "UiHomeAiChat", "UiLauncherLogo", "LaunchArgumentRam", "LaunchModCheck", "LaunchModAutoFix", "LaunchBackup", "AutoDownloadMissingDeps", "CrashAiAnalyze",
                 "LaunchRamType", "LaunchRamCustom", "KeepCrashReports", "UiMusicVolume",
                 "LaunchArgumentWindowWidth", "LaunchArgumentWindowHeight", "ToolDownloadThread", "ToolDownloadSpeed"
                Return True
            Case Else
                Return False
        End Select
    End Function

    Private Function ParseBool(Text As String, ByRef Value As Boolean) As Boolean
        If Boolean.TryParse(Text, Value) Then Return True
        If Text = "1" Then Value = True : Return True
        If Text = "0" Then Value = False : Return True
        Return False
    End Function

#End Region

#Region "Modrinth 与安装辅助"

    Private Function SearchModrinth(Query As String, Loader As String, GameVersion As String, Limit As Integer) As JArray
        If String.IsNullOrWhiteSpace(Query) Then Return New JArray
        Dim Facets As New JArray
        Facets.Add(New JArray("project_type:mod"))
        If Loader <> "" Then Facets.Add(New JArray("categories:" & Loader))
        If GameVersion <> "" Then Facets.Add(New JArray("versions:" & GameVersion))
        Dim Url As String = "https://api.modrinth.com/v2/search?query=" & Uri.EscapeDataString(Query) &
            "&limit=" & Limit & "&facets=" & Uri.EscapeDataString(Facets.ToString(Formatting.None))
        Dim Token As JToken = DlModRequest(Url)
        Dim Json As JObject = TryCast(Token, JObject)
        If Json Is Nothing Then Return New JArray
        Dim Hits As JArray = TryCast(Json("hits"), JArray)
        Return If(Hits, New JArray)
    End Function

    Private Function GetProjectVersions(ProjectId As String) As JArray
        If ProjectId = "" Then Return New JArray
        Dim Token As JToken = DlModRequest("https://api.modrinth.com/v2/project/" & Uri.EscapeDataString(ProjectId) & "/version?include_changelog=false")
        Dim Arr As JArray = TryCast(Token, JArray)
        Return If(Arr, New JArray)
    End Function

    Private Function FindBestVersion(Versions As JArray, Loader As String, GameVersion As String, VersionHint As String) As JObject
        Dim Candidates As New List(Of JObject)
        For Each Token As JToken In Versions
            Dim Obj As JObject = TryCast(Token, JObject)
            If Obj Is Nothing Then Continue For
            If GameVersion <> "" AndAlso Not ArrayHas(Obj("game_versions"), GameVersion) Then Continue For
            If Loader <> "" AndAlso Not ArrayHas(Obj("loaders"), Loader) Then Continue For
            Candidates.Add(Obj)
        Next
        If Candidates.Count = 0 Then Return Nothing
        If VersionHint <> "" Then
            For Each Obj As JObject In Candidates
                If JStr(Obj, "version_number").Contains(VersionHint) OrElse JStr(Obj, "name").Contains(VersionHint) Then Return Obj
            Next
        End If
        Return Candidates.OrderByDescending(Function(V) JStr(V, "date_published")).First()
    End Function

    Private Function ArrayHas(Token As JToken, Value As String) As Boolean
        If Token Is Nothing OrElse Token.Type <> JTokenType.Array Then Return False
        For Each Item As JToken In CType(Token, JArray)
            If Item.ToString().Equals(Value, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    Private Function GetPrimaryFile(Version As JObject) As JObject
        If Version Is Nothing Then Return Nothing
        Dim Files As JArray = TryCast(Version("files"), JArray)
        If Files Is Nothing OrElse Files.Count = 0 Then Return Nothing
        Dim First As JObject = Nothing
        For Each Token As JToken In Files
            Dim FileObj As JObject = TryCast(Token, JObject)
            If FileObj Is Nothing OrElse JStr(FileObj, "url") = "" Then Continue For
            If First Is Nothing Then First = FileObj
            If JStr(FileObj, "primary").ToLowerInvariant() = "true" Then Return FileObj
        Next
        Return First
    End Function

    Private Function DownloadModFile(FileObj As JObject, ModsFolder As String) As String
        Dim Url As String = JStr(FileObj, "url")
        Dim FileName As String = JStr(FileObj, "filename")
        If FileName = "" Then FileName = PathUtils.GetLastPart(Url)
        If Url = "" OrElse FileName = "" Then Throw New Exception("下载地址或文件名为空")
        DirectoryUtils.Create(ModsFolder)
        Dim LocalPath As String = ModsFolder & FileName
        If Not FileUtils.Exists(LocalPath) Then NetDownloadByLoader(Url, LocalPath)
        Return FileName
    End Function

    ''' <summary>
    ''' 校验下载的 jar 实际加载器是否与实例一致；Fabric 与 Quilt 兼容，Forge 与 NeoForge 不兼容。
    ''' 不匹配时把文件移入 PCL\ModBackup\加载器不符\，返回错误说明；返回空字符串表示通过或无法识别。
    ''' </summary>
    Private Function VerifyDownloadedModLoader(Inst As McInstance, FilePath As String, ExpectedLoader As String, ByRef ActualLoader As String) As String
        ActualLoader = ""
        If Not FileUtils.Exists(FilePath) Then Return "下载后的文件不存在：" & Path.GetFileName(FilePath)
        Dim Info As ModChecker.ModInfo = ModChecker.ReadInfo(FilePath)
        Dim Family As String = ""
        If Info IsNot Nothing Then
            Select Case Info.Loader.ToLowerInvariant()
                Case "fabric", "quilt"
                    Family = "fabric"
                    ActualLoader = Info.Loader
                Case "forge"
                    Family = "forge"
                    ActualLoader = "Forge"
            End Select
        End If
        'Forge 与 NeoForge 的元数据都可能是 mods.toml，再用 jar 内的描述文件细分
        If Family = "forge" AndAlso JarLoaderFamily(FilePath) = "neoforge" Then
            Family = "neoforge"
            ActualLoader = "NeoForge"
        End If
        If Family = "" Then Family = JarLoaderFamily(FilePath)
        If Family = "" Then
            '无法识别的 jar 不强行拦截，避免误伤
            ActualLoader = "未知"
            Return ""
        End If
        If ActualLoader = "" Then ActualLoader = LoaderFamilyName(Family)
        Dim Expected As String = NormalizeLoaderFamily(ExpectedLoader)
        If Expected = "" OrElse IsLoaderFamilyCompatible(Family, Expected) Then Return ""
        Dim BackupFolder As String = Inst.PathIndie & "PCL\ModBackup\加载器不符\"
        Dim MoveText As String
        Try
            DirectoryUtils.Create(BackupFolder)
            FileUtils.Move(FilePath, BackupFolder & Path.GetFileName(FilePath))
            MoveText = "已移出 mods 到 " & BackupFolder
        Catch ex As Exception
            Logger.Warn(ex, "移动加载器不符的 Mod 失败")
            MoveText = "移出 mods 失败（" & ex.Message & "），请手动删除"
        End Try
        Return "加载器不匹配：实例 " & Inst.Name & " 为 " & LoaderFamilyName(Expected) & "，jar 实际为 " & ActualLoader & "；" & MoveText
    End Function

    '读取 jar 内的加载器描述文件，区分 Fabric/Quilt、Forge 与 NeoForge
    Private Function JarLoaderFamily(FilePath As String) As String
        Try
            Using Zip As ZipArchive = ZipFile.OpenRead(FilePath)
                If Zip.GetEntry("fabric.mod.json") IsNot Nothing Then Return "fabric"
                If Zip.GetEntry("quilt.mod.json") IsNot Nothing Then Return "fabric"
                If Zip.GetEntry("META-INF/neoforge.mods.toml") IsNot Nothing Then Return "neoforge"
                Dim ForgeEntry As ZipArchiveEntry = Zip.GetEntry("META-INF/mods.toml")
                If ForgeEntry IsNot Nothing Then
                    Using S As IO.Stream = ForgeEntry.Open()
                        Using Reader As New IO.StreamReader(S, Encoding.UTF8)
                            If Reader.ReadToEnd().IndexOf("neoforge", StringComparison.OrdinalIgnoreCase) >= 0 Then Return "neoforge"
                        End Using
                    End Using
                    Return "forge"
                End If
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"读取 jar 加载器信息失败（{Path.GetFileName(FilePath)}）")
        End Try
        Return ""
    End Function

    '把 Fabric/Quilt、Forge、NeoForge 归一为家族名
    Private Function NormalizeLoaderFamily(Loader As String) As String
        Select Case Loader.Trim().ToLowerInvariant()
            Case "fabric", "quilt" : Return "fabric"
            Case "forge" : Return "forge"
            Case "neoforge" : Return "neoforge"
            Case Else : Return ""
        End Select
    End Function

    Private Function IsLoaderFamilyCompatible(JarFamily As String, InstanceFamily As String) As Boolean
        Select Case JarFamily
            Case "fabric" : Return InstanceFamily = "fabric"
            Case "forge" : Return InstanceFamily = "forge"
            Case "neoforge" : Return InstanceFamily = "neoforge"
            Case Else : Return True
        End Select
    End Function

    Private Function LoaderFamilyName(Family As String) As String
        Select Case Family
            Case "fabric" : Return "Fabric/Quilt"
            Case "forge" : Return "Forge"
            Case "neoforge" : Return "NeoForge"
            Case Else : Return "未知"
        End Select
    End Function

    '把可选的加载器版本号列成一段文字，用于错误提示
    Private Function VersionChoicesText(Names As IEnumerable(Of String)) As String
        Dim List As List(Of String) = Names.Where(Function(N) N <> "").ToList()
        If List.Count = 0 Then Return "（未获取到版本列表）"
        Dim Shown As String = String.Join("、", List.Take(6))
        If List.Count > 6 Then Shown &= " 等共 " & List.Count & " 个"
        Return Shown
    End Function

    '复用 PCL 的官方安装参数获取 Fabric、Forge、NeoForge 版本
    Private Function GetLatestFabricLoader() As String
        Try
            Dim Json As JToken = NetRequestByClientRetry("https://meta.fabricmc.net/v2/versions/loader", RequireJson:=True).DeserializeJson()
            If Json Is Nothing Then Return ""
            Dim Arr As JArray = TryCast(Json, JArray)
            If Arr Is Nothing Then
                '部分镜像返回 {loader: [...]}
                Dim Obj As JObject = TryCast(Json, JObject)
                If Obj IsNot Nothing Then Arr = TryCast(Obj("loader"), JArray)
            End If
            If Arr Is Nothing Then Return ""
            For Each Token As JToken In Arr
                Dim Obj As JObject = TryCast(Token, JObject)
                If Obj Is Nothing Then Continue For
                If JStr(Obj, "stable").ToLowerInvariant() = "true" Then Return JStr(Obj, "version")
            Next
            Dim FirstObj As JObject = TryCast(Arr.FirstOrDefault(), JObject)
            Return If(FirstObj Is Nothing, "", JStr(FirstObj, "version"))
        Catch ex As Exception
            Logger.Warn(ex, "获取 Fabric 加载器版本失败")
            Return ""
        End Try
    End Function

    Private Function GetForgeVersions(GameVersion As String) As List(Of DlForgeVersionEntry)
        Try
            Dim Loader As New LoaderTask(Of String, List(Of DlForgeVersionEntry))("AI 获取 Forge 版本", AddressOf DlForgeVersionMain) With {.Show = False}
            Loader.WaitForExit(GameVersion)
            Return If(Loader.Output, New List(Of DlForgeVersionEntry))
        Catch ex As Exception
            Logger.Warn(ex, "获取 Forge 版本列表失败")
            Return New List(Of DlForgeVersionEntry)
        End Try
    End Function

    Private Function GetNeoForgeVersions(GameVersion As String) As List(Of DlNeoForgeListEntry)
        Try
            DlNeoForgeListLoader.WaitForExit(0)
            Return DlNeoForgeListLoader.Output.Value.Where(Function(E) E.Inherit = GameVersion).ToList()
        Catch ex As Exception
            Logger.Warn(ex, "获取 NeoForge 版本列表失败")
            Return New List(Of DlNeoForgeListEntry)
        End Try
    End Function

    Private Function GetUnusedInstanceName(BaseName As String) As String
        Dim Name As String = BaseName
        Dim Index As Integer = 2
        While FileUtils.Exists(McFolderSelected & "versions\" & Name & "\" & Name & ".json")
            Name = BaseName & "-" & Index
            Index += 1
        End While
        Return Name
    End Function

#End Region

#Region "通用辅助"

    '按名称或当前选中版本解析实例
    Private Function ResolveInstance(VersionName As String) As McInstance
        If VersionName = "" Then Return McInstanceSelected
        If McInstanceSelected IsNot Nothing AndAlso McInstanceSelected.Name.Equals(VersionName, StringComparison.OrdinalIgnoreCase) Then
            Return McInstanceSelected
        End If
        For Each Inst As McInstance In GetAllInstances()
            If Inst.Name.Equals(VersionName, StringComparison.OrdinalIgnoreCase) Then Return Inst
        Next
        '按版本文件夹名再兜底一次
        Dim Path As String = McFolderSelected & "versions\" & VersionName & "\"
        If DirectoryUtils.Exists(Path) Then Return New McInstance(VersionName).Load()
        Return Nothing
    End Function

    '找不到实例时的统一错误说明：附带可用实例列表，避免 AI 拿当前选中实例顶替
    Private Function InstanceMissingMessage(VersionName As String) As String
        If VersionName = "" Then
            Return "当前没有选中的 Minecraft 版本，请先选择一个版本，或显式传入 version 参数。" & AvailableInstancesHint()
        End If
        Return "找不到版本「" & VersionName & "」。" & AvailableInstancesHint()
    End Function

    Private Function AvailableInstancesHint() As String
        Dim Names As New List(Of String)
        For Each Inst As McInstance In GetAllInstances()
            If Inst.Name <> "" Then Names.Add(Inst.Name)
        Next
        If Names.Count = 0 Then Return "当前没有任何已安装的版本。"
        Return "可用版本：" & String.Join("、", Names.Take(20)) & If(Names.Count > 20, " 等共 " & Names.Count & " 个", "") & "。"
    End Function

    Private Function GetAllInstances() As List(Of McInstance)
        Dim Result As New List(Of McInstance)
        If McInstanceList IsNot Nothing AndAlso McInstanceList.Count > 0 Then
            For Each Pair As KeyValuePair(Of McInstanceCardType, List(Of McInstance)) In McInstanceList
                For Each Inst As McInstance In Pair.Value
                    If Inst IsNot Nothing AndAlso Inst.Name <> "" Then Result.Add(Inst)
                Next
            Next
            If Result.Count > 0 Then Return Result
        End If
        '版本列表尚未加载时的兜底
        For Each Folder As String In DirectoryUtils.EnumerateDirectories(McFolderSelected & "versions\")
            Try
                Dim Inst As McInstance = New McInstance(Folder).Load()
                If Inst IsNot Nothing AndAlso Inst.Name <> "" Then Result.Add(Inst)
            Catch ex As Exception
                Logger.Warn(ex, "读取版本失败：" & Folder)
            End Try
        Next
        Return Result
    End Function

    Private Function GameVersionOf(Inst As McInstance) As String
        Try
            Dim GameVersion As String = Inst.Version.VanillaName
            If GameVersion = "Unknown" Then Return ""
            Return GameVersion
        Catch ex As Exception
            Return ""
        End Try
    End Function

    '推断实例的 Modrinth 加载器与游戏版本
    Private Sub GetModEnv(Inst As McInstance, ByRef Loader As String, ByRef GameVersion As String)
        Loader = ""
        GameVersion = ""
        If Inst Is Nothing Then Return
        GameVersion = GameVersionOf(Inst)
        Try
            If Inst.Version.ModLoaders.HasFlagF(ModLoaders.Fabric) Then
                Loader = "fabric"
            ElseIf Inst.Version.ModLoaders.HasFlagF(ModLoaders.NeoForge) Then
                Loader = "neoforge"
            ElseIf Inst.Version.ModLoaders.HasFlagF(ModLoaders.Forge) Then
                Loader = "forge"
            End If
        Catch ex As Exception
            Logger.Warn(ex, "推断实例加载器失败")
        End Try
    End Sub

    '移动旧文件，保留最近若干份
    Private Function MoveOldFiles(SourceFolder As String, TargetFolder As String, Keep As Integer) As Integer
        If Not DirectoryUtils.Exists(SourceFolder) Then Return 0
        Dim Files As List(Of String) = DirectoryUtils.EnumerateFiles(SourceFolder, False).ToList()
        If Files.Count <= Keep Then Return 0
        Dim Moved As Integer = 0
        For Each FilePath As String In Files.OrderByDescending(Function(F) FileUtils.GetInfo(F).LastWriteTime).Skip(Keep)
            Try
                FileUtils.Move(FilePath, TargetFolder & Path.GetFileName(FilePath))
                Moved += 1
            Catch ex As Exception
                Logger.Warn(ex, "移动旧日志失败：" & FilePath)
            End Try
        Next
        Return Moved
    End Function

    'AI 操作权限：0 手动审批（所有工具都确认）、1 自动审批（仅改动类确认）、2 完全访问（不确认）
    Private Function GetPermissionLevel() As Integer
        Return Settings.Get(Of Integer)("AiChatPermission")
    End Function

    '这些工具只读，不会改动任何文件或游戏状态
    Private Function IsReadOnlyTool(Name As String) As Boolean
        Select Case Name
            Case "get_instance_info", "list_versions", "launcher_status", "list_mods", "get_setting", "search_mods",
                 "check_mod_updates", "mod_selfcheck", "read_latest_crash", "list_backups"
                Return True
            Case Else
                Return False
        End Select
    End Function

    Private Function AskConfirm(Description As String) As Boolean
        '完全访问模式：不弹确认，直接执行
        If GetPermissionLevel() >= 2 Then Return True
        Dim Result As Integer = RunInUiWait(Function() MyMsgBox("即将执行：" & Description, "AI 助手", "执行", "取消"))
        Return Result = 1
    End Function

    Private Function RejectedWith(Append As Action(Of String, AiChatRole)) As String
        Append("✗ 已取消：用户拒绝了该操作", AiChatRole.Tool)
        Return ResultJson(False, "用户拒绝了该操作")
    End Function

    Private Function FailWith(Append As Action(Of String, AiChatRole), ActionName As String, Message As String) As String
        Append("✗ 失败：" & ActionName & "：" & Message, AiChatRole.Tool)
        Return ResultJson(False, Message)
    End Function

    Private Function ResultJson(Success As Boolean, Message As String) As String
        Return New JObject From {{"success", Success}, {"message", Message}}.ToString(Formatting.None)
    End Function

    Private Function JStr(Json As JObject, Key As String) As String
        If Json Is Nothing Then Return ""
        Dim Token As JToken = Json(Key)
        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return ""
        Return Token.ToString().Trim()
    End Function

    Private Function JInt(Json As JObject, Key As String, DefaultValue As Integer) As Integer
        Dim Text As String = JStr(Json, Key)
        Dim Value As Integer = 0
        If Integer.TryParse(Text, Value) Then Return Value
        Return DefaultValue
    End Function

    Private Function ArgStr(Args As JObject, Key As String) As String
        Return JStr(Args, Key)
    End Function

    Private Function ArgInt(Args As JObject, Key As String, DefaultValue As Integer) As Integer
        Return JInt(Args, Key, DefaultValue)
    End Function

    Private Function ArgBool(Args As JObject, Key As String, DefaultValue As Boolean) As Boolean
        Dim Text As String = JStr(Args, Key)
        If Text = "" Then Return DefaultValue
        Dim Value As Boolean = DefaultValue
        If ParseBool(Text, Value) Then Return Value
        Return DefaultValue
    End Function

#End Region

End Module
