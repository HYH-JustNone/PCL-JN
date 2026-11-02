''' <summary>
''' PCL-JN：通过 Modrinth 公共 API 下载缺少的前置，并检查已安装 Mod 的更新。
''' 不需要 API Key，但需要联网；所有网络异常都只会写入日志，不向界面抛错。
''' </summary>
Public Module ModUpdate

    Private Const ModrinthApi As String = "https://api.modrinth.com/v2/"

    ''' <summary>当前版本对应的 Modrinth 加载器与游戏版本。</summary>
    Public Class ModEnv
        Public Loader As String = ""
        Public GameVersion As String = ""
    End Class

    ''' <summary>
    ''' 启动自检发现缺少前置时调用。同一批前置只会询问一次，结果记在 PCL\ModBackup\dep-asked.txt。
    ''' </summary>
    Public Sub AskAndDownloadMissing(PathIndie As String, Missing As List(Of String))
        Try
            If Missing Is Nothing OrElse Missing.Count = 0 Then Return
            Dim AskedFile As String = PathIndie & "PCL\ModBackup\dep-asked.txt"
            Dim Asked As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            If FileUtils.Exists(AskedFile) Then
                For Each Line As String In FileUtils.ReadAsLines(AskedFile)
                    If Line.Trim() <> "" Then Asked.Add(Line.Trim())
                Next
            End If
            Dim NewOnes As New List(Of String)
            For Each Dep As String In Missing
                If Not Asked.Contains(Dep) Then
                    NewOnes.Add(Dep)
                    Asked.Add(Dep)
                End If
            Next
            If NewOnes.Count = 0 Then Return
            '先记录已询问，避免每次启动重复弹窗
            DirectoryUtils.Create(PathIndie & "PCL\ModBackup\")
            FileUtils.Write(AskedFile, String.Join(vbCrLf, Asked.ToList()))

            Dim Result As Integer = MyMsgBox($"Mod 自检发现缺少前置：{vbCrLf}- {String.Join(vbCrLf & "- ", NewOnes)}{vbCrLf}{vbCrLf}是否尝试从 Modrinth 自动下载？",
                "缺少前置", "自动下载", "跳过")
            If Result <> 1 Then Return

            Dim Env As ModEnv = GetTargetEnv()
            If Env Is Nothing Then
                Logger.Warn("[ModUpdate] 无法确定加载器或游戏版本，跳过缺少前置的自动下载")
                Hint("无法确定当前版本的加载器或游戏版本，已跳过自动下载缺少的前置", HintType.Red)
                Return
            End If

            Dim Success As Integer = 0
            Dim Failed As New List(Of String)
            For Each Dep As String In NewOnes
                Try
                    Dim FileName As String = DownloadDependency(PathIndie, Dep, Env)
                    If FileName = "" Then Failed.Add(Dep) Else Success += 1
                Catch ex As Exception
                    Logger.Warn(ex, $"[ModUpdate] 下载缺少的前置失败：{Dep}")
                    Failed.Add(Dep)
                End Try
            Next
            If Success > 0 Then Hint($"已自动下载 {Success} 个缺少的前置", HintType.Green, True)
            If Failed.Count > 0 Then Hint($"有 {Failed.Count} 个前置未能自动下载（{String.Join("、", Failed)}），请手动下载", HintType.Red, True)
        Catch ex As Exception
            Logger.Warn(ex, "[ModUpdate] 处理缺少前置时出错")
        End Try
    End Sub

    ''' <summary>
    ''' 检查 mods 中可识别的 Mod 是否有更新，返回给用户看的报告。需要联网，请勿在 UI 线程调用。
    ''' </summary>
    Public Function CheckUpdates(PathIndie As String) As String
        Try
            If McInstanceSelected Is Nothing Then Return "请先选择一个 Minecraft 版本。"
            Dim Env As ModEnv = GetTargetEnv()
            If Env Is Nothing Then Return "无法确定当前版本的加载器或游戏版本，暂时无法检查 Mod 更新。"

            Dim Mods As New List(Of ModChecker.ModInfo)
            For Each FilePath As String In DirectoryUtils.EnumerateFiles(PathIndie & "mods\", False, "*.jar")
                Dim Info As ModChecker.ModInfo = ModChecker.ReadInfo(FilePath)
                If Info IsNot Nothing AndAlso Info.Id <> "" AndAlso Info.Version <> "" Then Mods.Add(Info)
            Next
            If Mods.Count = 0 Then Return "mods 文件夹里没有找到可以检查更新的 Mod。"

            Dim Updates As New List(Of String)
            Dim Checked As Integer = 0
            For Each M As ModChecker.ModInfo In Mods
                Try
                    Dim ProjectId As String = FindProjectId(M.Id)
                    If ProjectId = "" Then Continue For
                    Dim Latest As JObject = GetLatestVersion(ProjectId, Env)
                    If Latest Is Nothing Then Continue For
                    Checked += 1
                    Dim NewVersion As String = Str(Latest, "version_number")
                    If NewVersion = "" Then NewVersion = Str(Latest, "name")
                    If CompareVersion(NewVersion, M.Version) > 0 Then Updates.Add($"{ShowModName(M)}：{M.Version} → {NewVersion}")
                Catch ex As Exception
                    Logger.Warn(ex, $"[ModUpdate] 检查 Mod 更新失败：{M.Id}")
                End Try
                '串行请求，并留出间隔，避免触发 Modrinth 的频率限制
                Thread.Sleep(400)
            Next

            If Updates.Count = 0 Then Return $"已检查 {Checked} 个 Mod，暂未发现可更新的版本。"
            Return $"共检查 {Checked} 个 Mod，发现 {Updates.Count} 个有更新：{vbCrLf}- {String.Join(vbCrLf & "- ", Updates)}"
        Catch ex As Exception
            Logger.Warn(ex, "[ModUpdate] 检查 Mod 更新时出错")
            Return "检查 Mod 更新时出错，详见日志。"
        End Try
    End Function

#Region "内部实现"

    '从依赖 id 下载对应前置，返回下载的文件名；失败时返回空字符串
    Private Function DownloadDependency(PathIndie As String, Dep As String, Env As ModEnv) As String
        Dim ProjectId As String = FindProjectId(Dep)
        If ProjectId = "" Then
            Logger.Info($"[ModUpdate] 在 Modrinth 上找不到前置 {Dep}，跳过")
            Return ""
        End If
        Dim Latest As JObject = GetLatestVersion(ProjectId, Env)
        If Latest Is Nothing Then
            Logger.Info($"[ModUpdate] 前置 {Dep} 没有适配 {Env.Loader} / {Env.GameVersion} 的版本，跳过")
            Return ""
        End If
        '优先取 primary 文件
        Dim Chosen As JObject = Nothing
        Dim FilesToken As JToken = Latest("files")
        If FilesToken IsNot Nothing AndAlso FilesToken.Type = JTokenType.Array Then
            For Each Token As JToken In CType(FilesToken, JArray)
                Dim FileObj As JObject = TryCast(Token, JObject)
                If FileObj Is Nothing OrElse Str(FileObj, "url") = "" Then Continue For
                If Chosen Is Nothing Then Chosen = FileObj
                If Str(FileObj, "primary").ToLowerInvariant() = "true" Then
                    Chosen = FileObj
                    Exit For
                End If
            Next
        End If
        If Chosen Is Nothing Then Return ""
        Dim FileName As String = Str(Chosen, "filename")
        If FileName = "" Then FileName = PathUtils.GetLastPart(Str(Chosen, "url"))
        DirectoryUtils.Create(PathIndie & "mods\")
        Dim LocalPath As String = PathIndie & "mods\" & FileName
        If FileUtils.Exists(LocalPath) Then
            Logger.Info($"[ModUpdate] 前置文件已存在，跳过下载：{FileName}")
            Return FileName
        End If
        NetDownloadByLoader(Str(Chosen, "url"), LocalPath)
        Logger.Info($"[ModUpdate] 已下载前置 {Dep}：{FileName}")
        Return FileName
    End Function

    '先当作项目 slug 直接查询，失败后再走搜索
    Private Function FindProjectId(Id As String) As String
        If Id = "" Then Return ""
        Try
            Dim ProjectText As String = NetRequestByClientRetry(ModrinthApi & "project/" & Uri.EscapeDataString(Id), RequireJson:=True)
            Dim ProjectId As String = Str(JObject.Parse(ProjectText), "id")
            If ProjectId <> "" Then Return ProjectId
        Catch ex As Exception
            Logger.Info($"[ModUpdate] {Id} 不是有效的 Modrinth 项目（{ex.Message}），改用搜索")
        End Try
        Return SearchProject(Id)
    End Function

    Private Function SearchProject(Query As String) As String
        Try
            Dim Facets As String = "[[" & Chr(34) & "project_type:mod" & Chr(34) & "]]"
            Dim Url As String = ModrinthApi & "search?query=" & Uri.EscapeDataString(Query) & "&limit=5&facets=" & Uri.EscapeDataString(Facets)
            Dim Text As String = NetRequestByClientRetry(Url, RequireJson:=True)
            Dim Json As JObject = JObject.Parse(Text)
            Dim Hits As JArray = TryCast(Json("hits"), JArray)
            If Hits Is Nothing OrElse Hits.Count = 0 Then Return ""
            Dim First As JObject = CType(Hits(0), JObject)
            Dim ProjectId As String = Str(First, "project_id")
            If ProjectId = "" Then ProjectId = Str(First, "slug")
            Return ProjectId
        Catch ex As Exception
            Logger.Warn(ex, $"[ModUpdate] 搜索 Modrinth 项目失败：{Query}")
            Return ""
        End Try
    End Function

    '取同加载器、同游戏版本的最新版本；没有匹配时返回 Nothing
    Private Function GetLatestVersion(ProjectId As String, Env As ModEnv) As JObject
        Try
            Dim LoadersJson As String = "[" & Chr(34) & Env.Loader & Chr(34) & "]"
            Dim VersionsJson As String = "[" & Chr(34) & Env.GameVersion & Chr(34) & "]"
            Dim Url As String = ModrinthApi & "project/" & Uri.EscapeDataString(ProjectId) & "/version?loaders=" &
                Uri.EscapeDataString(LoadersJson) & "&game_versions=" & Uri.EscapeDataString(VersionsJson)
            Dim Text As String = NetRequestByClientRetry(Url, RequireJson:=True)
            Dim Versions As JArray = JArray.Parse(Text)
            If Versions.Count = 0 Then Return Nothing
            Dim Latest As JObject = Nothing
            For Each Token As JToken In Versions
                Dim Obj As JObject = TryCast(Token, JObject)
                If Obj Is Nothing Then Continue For
                If Latest Is Nothing Then
                    Latest = Obj
                    Continue For
                End If
                '按发布时间取最新
                If Str(Obj, "date_published") > Str(Latest, "date_published") Then Latest = Obj
            Next
            Return Latest
        Catch ex As Exception
            Logger.Warn(ex, $"[ModUpdate] 获取 Modrinth 版本列表失败：{ProjectId}")
            Return Nothing
        End Try
    End Function

    '从当前选择的版本推断 Modrinth 的加载器与游戏版本，实在拿不到时返回 Nothing
    Private Function GetTargetEnv() As ModEnv
        Try
            If McInstanceSelected Is Nothing Then Return Nothing
            Dim Inst As McInstance = McInstanceSelected
            Dim GameVersion As String = Inst.Version.VanillaName
            If GameVersion = "Unknown" Then GameVersion = ""
            If GameVersion = "" Then
                '兜底：从版本 JSON 的 id 或继承版本名中提取
                Try
                    Dim Id As String = If(Inst.JsonObject("id"), "").ToString
                    If Id = "" Then Id = Inst.InheritName
                    Dim Matches As System.Text.RegularExpressions.Match = System.Text.RegularExpressions.Regex.Match(Id, "\d+\.\d+(\.\d+)?")
                    If Matches.Success Then GameVersion = Matches.Value
                Catch ex As Exception
                    Logger.Warn(ex, "[ModUpdate] 无法从版本 JSON 读取游戏版本")
                End Try
            End If
            If GameVersion = "" Then
                Logger.Warn("[ModUpdate] 无法确定游戏版本，跳过联网操作")
                Return Nothing
            End If
            Dim Env As New ModEnv With {.GameVersion = GameVersion}
            Dim Flags As ModLoaders = Inst.Version.ModLoaders
            If Flags.HasFlagF(ModLoaders.Fabric) Then
                Env.Loader = "fabric"
            ElseIf Flags.HasFlagF(ModLoaders.NeoForge) Then
                Env.Loader = "neoforge"
            ElseIf Flags.HasFlagF(ModLoaders.Forge) Then
                Env.Loader = "forge"
            ElseIf Flags.HasFlagF(ModLoaders.LiteLoader) Then
                Env.Loader = "liteloader"
            End If
            If Env.Loader = "" Then
                '兜底：根据 mods 里已有 Mod 的加载器判断
                For Each FilePath As String In DirectoryUtils.EnumerateFiles(Inst.PathIndie & "mods\", False, "*.jar")
                    Dim Info As ModChecker.ModInfo = ModChecker.ReadInfo(FilePath)
                    If Info Is Nothing Then Continue For
                    Select Case Info.Loader
                        Case "Fabric"
                            Env.Loader = "fabric"
                            Exit For
                        Case "Quilt"
                            Env.Loader = "quilt"
                            Exit For
                        Case "Forge"
                            Env.Loader = "forge"
                            Exit For
                    End Select
                Next
            End If
            If Env.Loader = "" Then
                Logger.Warn("[ModUpdate] 无法确定 Mod 加载器，跳过联网操作")
                Return Nothing
            End If
            Return Env
        Catch ex As Exception
            Logger.Warn(ex, "[ModUpdate] 推断加载器与游戏版本失败")
            Return Nothing
        End Try
    End Function

    Private Function ShowModName(Info As ModChecker.ModInfo) As String
        If Info.Name = "" Then Return Path.GetFileNameWithoutExtension(Info.FilePath)
        Return Info.Name
    End Function

    '只比较数字段，够用且不会因为后缀字母误判
    Private Function CompareVersion(Left As String, Right As String) As Integer
        Dim LeftParts As Double() = GetVersionNumbers(Left)
        Dim RightParts As Double() = GetVersionNumbers(Right)
        For i As Integer = 0 To Math.Max(LeftParts.Length, RightParts.Length) - 1
            Dim A As Double = If(i < LeftParts.Length, LeftParts(i), 0)
            Dim B As Double = If(i < RightParts.Length, RightParts(i), 0)
            If A <> B Then Return If(A > B, 1, -1)
        Next
        Return 0
    End Function

    Private Function GetVersionNumbers(Version As String) As Double()
        Dim Parts As New List(Of Double)
        For Each Segment As String In System.Text.RegularExpressions.Regex.Split(Version, "[^0-9]+")
            If Segment = "" Then Continue For
            Parts.Add(Val(Segment))
        Next
        Return Parts.ToArray()
    End Function

    Private Function Str(Json As JObject, Key As String) As String
        Dim Token As JToken = Json(Key)
        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return ""
        Return Token.ToString()
    End Function

#End Region

End Module
