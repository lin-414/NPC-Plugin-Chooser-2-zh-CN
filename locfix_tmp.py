import json, re, glob, os

BASE = "Localization/strings.json"
ZH = "Localization/strings.zh-CN.json"

base = json.load(open(BASE, encoding="utf-8"))
zh = json.load(open(ZH, encoding="utf-8"))

def deent(s):
    if not isinstance(s, str):
        return s
    return (s.replace("&#x0a;", "\n").replace("&#10;", "\n")
             .replace("&gt;", ">").replace("&lt;", "<").replace("&amp;", "&"))

val2keys = {}
for k, v in base.items():
    val2keys.setdefault(deent(v), []).append(k)

# (raw literal exactly as in XAML, proposed key, Chinese translation)
# Reused keys are overridden by base lookup; only the ~16 new ones actually get added.
M = [
 ("Allows the 'Live Tile' mugshot source above: when NPC selection reaches it, the tile becomes an embedded 3D viewport rendered on the spot instead of a static image. Costs a render per tile each time an NPC is selected. Individual tiles can always be switched via right-click &gt; Make Live Tile, regardless of this setting.",
   "liveTileSourceExplanation",
   "启用上方的“实时磁贴”头像来源：当 NPC 选择到达此处时，磁贴会变为即时渲染的 3D 视口而非静态图片。每次选中 NPC 都会对该磁贴进行一次渲染。无论此设置如何，都可通过右键 > 设为实时磁贴 单独切换各磁贴。"),
 ("Automatically select appearance mods for NPCs based on your current load order and loose file overrides.",
   "autoSelectAppearanceModsDesc",
   "根据你的当前加载顺序与松散文件覆盖，自动为 NPC 选择外观模组。"),
 ("Clear", "clear", "清除"),
 ("Clear all selections you have made.", "clearAllSelectionsYouHaveMade", "清除你所做的全部选择。"),
 ("Copy selected cells", "copySelectedCells", "复制所选单元格"),
 ("Delete the image currently shown on this tile and fall back to the next available mugshot source.",
   "deleteTileImageTooltip",
   "删除此磁贴当前显示的图像，并回退到下一个可用的头像来源。"),
 ("Deselect", "deselect", "取消选择"),
 ("Enable Live Tiles on Selection", "enableLiveTilesOnSelection", "选中时启用实时磁贴"),
 ("Export", "export", "导出"),
 ("Export the current list of selected appearance mods for each NPC to a .json file.",
   "exportTooltip",
   "将每个 NPC 当前已选外观模组的列表导出为 .json 文件。"),
 ("Filter rows by any column", "filterRowsByAnyColumn", "按任意列筛选行"),
 ("Filter the list by mod display name&#x0a;Ctrl+Shift+C clears all search filters",
   "filterByModDisplayName",
   "按模组显示名称筛选列表\nCtrl+Shift+C 清除所有搜索筛选"),
 ("Filter:", "filterLabel", "筛选："),
 ("FormKey", "formKey", "FormKey"),
 ("Generate Mugshot", "generateMugshot", "生成头像"),
 ("Hide or unhide mugshots. Right-click or click for options (enabled if at least one mugshot is checked).",
   "hideMugshotsTooltip",
   "隐藏或显示头像。右键或点击以查看更多选项（至少勾选一个头像后可用）。"),
 ("Import", "import", "导入"),
 ("Import a list of NPC appearance selections from a .json file, overwriting current choices.",
   "importTooltip",
   "从 .json 文件导入 NPC 外观选择列表，覆盖当前选择。"),
 ("Invalid Selections Found", "invalidSelectionsFound", "发现无效选择"),
 ("Issue", "issue", "问题"),
 ("Jump to NPC in List", "jumpToNPCInList", "在列表中跳转到该 NPC"),
 ("Live Tile turns this mugshot into a miniature 3D viewport (left-drag to rotate, middle-drag to move, wheel to zoom), framed like the auto-generated mugshot. Make Mugshot turns it back into a static image.",
   "liveTileViewportExplanation",
   "实时磁贴将此头像变为迷你 3D 视口（左键拖动旋转，中键拖动移动，滚轮缩放），外观与自动生成的头像一致。生成头像则将其还原为静态图片。"),
 ("Match Load Order", "matchLoadOrder", "匹配加载顺序"),
 ("Missing Record", "missingRecord", "缺失记录"),
 ("Mod", "mod", "模组"),
 ("Mod Issues", "modIssues", "模组问题"),
 ("Mods", "mods", "模组"),
 ("NPC", "nPC", "NPC"),
 ("NPCs", "nPCs", "NPC"),
 ("Open the Randomize dialog to pick random appearances for the visible or all NPCs, with options for base / shared / favorite faces and which mods are eligible as sources.",
   "randomizeTooltip",
   "打开随机化对话框，为可见或全部 NPC 选取随机外观，可设置基础 / 共享 / 收藏面孔，以及可作为来源的模组。"),
 ("Randomize", "randomize", "随机化"),
 ("Render this appearance now with the built-in 3D renderer, replacing any auto-generated mugshot it already has. Works whether or not Auto-Generate missing mugshots is switched on.",
   "renderNowTooltip",
   "立即使用内置 3D 渲染器渲染此外观，替换其已有的自动生成头像。无论是否开启“自动生成缺失头像”均可用。"),
 ("Run", "run", "运行"),
 ("Settings", "settings", "设置"),
 ("Show 3D Preview (Ctrl+Shift+RClick)", "show3DPreviewCtrl", "显示 3D 预览（Ctrl+Shift+右键）"),
 ("Show Data Folder Assets Icon", "showDataFolderAssetsIcon", "显示数据文件夹资源图标"),
 ("Show Full Image (Ctrl+RClick)", "showFullImageCtrl", "显示完整图像（Ctrl+右键）"),
 ("Show only mods with an affected NPC whose name or FormKey matches&#x0a;Ctrl+Shift+C clears all search filters",
   "showOnlyAffectedMods",
   "仅显示受影响 NPC 的名称或 FormKey 匹配的模组\nCtrl+Shift+C 清除所有搜索筛选"),
 ("Skip them and continue with only the valid selections?", "skipInvalidSelections", "跳过它们，仅使用有效选择继续？"),
 ("Summary", "summary", "摘要"),
 ("Toggle Live Tile mode for the checked mugshots (Requires 1+ selected). 3D turns them into embedded 3D viewports; 2D turns them back into static mugshots. On a mixed selection the button applies the state shown.",
   "toggleLiveTileTooltip",
   "为所选头像切换实时磁贴模式（需至少选中 1 个）。3D 将其变为嵌入式 3D 视口；2D 还原为静态头像。若选择混合，则按按钮所示状态应用。"),
 ("Un-greys the 'Live Tile' source above. Place it first to always get live 3D tiles, or last as a fallback when no image exists.",
   "ungreyLiveTileExplanation",
   "取消上方“实时磁贴”来源的置灰状态。将其置于首位可始终获得实时 3D 磁贴；置于末位则作为无图像时的回退。"),
 ("Uncheck all mugshots for the current NPC (Requires 1+ selected).",
   "uncheckAllMugshotsTooltip",
   "取消当前 NPC 的全部头像勾选（需至少选中 1 个）。"),
 ("Use FaceFinder API for missing mugshots", "useFaceFinderAPIFor", "对缺失头像使用 FaceFinder API"),
 ("Validate", "validate", "校验"),
 ("View Full Screen (Ctrl+RClick)", "viewFullScreenCtrl", "全屏查看（Ctrl+右键）"),
 ("When on (default), a mugshot that pulled non-vanilla assets from your data folder — because they were not found in the mod's Corresponding Mod Folders — shows the data-folder-assets icon, recorded in the PNG so it survives restarts. These assets come from some other installed mod: it must stay activated, or be added to this mod's Corresponding Mod Folders, for the NPC to look right in game. Informational only — the render itself is correct. Unchecking hides the icon and re-renders the affected mugshots once so their recorded state is cleared.",
   "dataFolderAssetsIconExplanation",
   "开启时（默认），若头像从其数据文件夹提取了非原版资源——因为在模组的“对应模组文件夹”中未找到——将显示数据文件夹资源图标，并记录在 PNG 中以便重启后保留。这些资源来自其他已安装模组：该模组必须保持启用，或加入此模组的对应模组文件夹，NPC 在游戏中才能显示正常。仅为提示——渲染本身是正确的。取消勾选将隐藏该图标，并重新渲染受影响头像一次以清除其记录状态。"),
 ("When on (default), a mugshot whose outfit/headgear is missing meshes or textures — or references a physics config that doesn't exist (a broken SMP/HDT link inside the mod; the render itself is correct) — shows the outfit-assets warning icon, recorded in the PNG so it survives restarts. The missing meshes/textures re-render when you install the assets (via 'Re-render When: Missing Assets'); the broken physics link is informational. Unchecking hides the icon and re-renders the affected mugshots once so their recorded state is cleared.",
   "outfitAssetsIconExplanation",
   "开启时（默认），若头像的服饰/头饰缺失网格或贴图——或引用了不存在的物理配置（模组内部损坏的 SMP/HDT 链接；渲染本身正确）——将显示服饰资源警告图标，并记录在 PNG 中以便重启后保留。安装对应资源后（通过“重新渲染条件：缺失资源”），缺失的网格/贴图会重新渲染；损坏的物理链接仅为提示。取消勾选将隐藏该图标，并重新渲染受影响头像一次以清除其记录状态。"),
 ("⚠ Mod has changed since this scan — results may be outdated",
   "modChangedSinceScan",
   "⚠ 自本次扫描以来模组已变更——结果可能已过时"),
]

