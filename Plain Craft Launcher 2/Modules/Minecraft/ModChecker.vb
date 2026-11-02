''' <summary>
''' PCL-JN：启动前的 Mod 自检与自动修复。
''' 检测重复 Mod、缺少前置、加载器混装，并在改动文件前先把它们移动到备份目录。
''' </summary>
Public Module ModChecker

    Public Class ModInfo
        Public FilePath As String = ""
        Public Id As String = ""
        Public Name As String = ""
        Public Version As String = ""
        Public Loader As String = ""
        Public Depends As New List(Of String)
        Public Breaks As New List(Of String)
    End Class

    '读取 Mod 元信息
    Private Function ReadModInfo(FilePath As String) As ModInfo
        Try
            Using Zip As ZipArchive = ZipFile.OpenRead(FilePath)
                Dim Entry As ZipArchiveEntry = Zip.GetEntry("fabric.mod.json")
                If Entry IsNot Nothing Then Return ParseFabric(FilePath, Entry)
                Entry = Zip.GetEntry("quilt.mod.json")
                If Entry IsNot Nothing Then Return ParseQuilt(FilePath, Entry)
                Entry = Zip.GetEntry("META-INF/mods.toml")
                If Entry IsNot Nothing Then Return ParseForge(FilePath, Entry)
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"读取 Mod 信息失败（{Path.GetFileName(FilePath)}）")
        End Try
        Return Nothing
    End Function
    ''' <summary>
    ''' 读取单个 Mod 的元信息，供更新检测等功能复用。无法读取时返回 Nothing。
    ''' </summary>
    Public Function ReadInfo(FilePath As String) As ModInfo
        Return ReadModInfo(FilePath)
    End Function
    Private Function ReadEntry(Entry As ZipArchiveEntry) As String
        Using S As IO.Stream = Entry.Open()
            Using Reader As New IO.StreamReader(S, System.Text.Encoding.UTF8)
                Return Reader.ReadToEnd()
            End Using
        End Using
    End Function
    Private Function Str(Json As JObject, Key As String) As String
        Dim Token As JToken = Json(Key)
        If Token Is Nothing OrElse Token.Type = JTokenType.Null Then Return ""
        Return Token.ToString()
    End Function
    Private Sub FillNames(Token As JToken, Target As List(Of String))
        If Token Is Nothing OrElse Token.Type <> JTokenType.Object Then Return
        For Each P As JProperty In CType(Token, JObject).Properties
            Target.Add(P.Name)
        Next
    End Sub

    'Fabric / Quilt
    Private Function ParseFabric(FilePath As String, Entry As ZipArchiveEntry) As ModInfo
        Dim Json As JObject = JObject.Parse(ReadEntry(Entry))
        Dim Info As New ModInfo With {.FilePath = FilePath, .Loader = "Fabric", .Id = Str(Json, "id"), .Name = Str(Json, "name"), .Version = Str(Json, "version")}
        FillNames(Json("depends"), Info.Depends)
        FillNames(Json("breaks"), Info.Breaks)
        Return Info
    End Function
    Private Function ParseQuilt(FilePath As String, Entry As ZipArchiveEntry) As ModInfo
        Dim Json As JObject = JObject.Parse(ReadEntry(Entry))
        Dim Loader As JObject = TryCast(Json("quilt_loader"), JObject)
        If Loader Is Nothing Then Return Nothing
        Dim Meta As JObject = TryCast(Loader("metadata"), JObject)
        Dim Info As New ModInfo With {.FilePath = FilePath, .Loader = "Quilt", .Id = Str(Loader, "id"), .Version = Str(Loader, "version")}
        If Meta IsNot Nothing Then Info.Name = Str(Meta, "name")
        Dim Depends As JToken = Loader("depends")
        If Depends IsNot Nothing AndAlso Depends.Type = JTokenType.Array Then
            For Each Item As JToken In CType(Depends, JArray)
                Dim DepId As String = Str(CType(Item, JObject), "id")
                If DepId <> "" Then Info.Depends.Add(DepId)
            Next
        End If
        Return Info
    End Function

    'Forge / NeoForge（只做够用的 toml 行解析，不引入完整 TOML 库）
    Private Function ParseForge(FilePath As String, Entry As ZipArchiveEntry) As ModInfo
        Dim Info As New ModInfo With {.FilePath = FilePath, .Loader = "Forge"}
        Dim Section As String = ""
        Dim DepId As String = ""
        For Each RawLine As String In ReadEntry(Entry).Replace(vbCrLf, vbLf).Split(CChar(vbLf))
            Dim Line As String = RawLine.Trim()
            If Line = "" OrElse Line.StartsWith("#") Then Continue For
            If Line.StartsWith("[[") OrElse Line.StartsWith("[") Then
                Section = Line
                If Section.StartsWith("[[dependencies") Then DepId = ""
                Continue For
            End If
            Dim Pos As Integer = Line.IndexOf("=")
            If Pos < 0 Then Continue For
            Dim Key As String = Line.Substring(0, Pos).Trim().ToLowerInvariant()
            Dim Value As String = Line.Substring(Pos + 1).Trim().Trim(CChar(Chr(34)))
            Select Case Section
                Case "[[mods]]"
                    Select Case Key
                        Case "modid" : Info.Id = Value
                        Case "displayname" : Info.Name = Value
                        Case "version" : Info.Version = Value
                    End Select
                Case Else
                    If Section.StartsWith("[[dependencies") Then
                        Select Case Key
                            Case "modid" : DepId = Value
                            Case "mandatory"
                                If Value.ToLowerInvariant() = "true" AndAlso DepId <> "" Then Info.Depends.Add(DepId)
                        End Select
                    End If
            End Select
        Next
        Return Info
    End Function

    '加载器自身或原版，不需要单独下载
    Private ReadOnly LoaderDependencies As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "minecraft", "forge", "neoforge", "fabric", "fabricloader", "fabric-api", "java", "quilt_loader", "liteloader", "rift"
    }

    Private Function ShowName(Info As ModInfo) As String
        If Info.Name = "" Then Return Path.GetFileNameWithoutExtension(Info.FilePath)
        Return Info.Name
    End Function

    ''' <summary>
    ''' 执行一次自检，返回问题报告；一切正常时返回 Nothing。
    ''' 会自动隔离重复 Mod、清理堆积的崩溃报告，这些文件都会被移动到备份目录而不是删除。
    ''' KeepReports 指定旧崩溃报告的保留数量，为 0 时不清理。
    ''' </summary>
    Public Function RunCheck(PathIndie As String, Optional AutoFix As Boolean = True, Optional KeepReports As Integer = 5, Optional ByRef MissingDeps As List(Of String) = Nothing) As String
        Try
            Dim Mods As New List(Of ModInfo)
            For Each FilePath As String In DirectoryUtils.EnumerateFiles(PathIndie & "mods\", False, "*.jar")
                Dim Info As ModInfo = ReadModInfo(FilePath)
                If Info IsNot Nothing AndAlso Info.Id <> "" Then Mods.Add(Info)
            Next

            Dim Issues As New List(Of String)
            Dim Removed As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim Stamp As String = DateTime.Now.ToString("yyyyMMdd-HHmmss")
            Dim BackupFolder As String = PathIndie & "PCL\ModBackup\" & Stamp & "\"

            '1. 同一个 modid 装了多个版本：保留最新的，其余移动到备份目录
            For Each Group In Mods.GroupBy(Function(M) M.Id.ToLowerInvariant())
                If Group.Count() <= 1 Then Continue For
                Dim Keep As ModInfo = Group.OrderByDescending(Function(M) New FileInfo(M.FilePath).LastWriteTimeUtc).First()
                For Each M As ModInfo In Group
                    If M Is Keep Then Continue For
                    Issues.Add($"重复 Mod「{ShowName(M)}」：装入了多个版本（{Path.GetFileName(M.FilePath)} 与 {Path.GetFileName(Keep.FilePath)}）")
                    If Not AutoFix Then Continue For
                    Try
                        FileUtils.Move(M.FilePath, BackupFolder & "mods\" & Path.GetFileName(M.FilePath))
                        Removed.Add(M.FilePath)
                        Logger.Info($"[ModChecker] 已隔离重复 Mod：{Path.GetFileName(M.FilePath)}，保留 {Path.GetFileName(Keep.FilePath)}")
                    Catch ex As Exception
                        Logger.Warn(ex, $"[ModChecker] 隔离重复 Mod 失败：{Path.GetFileName(M.FilePath)}")
                    End Try
                Next
            Next

            '2. 缺少前置、声明不兼容
            Dim Provided As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each M As ModInfo In Mods
                If Removed.Contains(M.FilePath) Then Continue For
                Provided.Add(M.Id.ToLowerInvariant())
            Next
            For Each M As ModInfo In Mods
                If Removed.Contains(M.FilePath) Then Continue For
                For Each Dep As String In M.Depends
                    If LoaderDependencies.Contains(Dep) OrElse Provided.Contains(Dep.ToLowerInvariant()) Then Continue For
                    Issues.Add($"缺少前置：{ShowName(M)} 需要「{Dep}」，但 mods 文件夹里没有找到")
                    If MissingDeps IsNot Nothing AndAlso Not MissingDeps.Any(Function(D) D.Equals(Dep, StringComparison.OrdinalIgnoreCase)) Then MissingDeps.Add(Dep)
                Next
                For Each Conflict As String In M.Breaks
                    If Provided.Contains(Conflict.ToLowerInvariant()) Then Issues.Add($"Mod 冲突：{ShowName(M)} 声明与「{Conflict}」不兼容，两者建议只保留一个")
                Next
            Next

            '3. 加载器混装：Fabric 与 Forge 系 Mod 无法在同一个版本里共存
            Dim HasFabric As Boolean = Mods.Any(Function(M) M.Loader = "Fabric" OrElse M.Loader = "Quilt")
            Dim HasForge As Boolean = Mods.Any(Function(M) M.Loader = "Forge")
            If HasFabric AndAlso HasForge Then Issues.Add("加载器混装：mods 文件夹里同时有 Fabric/Quilt 系与 Forge 系 Mod，它们无法在同一个版本中共存")

            '4. 清理堆积的旧崩溃报告（保留 KeepReports 份，0 表示不清理）
            Dim CrashFolder As String = PathIndie & "crash-reports\"
            If KeepReports > 0 AndAlso DirectoryUtils.Exists(CrashFolder) Then
                Dim CrashFiles As New List(Of String)(DirectoryUtils.EnumerateFiles(CrashFolder, False, "*.txt"))
                If CrashFiles.Count > KeepReports Then
                    Dim OldFiles = CrashFiles.OrderByDescending(Function(F) New FileInfo(F).LastWriteTimeUtc).Skip(KeepReports).ToList()
                    For Each OldFile As String In OldFiles
                        Try
                            FileUtils.Move(OldFile, PathIndie & "PCL\ModBackup\旧崩溃报告\" & Stamp & "-" & Path.GetFileName(OldFile))
                        Catch ex As Exception
                            Logger.Warn(ex, "[ModChecker] 清理旧崩溃报告失败")
                        End Try
                    Next
                    Issues.Add($"清理了 {OldFiles.Count} 份旧崩溃报告（保留最近 {KeepReports} 份，其余移入备份目录）")
                End If
            End If

            If Issues.Count = 0 Then Return Nothing

            '写入报告，方便玩家自己查看
            Dim Builder As New Text.StringBuilder
            Builder.AppendLine($"PCL-JN Mod 自检报告（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）")
            Builder.AppendLine($"游戏目录：{PathIndie}")
            Builder.AppendLine($"共检测到 {Issues.Count} 条问题：")
            For i As Integer = 0 To Issues.Count - 1
                Builder.AppendLine($"{i + 1}. {Issues(i)}")
            Next
            Builder.AppendLine()
            If AutoFix AndAlso Removed.Count > 0 Then Builder.AppendLine($"被移出的文件都保存在：{BackupFolder}") Else Builder.AppendLine("本次没有自动修改任何 Mod 文件。")
            Builder.AppendLine("缺少前置不会被自动下载，请到 Mod 的来源处下载对应前置后放进 mods 文件夹。")

            Dim ReportPath As String = PathIndie & "PCL\ModBackup\最近一次自检.txt"
            DirectoryUtils.Create(PathIndie & "PCL\ModBackup\")
            FileUtils.Write(ReportPath, Builder.ToString())
            Logger.Info($"[ModChecker] 自检完成，发现 {Issues.Count} 条问题，报告：{ReportPath}")
            Return Builder.ToString()
        Catch ex As Exception
            Logger.Warn(ex, "[ModChecker] 自检过程出错")
            Return Nothing
        End Try
    End Function

End Module
