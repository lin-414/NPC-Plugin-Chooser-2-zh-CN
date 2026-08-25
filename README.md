**NPC Plugin Chooser 2**

> ## 中文本地化分支 (Chinese Localization Fork)
>
> 这是 [Piranha91/NPC-Plugin-Chooser-2](https://github.com/Piranha91/NPC-Plugin-Chooser-2)（GPL-3.0）的社区分支：
> 在原版基础上加入完整的简体中文界面，以及面向汉化版游戏的中文 NPC 描述功能。
>
> ### 本分支的新增内容（2026-08）
>
> **界面语言**
> - **完整中文本地化（zh-CN）** —— 覆盖所有 XAML 视图与 ViewModel 动态文本（消息框、折叠组标题、
>   运行按钮等），约 770 个词条；支持运行时在 中文 / English 之间切换，无需重启；不使用 .resx
>   （自定义 `LocExtension` + JSON）。
> - **设置备份 / 恢复** —— 可备份 `Settings.json`，之后随时恢复（恢复前自动保留当前设置的安全副本）。
>
> **面向汉化版游戏的 NPC 描述**
> - **基于 EditorID 的百科检索** —— 汉化版游戏里 NPC 显示名是中文，原版代码无法用它在英文百科
>   上找到条目。本分支改用英文 `EditorID` 搜索 UESP / Elder Scrolls Wiki（CamelCase 拆分、去除
>   玩法前缀），并用 NPC 关键词校验结果。
> - **自动翻译** —— 界面语言为中文时，抓取到的英文描述自动翻译为中文（Google 免费端点，MyMemory
>   备用），失败时回退英文原文。
> - **本地描述缓存** —— 抓取的描述缓存在 `DescriptionCache/descriptions.json`，重复导出不再请求
>   百科；英文与中文分开缓存。
> - **一次性批量预翻译** —— 一次性把全部缓存的英文描述翻译为中文，之后可离线使用。
> - **CSV 人工翻译** —— 将英文描述导出为 CSV（中文列留空），随时离线翻译后重新导入：翻译质量
>   完全由你掌控。
> - **NPC 菜单批量导出** —— 支持按选中集合或筛选子集（含性别筛选）批量导出描述，失败项提供
>   重试按钮。
> - **失败原因分类** —— 网络失败（限流 / 超时 / Cloudflare）与"页面未找到"分开统计与报告，
>   真正的查找未命中不会被计为网络错误。
>
> **抓取可靠性**
> - **extracts API 绕过 Cloudflare** —— UESP/Fandom 的 HTML 页面受 Cloudflare 验证保护；`prop=extracts`
>   API 返回纯文本且不受拦截，优先使用，页面 HTML 仅作回退。
> - **限流保护** —— HTTP 429/503 时全局暂停（60 秒），备用搜索词限速（至多 2 个派生词、间隔 1 秒），
>   模板 NPC（Enc…/Lvl…/TreasCorpse…/SoulCairnSoul…）在发起任何请求前直接跳过——修复了批量任务
>   大面积失败的回归。
> - **导航页识别与排除** —— UESP 字母序派系索引页（"Skyrim:Factions D"）与消歧义页
>   （"Karita may refer to:"）在搜索 / 提取 / 校验 / 缓存各层被识别并拒绝；消歧义页的候选条目
>   （如 `Skyrim:Karita (bard)`）会被解析并抓取真实条目，而不是把导航页当描述输出。
>
> **窗口行为**
> - **进度窗口跟随主窗口** —— 导出 / 进度窗口随主窗口一起最小化与还原，不再浮在桌面之上，
>   也可单独最小化后从任务栏恢复。
>
> **构建修复**
> - ReactiveUI.Fody 所需的 `FodyWeavers.xml`；资源文件打包修正（`<Content>` 与 `<Resource>`、
>   拆分条件 ItemGroup）；可选 NPC Portrait Creator 原生二进制在 csproj 中的条件引用；
>   CharacterViewer.Rendering API 版本同步。
>
> ### 许可
>
> GPL-3.0 —— 与原项目一致，保留所有原始版权声明。
>
> ### 上游文档
>
> 原版完整的功能说明（英文）请参阅上游仓库的 README：
> [Piranha91/NPC-Plugin-Chooser-2](https://github.com/Piranha91/NPC-Plugin-Chooser-2)。
> 本分支未改动上游的功能本身，以上列表即为本分支与上游的全部差异。