Public Class PageDownloadPlugin
    Implements IRefreshable

    Private MarketList As New List(Of Plugin.MarketPlugin)
    Private IsLoading As Boolean = False

    Private Sub PageDownloadPlugin_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If MarketList.Count = 0 Then LoadMarket(False)
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        LoadMarket(True)
    End Sub

    Private Sub BtnRefresh_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRefresh.Click
        LoadMarket(True)
    End Sub

    '安装本地 .pjnp 插件包
    Private Sub BtnInstallLocal_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnInstallLocal.Click
        Dim FileName As String = Dialogs.SelectFile("选择插件包", False, filter:={({"pjnp"}, "PCL-JN 插件包")}).FirstOrDefault()
        If String.IsNullOrEmpty(FileName) Then Return
        PageSetupLaunch.InstallPackageWithConfirm(FileName, False, Sub() RenderList())
    End Sub

    Private Sub TextSearch_TextChanged(sender As Object, e As TextChangedEventArgs) Handles TextSearch.TextChanged
        RenderList()
    End Sub

    Private Sub LoadMarket(ForceRefresh As Boolean)
        If IsLoading Then Return
        IsLoading = True
        PanList.Children.Clear()
        PanList.Children.Add(New TextBlock With {.Text = "正在获取插件列表……", .FontSize = 12, .Opacity = 0.7})
        RunInNewThread(Sub()
                           Dim List As List(Of Plugin.MarketPlugin) = Plugin.PluginManager.GetMarketPlugins(ForceRefresh)
                           RunInUi(Sub()
                                       MarketList = List
                                       IsLoading = False
                                       RenderList()
                                       If List.Count = 0 Then Hint("插件列表为空或获取失败，请检查网络", HintType.Red)
                                   End Sub)
                       End Sub, "Plugin Market")
    End Sub

    Private Sub RenderList()
        If PanList Is Nothing Then Return
        PanList.Children.Clear()
        Dim Keyword As String = If(TextSearch Is Nothing, "", TextSearch.Text.Trim())
        Dim Installed As List(Of Plugin.PluginEntry) = Plugin.PluginManager.GetPlugins()
        Dim Shown As Integer = 0
        For Each Item As Plugin.MarketPlugin In MarketList
            If Keyword <> "" AndAlso
               Item.Name.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase) < 0 AndAlso
               Item.Description.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase) < 0 AndAlso
               Item.Id.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase) < 0 Then Continue For
            Shown += 1
            Dim Row As New Grid With {.Margin = New Thickness(0, 0, 0, 10)}
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
            Dim Info As New StackPanel
            Info.Children.Add(New TextBlock With {
                .Text = Item.Name & "  " & Item.Version,
                .FontWeight = FontWeights.Bold,
                .TextWrapping = TextWrapping.Wrap
            })
            Dim Detail As New List(Of String)
            If Not String.IsNullOrEmpty(Item.Author) Then Detail.Add("作者 " & Item.Author)
            If Item.Permissions.Count > 0 Then Detail.Add("权限 " & String.Join("、", Item.Permissions))
            If Item.Platforms.Count > 0 Then Detail.Add("平台 " & String.Join("/", Item.Platforms))
            If Detail.Count > 0 Then
                Info.Children.Add(New TextBlock With {
                    .Text = String.Join(" / ", Detail),
                    .FontSize = 11,
                    .Opacity = 0.7,
                    .Margin = New Thickness(0, 3, 0, 0)
                })
            End If
            If Not String.IsNullOrEmpty(Item.Description) Then
                Info.Children.Add(New TextBlock With {
                    .Text = Item.Description,
                    .FontSize = 11.5,
                    .Opacity = 0.85,
                    .TextWrapping = TextWrapping.Wrap,
                    .Margin = New Thickness(0, 4, 16, 0)
                })
            End If
            Row.Children.Add(Info)
            Dim InstalledEntry As Plugin.PluginEntry = Installed.FirstOrDefault(Function(E) String.Equals(E.Id, Item.Id, StringComparison.OrdinalIgnoreCase))
            Dim IsInstalled As Boolean = InstalledEntry IsNot Nothing
            Dim CanUpdate As Boolean = IsInstalled AndAlso Plugin.PluginManager.IsNewerVersion(Item.Version, InstalledEntry.Version)
            Dim InstallButton As New MyButton With {
                .Text = If(CanUpdate, "更新", If(IsInstalled, "已安装", "安装")),
                .Height = 32,
                .Padding = New Thickness(15, 0, 15, 0),
                .VerticalAlignment = VerticalAlignment.Top
            }
            InstallButton.IsEnabled = CanUpdate OrElse Not IsInstalled
            Dim Target As Plugin.MarketPlugin = Item
            Dim TargetUpgrade As Boolean = CanUpdate
            AddHandler InstallButton.Click,
            Sub(Sender2 As Object, Ev2 As MouseButtonEventArgs)
                PageSetupLaunch.InstallFromMarketWithConfirm(Target, TargetUpgrade, Sub() RenderList())
            End Sub
            Grid.SetColumn(InstallButton, 1)
            Row.Children.Add(InstallButton)
            PanList.Children.Add(Row)
        Next
        If Shown = 0 Then
            PanList.Children.Add(New TextBlock With {
                .Text = If(MarketList.Count = 0, "暂时没有可安装的插件", "没有匹配的插件"),
                .FontSize = 12,
                .Opacity = 0.6
            })
        End If
    End Sub

End Class
