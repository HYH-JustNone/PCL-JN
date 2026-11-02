''' <summary>
''' 可选的 AI 崩溃辅助分析，仅在用户自行配置了 OpenAI 兼容接口时启用，支持多个平台预设。
''' </summary>
Public Module ModAI

    ''' <summary>
    ''' 一个 AI 平台的预设信息：显示名、API Key 设置键后缀、默认接口地址与常用模型列表。
    ''' </summary>
    Public Class AiPlatformPreset
        Public Name As String
        Public KeySuffix As String
        Public ApiUrl As String
        Public Models As String()
    End Class

    '平台预设表，顺序与设置项 AiPlatform 的取值一致
    Public ReadOnly AiPlatforms As AiPlatformPreset() = {
        New AiPlatformPreset With {
            .Name = "OpenAI", .KeySuffix = "OpenAI",
            .ApiUrl = "https://api.openai.com/v1/chat/completions",
            .Models = {"gpt-4o-mini", "gpt-4o", "gpt-4.1-mini", "o3-mini"}},
        New AiPlatformPreset With {
            .Name = "DeepSeek", .KeySuffix = "DeepSeek",
            .ApiUrl = "https://api.deepseek.com/v1/chat/completions",
            .Models = {"deepseek-chat", "deepseek-reasoner"}},
        New AiPlatformPreset With {
            .Name = "Moonshot（Kimi）", .KeySuffix = "Moonshot",
            .ApiUrl = "https://api.moonshot.cn/v1/chat/completions",
            .Models = {"moonshot-v1-8k", "moonshot-v1-32k", "kimi-k2"}},
        New AiPlatformPreset With {
            .Name = "智谱 GLM", .KeySuffix = "Zhipu",
            .ApiUrl = "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            .Models = {"glm-4-flash", "glm-4-plus", "glm-4-air"}},
        New AiPlatformPreset With {
            .Name = "硅基流动", .KeySuffix = "SiliconFlow",
            .ApiUrl = "https://api.siliconflow.cn/v1/chat/completions",
            .Models = {"Qwen/Qwen2.5-7B-Instruct", "Qwen/Qwen2.5-72B-Instruct", "deepseek-ai/DeepSeek-V3"}},
        New AiPlatformPreset With {
            .Name = "自定义", .KeySuffix = "Custom",
            .ApiUrl = "",
            .Models = New String() {}}
    }

    '提交给 AI 的日志最大字符数，超出时优先保留尾部
    Private Const MaxLogLength As Integer = 20000

    ''' <summary>
    ''' 按索引获取平台预设，越界时回退到第一项。
    ''' </summary>
    Public Function GetPlatform(Index As Integer) As AiPlatformPreset
        Return AiPlatforms(Math.Max(0, Math.Min(Index, AiPlatforms.Length - 1)))
    End Function

    ''' <summary>
    ''' 某个平台保存 API Key 的设置键名。
    ''' </summary>
    Public Function ApiKeyName(Index As Integer) As String
        Return "AiApiKey_" & GetPlatform(Index).KeySuffix
    End Function

    '当前选中的平台预设
    Public ReadOnly Property Platform As AiPlatformPreset
        Get
            Return GetPlatform(Settings.Get(Of Integer)("AiPlatform"))
        End Get
    End Property

    '用户配置的接口地址，留空时使用当前平台的默认值
    Public ReadOnly Property ApiUrl As String
        Get
            Dim Custom As String = Settings.Get(Of String)("AiApiUrl")
            If Not String.IsNullOrWhiteSpace(Custom) Then Return Custom.Trim()
            Return Platform.ApiUrl
        End Get
    End Property
    '当前平台已保存的 API Key
    Public ReadOnly Property ApiKey As String
        Get
            Return If(Settings.Get(Of String)("AiApiKey_" & Platform.KeySuffix), "").Trim()
        End Get
    End Property
    '用户配置的模型名，留空时使用当前平台列表的第一个模型
    Public ReadOnly Property Model As String
        Get
            Dim Custom As String = Settings.Get(Of String)("AiModel")
            If Not String.IsNullOrWhiteSpace(Custom) Then Return Custom.Trim()
            If Platform.Models IsNot Nothing AndAlso Platform.Models.Length > 0 Then Return Platform.Models(0)
            Return ""
        End Get
    End Property

    ''' <summary>
    ''' 接口地址与 Key 均已填写时才启用 AI 分析。
    ''' </summary>
    Public Function IsConfigured() As Boolean
        Return Not String.IsNullOrWhiteSpace(ApiUrl) AndAlso Not String.IsNullOrWhiteSpace(ApiKey)
    End Function

    ''' <summary>
    ''' 将崩溃日志提交给 AI 分析，返回分析文本；失败或未配置时返回 Nothing，不抛出异常。
    ''' </summary>
    Public Function AnalyzeCrash(LogText As String) As String
        Try
            If Not IsConfigured() OrElse String.IsNullOrWhiteSpace(LogText) Then Return Nothing

            '构建 OpenAI Chat Completions 请求体
            Dim Messages As New JArray From {
                New JObject From {
                    {"role", "system"},
                    {"content", "你是 Minecraft 启动失败诊断专家。请根据用户提供的启动器与游戏日志，用中文分析崩溃的根因，分点作答，并给出可执行的具体修复步骤；" &
                                "不要编造日志中不存在的 Mod、文件或版本信息，信息不足时请明确指出还需要哪些日志。"}
                },
                New JObject From {
                    {"role", "user"},
                    {"content", TruncateLog(LogText)}
                }
            }
            Dim Body As New JObject From {
                {"model", Model},
                {"messages", Messages}
            }

            Using Client As New HttpClient
                Client.Timeout = TimeSpan.FromSeconds(60)
                Using Request As New HttpRequestMessage(HttpMethod.Post, ApiUrl)
                    Request.Headers.TryAddWithoutValidation("Authorization", "Bearer " & ApiKey)
                    Request.Content = New StringContent(Body.ToString(), Encoding.UTF8, "application/json")
                    Using Response = Client.SendAsync(Request).GetAwaiter().GetResult()
                        Dim Raw As String = Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        If Not Response.IsSuccessStatusCode Then
                            Logger.Warn($"AI 崩溃分析请求失败：{Response.StatusCode}，{Raw.Substring(0, Math.Min(Raw.Length, 200))}")
                            Return Nothing
                        End If
                        '取出 choices[0].message.content
                        Dim Choices As JToken = JObject.Parse(Raw)("choices")
                        Dim FirstChoice As JObject = TryCast(If(Choices Is Nothing, Nothing, Choices.FirstOrDefault()), JObject)
                        Dim Message As JObject = If(FirstChoice Is Nothing, Nothing, TryCast(FirstChoice("message"), JObject))
                        Dim Content As String = If(Message Is Nothing, Nothing, Message("content")?.ToString)
                        If String.IsNullOrWhiteSpace(Content) Then
                            Logger.Warn("AI 返回的内容为空或格式异常")
                            Return Nothing
                        End If
                        Return Content.Trim()
                    End Using
                End Using
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "AI 崩溃分析失败")
            Return Nothing
        End Try
    End Function

    '日志过长时保留尾部，并在开头注明
    Private Function TruncateLog(Raw As String) As String
        If Raw.Length <= MaxLogLength Then Return Raw
        Return "（日志过长，已省略前面的部分，以下为最后 " & MaxLogLength & " 个字符）" & vbCrLf & Raw.Substring(Raw.Length - MaxLogLength)
    End Function

End Module