plan = {}          # raw -> final key
new_added = []     # keys actually added to JSON
for raw, key, zhtext in M:
    stored = deent(raw)
    if stored in val2keys:
        usekey = val2keys[stored][0]      # reuse existing key
    else:
        usekey = key
        orig = usekey
        i = 2
        while usekey in base:
            usekey = f"{orig}{i}"
            i += 1
        if usekey not in base:
            base[usekey] = stored
            new_added.append(usekey)
        if usekey not in zh:
            zh[usekey] = zhtext
    plan[raw] = usekey

# Apply XAML replacements
props = ["Text", "Content", "Header", "ToolTip", "Title", "Watermark", "Hint", "Placeholder"]
changed = {}
for fp in glob.glob("**/*.xaml", recursive=True):
    if "/obj/" in fp or "/bin/" in fp:
        continue
    txt = open(fp, encoding="utf-8", errors="ignore").read()
    original = txt
    for raw, usekey in plan.items():
        for p in props:
            old = f'{p}="{raw}"'
            if old in txt:
                txt = txt.replace(old, f'{p}={{l:Loc {usekey}}}')
    if txt != original:
        open(fp, "w", encoding="utf-8").write(txt)
        changed[fp] = sum(1 for _ in [1])  # marker

# Count actual replacements per file by diffing lines
import io
changed_files = []
for fp in glob.glob("**/*.xaml", recursive=True):
    if "/obj/" in fp or "/bin/" in fp:
        continue
    if fp in changed or True:
        pass

# Write JSON back
json.dump(base, open(BASE, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
json.dump(zh, open(ZH, "w", encoding="utf-8"), indent=2, ensure_ascii=False)

print("NEW keys added to JSON:", new_added)
print("XAML files changed:", sorted(changed.keys()))
