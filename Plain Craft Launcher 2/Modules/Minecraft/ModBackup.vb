''' <summary>
''' PCL-JN：启动前的配置与存档备份，以及从备份目录一键回滚。
''' 只有 mods 文件夹发生增删改时才会产生新的备份，避免每次启动都堆积压缩包。
''' </summary>
Public Module ModBackup

    '备份根目录，以 \ 结尾
    Public Function BackupRoot(PathIndie As String) As String
        Return PathIndie & "PCL\ModBackup\"
    End Function

    Private Const FingerprintName As String = ".fingerprint"
    '存档超过这个大小就不再打包，以免备份耗费过长时间
    Private Const MaxSavesSize As Long = 2L * 1024 * 1024 * 1024

    ''' <summary>
    ''' 计算 mods 文件夹的指纹（文件名 + 大小 + 最后修改时间）。mods 不存在或为空时返回空字符串。
    ''' </summary>
    Public Function GetModsFingerprint(PathIndie As String) As String
        Dim ModsFolder As String = PathIndie & "mods\"
        If Not DirectoryUtils.Exists(ModsFolder) Then Return ""
        Dim Parts As New List(Of String)
        For Each FilePath As String In DirectoryUtils.EnumerateFiles(ModsFolder, False, "*.jar")
            Dim Info As New FileInfo(FilePath)
            Parts.Add($"{Info.Name}|{Info.Length}|{Info.LastWriteTimeUtc.Ticks}")
        Next
        If Parts.Count = 0 Then Return ""
        Parts.Sort(StringComparer.OrdinalIgnoreCase)
        Return CryptographyUtils.ComputeHash(String.Join(vbLf, Parts), CryptographyUtils.HashMethod.Sha1)
    End Function

    ''' <summary>
    ''' 启动前调用：Mod 有增删改时备份 options.txt、config 与 saves。
    ''' 任何异常都只会写入日志，不影响启动。
    ''' </summary>
    Public Sub CheckAndBackup(PathIndie As String)
        Try
            Dim Fingerprint As String = GetModsFingerprint(PathIndie)
            If Fingerprint = "" Then Return 'mods 不存在或为空，跳过
            Dim StampFile As String = BackupRoot(PathIndie) & FingerprintName
            Dim LastFingerprint As String = ""
            If FileUtils.Exists(StampFile) Then LastFingerprint = FileUtils.ReadAsString(StampFile).Trim()
            If LastFingerprint = Fingerprint Then Return 'Mod 没有变化
            DirectoryUtils.Create(BackupRoot(PathIndie))
            Dim Folder As String = CreateBackupCore(PathIndie, GetNewBackupFolder(PathIndie))
            '无论是否有内容可备份都记录指纹，避免每次启动重复尝试
            FileUtils.Write(StampFile, Fingerprint)
            If Folder IsNot Nothing Then Logger.Info($"[ModBackup] 检测到 Mod 变化，已备份配置与存档：{Folder}")
        Catch ex As Exception
            Logger.Warn(ex, "[ModBackup] 启动前备份失败")
        End Try
    End Sub

    ''' <summary>
    ''' 立即备份一次（忽略指纹）。没有可备份内容时返回 Nothing，否则返回备份目录。
    ''' </summary>
    Public Function ForceBackup(PathIndie As String) As String
        Dim Folder As String = CreateBackupCore(PathIndie, GetNewBackupFolder(PathIndie))
        If Folder Is Nothing Then Return Nothing
        Try
            DirectoryUtils.Create(BackupRoot(PathIndie))
            FileUtils.Write(BackupRoot(PathIndie) & FingerprintName, GetModsFingerprint(PathIndie))
        Catch ex As Exception
            Logger.Warn(ex, "[ModBackup] 写入 Mod 指纹失败")
        End Try
        Return Folder
    End Function

    ''' <summary>
    ''' 从备份目录回滚。RestoreContent 为 True 时同时还原配置与存档，覆盖前会先备份当前内容。
    ''' 返回给用户看的总结文本，调用方负责提示。
    ''' </summary>
    Public Function Restore(PathIndie As String, BackupFolder As String, RestoreContent As Boolean) As String
        BackupFolder = PathUtils.AddSlashSuffix(BackupFolder)
        Dim Summary As New List(Of String)
        Dim UndoFolder As String = Nothing

        '1. 把备份里的 jar 移回 mods
        Dim ModJars As List(Of String) = DirectoryUtils.EnumerateFiles(BackupFolder & "mods\", False, "*.jar").ToList()
        If ModJars.Count > 0 Then
            Dim ModsFolder As String = PathIndie & "mods\"
            DirectoryUtils.Create(ModsFolder)
            For Each Jar As String In ModJars
                Dim Target As String = ModsFolder & Path.GetFileName(Jar)
                If FileUtils.Exists(Target) Then
                    '同名 Mod 先移到回滚前目录，保证可以反悔
                    If UndoFolder Is Nothing Then UndoFolder = GetUndoFolder(PathIndie)
                    FileUtils.Move(Target, UndoFolder & "mods\" & Path.GetFileName(Jar))
                End If
                FileUtils.Move(Jar, Target)
            Next
            Summary.Add($"恢复了 {ModJars.Count} 个 Mod")
        End If

        '2. 解压配置与存档
        Dim ZipPath As String = BackupFolder & "配置与存档备份.zip"
        If RestoreContent AndAlso FileUtils.Exists(ZipPath) Then
            If UndoFolder Is Nothing Then UndoFolder = GetUndoFolder(PathIndie)
            '先把当前同名内容复制到回滚前目录
            If FileUtils.Exists(PathIndie & "options.txt") Then FileUtils.Copy(PathIndie & "options.txt", UndoFolder & "options.txt")
            If DirectoryUtils.Exists(PathIndie & "config\") Then DirectoryUtils.Copy(PathIndie & "config\", UndoFolder & "config\")
            If DirectoryUtils.Exists(PathIndie & "saves\") Then DirectoryUtils.Copy(PathIndie & "saves\", UndoFolder & "saves\")
            FileUtils.ExtractToDirectory(ZipPath, PathIndie)
            Summary.Add("已还原配置与存档")
        End If

        If Summary.Count = 0 Then Return "这个备份里没有可恢复的内容。"
        Dim Result As String = String.Join("，", Summary) & "。"
        If UndoFolder IsNot Nothing Then Result &= $"还原前的文件已保存到 {UndoFolder}"
        Logger.Info($"[ModBackup] 已从备份回滚：{BackupFolder}（{Result}）")
        Return Result
    End Function

#Region "内部实现"

    '把 options.txt、config、saves 打包到指定目录；没有内容时返回 Nothing
    Private Function CreateBackupCore(PathIndie As String, Folder As String) As String
        Dim Sources As New Dictionary(Of String, String)
        Dim OptionsFile As String = PathIndie & "options.txt"
        If FileUtils.Exists(OptionsFile) Then Sources("options.txt") = OptionsFile
        AddFolderSources(Sources, PathIndie & "config\", "config\")
        Dim SavesFolder As String = PathIndie & "saves\"
        If DirectoryUtils.Exists(SavesFolder) Then
            Dim SavesSize As Long = GetFolderSize(SavesFolder)
            If SavesSize > MaxSavesSize Then
                Logger.Warn("[ModBackup] 存档文件夹超过 2GB，本次只备份 options.txt 与 config")
            Else
                AddFolderSources(Sources, SavesFolder, "saves\")
            End If
        End If
        If Sources.Count = 0 Then Return Nothing
        DirectoryUtils.Create(Folder)
        Dim ZipPath As String = Folder & "配置与存档备份.zip"
        FileUtils.CreateZipFromFiles(ZipPath, Sources)
        Return Folder
    End Function

    '按文件夹内的相对路径加入压缩包，保持 config、saves 的目录结构
    Private Sub AddFolderSources(Sources As Dictionary(Of String, String), Folder As String, ZipPrefix As String)
        If Not DirectoryUtils.Exists(Folder) Then Exit Sub
        For Each FilePath As String In DirectoryUtils.EnumerateFiles(Folder, True)
            Dim Relative As String = Path.GetFileName(FilePath)
            If FilePath.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) Then Relative = FilePath.Substring(Folder.Length)
            Sources(ZipPrefix & Relative) = FilePath
        Next
    End Sub

    Private Function GetFolderSize(Folder As String) As Long
        Dim Total As Long = 0
        For Each FilePath As String In DirectoryUtils.EnumerateFiles(Folder, True)
            Total += New FileInfo(FilePath).Length
        Next
        Return Total
    End Function

    Private Function GetNewBackupFolder(PathIndie As String) As String
        Dim Root As String = BackupRoot(PathIndie)
        DirectoryUtils.Create(Root)
        Dim Stamp As String = DateTime.Now.ToString("yyyyMMdd-HHmmss")
        Dim Folder As String = Root & Stamp & "\"
        Dim Index As Integer = 2
        While DirectoryUtils.Exists(Folder)
            Folder = Root & Stamp & "-" & Index & "\"
            Index += 1
        End While
        Return Folder
    End Function

    '回滚前的现场保存目录
    Private Function GetUndoFolder(PathIndie As String) As String
        Dim Root As String = BackupRoot(PathIndie)
        DirectoryUtils.Create(Root)
        Dim Stamp As String = DateTime.Now.ToString("yyyyMMdd-HHmmss") & "-回滚前"
        Dim Folder As String = Root & Stamp & "\"
        Dim Index As Integer = 2
        While DirectoryUtils.Exists(Folder)
            Folder = Root & Stamp & "-" & Index & "\"
            Index += 1
        End While
        DirectoryUtils.Create(Folder)
        Return Folder
    End Function

#End Region

End Module
