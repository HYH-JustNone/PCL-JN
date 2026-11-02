-- PCL-JN 示例插件：启动计数器
-- 用到的 API：GetSetting / SetSetting / Log / Hint / ShowDialog / AddMenuItem / GetLaunchArgs
-- 把整个文件夹丢进 PCL 的 Plugins 目录，重启启动器即可生效。

local count = tonumber(pcl.GetSetting("count") or "0") or 0

function OnLoad()
    pcl.Log("启动计数器已加载，当前记录 " .. count .. " 次启动")

    -- 在设置页的插件卡片里注册一个按钮
    pcl.AddMenuItem("查看启动次数", function()
        local args = pcl.GetLaunchArgs()
        local jvm = tostring(args.jvm or "")
        local game = tostring(args.game or "")
        local lines = {
            "从安装以来一共启动了 " .. count .. " 次游戏。",
            "当前版本：" .. tostring(args.instance or "（未选择）"),
            "自定义 JVM 参数：" .. (jvm == "" and "（无）" or jvm),
            "自定义游戏参数：" .. (game == "" and "（无）" or game)
        }
        pcl.ShowDialog("启动计数器", table.concat(lines, "\n"))
    end)
end

function OnGameLaunching(instance)
    count = count + 1
    pcl.SetSetting("count", tostring(count))
    pcl.Hint("第 " .. count .. " 次启动：" .. instance)
end

function OnGameExited(code)
    if tonumber(code) ~= 0 then
        pcl.Log("游戏异常退出，退出代码 " .. tostring(code))
    end
end
