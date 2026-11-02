Public Class PageOtherAbout

    Private Shadows IsLoaded As Boolean = False
    Private Sub PageOtherAbout_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded

        '重复加载部分
        PanBack.ScrollToHome()

        '非重复加载部分
        If IsLoaded Then Return
        IsLoaded = True

        'PCL-JN：版本号后缀固定显示为 1.0
        ItemAboutPcl.Info = ItemAboutPcl.Info.Replace("%VERSION%", VersionDisplay).Replace("%VERSIONCODE%.%BRANCH%", "1.0")

    End Sub

    '复制识别码（设置页面也会调用）
    Public Shared Sub CopyIdentify()
        ClipboardSet(Identify)
    End Sub

    'PCL-JN：不再检查官方更新，改为显示本版本说明
    Private Sub BtnAboutUpdate_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnAboutUpdate.Click
        MyMsgBox("本程序 PCL-JN v" & VersionBaseName & " 是基于 PCL 开源代码修改的个人版本，与官方无关，因此不检查官方更新。" & vbCrLf & vbCrLf &
                 "需要更新时，请从获取本程序的渠道下载新版本，直接覆盖替换即可。", "版本说明")
    End Sub

End Class
