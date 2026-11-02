# PCL-JN

PCL-JN 是基于 [Plain Craft Launcher 2（PCL）](https://github.com/Meloong-Git/PCL/) 开源代码的个人修改版本，由 **just_none** 维护，与 PCL 官方无关。

## 本版本的主要改动
- 默认关闭自动更新提示
- 解锁全部隐藏主题：玄素黑、滑稽彩、铁杆粉、神秘紫、欧皇彩、秋仪金、活跃橙、跳票红、极客蓝、自定义
- 补充主题色相映射，切换隐藏主题后界面颜色会真正变化
- 主界面新增「第三方」登录入口（Authlib-Injector）
- 设置页新增「账号与密钥」卡片：微软登录 Client ID、CurseForge API Key 由使用者自行填写
- 补齐「百宝箱」页面：今日人品、内存优化、清理游戏垃圾、回声洞、自定义文件下载等
- 标题栏徽标改为「PCL · JN」
- 「关于与鸣谢」中补充本版本说明

## 构建方式

需要 Visual Studio（含 .NET Framework 4.8 Developer Pack）以及 .NET SDK 8，然后使用 MSBuild 编译 `Plain Craft Launcher 2.vbproj`。

```
MSBuild "Plain Craft Launcher 2.vbproj" -p:Configuration=Release
```

## 版权与许可

本项目采用**分层许可**，两部分须同时遵守：

| 范围 | 对应文件 | 说明 |
| --- | --- | --- |
| 来自 PCL 的全部代码 | [LICENCE](./LICENCE) | 《PCL 分发有限许可》与《PCL 存储库合理使用指南》|
| PCL-JN 新增/修改的部分 | [LICENSE-PCL-JN](./LICENSE-PCL-JN) | MIT |

- 原项目：**Plain Craft Launcher（PCL）**，作者 **龙腾猫跃（LTCat）**，仓库：<https://github.com/Meloong-Git/PCL/>
- 本项目属于《PCL 存储库合理使用指南》中的**重度使用**：基于原仓库修改，与 PCL 官方无关，名称以 PCL 开头并带 `-JN` 后缀以示区分。
- 本项目完整保留原项目的版权声明、许可条款，以及「关于与鸣谢」页面中的原作者署名。
- 二次分发本版本时，请同样遵守 `LICENCE`：保留署名、公开源代码、保留许可声明、界面主色相保持蓝色、不加入赞助解锁类功能、保留 Minecraft 正版购买劝导。
- `LICENSE-PCL-JN`（MIT）只适用于本项目新添加的模块，不能用于替代或放宽 `LICENCE`。

## 相关链接

- [PCL-JN 仓库](https://github.com/HYH-JustNone/PCL-JN)
- [PCL 官方仓库](https://github.com/Meloong-Git/PCL/)
- [PCL 官方下载](https://meloong.com/pcl)
- [帮助文档库](https://github.com/LTCatt/PCL2Help)