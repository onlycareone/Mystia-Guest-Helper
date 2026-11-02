# MystiaGuestTag — 东方夜雀食堂稀客喜好标签插件

营业时自动识别进店稀客，在屏幕角落用带黑底（默认 70% 不透明）的面板显示：
**稀客名字（加粗+按人上色）、喜好/厌恶标签、以及按喜好算出的高价/低价推荐菜品与酒水**。
3 个指定稀客用硬编码标签，其余稀客自动退回游戏运行时数据（`LikeFoodTag` / `GetFoodTagText`）。
默认停靠**右下角**（右上角被游戏自带的传菜/酒水/厨师 HUD 占着），可在配置文件里改。

- 游戏版本：4.4.0e（Unity 2021.3.28f1，IL2CPP metadata v29）
- 加载器：BepInEx 6.0.0-be.785+6abdba4（Unity.IL2CPP-win-x64）
- 插件目标框架：`net6.0`（游戏自带 `BepInEx/dotnet` 的 coreclr 为 .NET 6.0.7）

## 给玩家：怎么装（三种情况）

只想玩游戏、不打算编译的，看这一节就够。装好后的效果：营业时屏幕右下角出现稀客面板，显示进店稀客的名字、喜好/厌恶标签和高低价推荐菜。

### 情况 A：从没装过 BepInEx（全新游戏目录）

1. 从 [Releases](https://github.com/onlycareone/Mystia-Guest-Helper/releases) 下载 `GuestTag.zip`。
2. 在 Steam 库里右键游戏 → 管理 → 浏览本地文件，打开游戏根目录。
3. 把 **zip 里的全部内容**解压进去，与 `Touhou Mystia Izakaya.exe` 平级。
4. 启动游戏。首次启动会慢一点（IL2CPP 初始化），`BepInEx\LogOutput.log` 生成即说明加载器起来了。

解压后的目录应该长这样：

```
Touhou Mystia Izakaya/
├─ Touhou Mystia Izakaya.exe
├─ winhttp.dll            ← zip 里的
├─ doorstop_config.ini    ← zip 里的
├─ .doorstop_version      ← zip 里的
├─ dotnet/                ← zip 里的
└─ BepInEx/               ← zip 里的（已有同名文件就覆盖合并）
   └─ plugins/GuestTag.dll
```

### 情况 B：已经装过 BepInEx（别的 Mod 或旧版本）

只需要一个文件：`GuestTag.dll`，放进

```
<游戏根目录>\BepInEx\plugins\GuestTag.dll
```

直接平铺在 `plugins` 下，**不要**再套一层文件夹。启动游戏即可。
如果 `plugins` 里还躺着以前的 `MystiaGuestTag.dll`，删掉它，别和 `GuestTag.dll` 并存。

### 情况 C：装完黑屏 / 闪退 / 进不去游戏

1. 先看 `<游戏根目录>\BepInEx\LogOutput.log` 最后几十行，报错通常写在那里。
2. 启动几秒就闪退：先 Steam「验证游戏文件完整性」（本机 2026-11-01 的崩溃是 6 个文件缺失，补回后 D3D11 正常），再考虑 `-force-vulkan`。
3. 还不行：把 `LogOutput.log` 提到 issue 里，说明游戏版本（目前适配 4.4.0e）。
4. 想彻底卸载：删掉 `BepInEx\plugins\GuestTag.dll` 就行，其余文件不影响游戏。

## 目录

```
MystiaGuestTag/
├─ src/MystiaGuestTag/
│  ├─ MystiaGuestTag.csproj      # 引用 $(GameDir)\BepInEx\{core,interop}\*.dll
│  ├─ Plugin.cs                  # [BepInPlugin] 入口，注册两个 Harmony Hook
│  ├─ SpecialGuestTracking.cs    # Hook 稀客生成，读 stringId / Id
│  ├─ GuestTagDatabase.cs        # 硬编码的 3 个稀客喜好标签字典
│  ├─ GuestTagOverlay.cs         # Hook 营业场景 OnGUI，画面板（角落/字号/透明度可配）
│  ├─ GuestTagConfig.cs          # BepInEx 配置项（角落、宽度、字号、底框透明度）
│  ├─ RecipeRecommender.cs       # 按喜好标签给当晚菜单打分，算推荐菜品
│  └─ PhaseGate.cs               # 只在实际营业相位读游戏对象
├─ src/NuGet.Config              # 隔离本机 C 盘 NuGet 配置
├─ tools/deploy.sh               # 构建 + 复制到游戏 plugins
├─ tools/dump_type.ps1           # Mono.Cecil 读 interop 元数据的探查脚本
└─ MystiaAI/ MetaMystia/         # 参考实现（不参与本项目编译）
```

## 构建与部署（想自己编译的人看）

需要 .NET SDK 8 和游戏本体（要引用游戏目录里 `BepInEx/core`、`BepInEx/interop` 的 DLL，这些不随仓库分发）。在仓库根目录：

```bash
dotnet build src/MystiaGuestTag/MystiaGuestTag.csproj -c Release -p:GameDir="<游戏根目录>"
```

- `GameDir` 指向**游戏根目录**（含 `Touhou Mystia Izakaya.exe`），不是 `BepInEx` 目录；也可以设环境变量 `TOUHOU_GAME_DIR` 代替命令行参数。
- 两个都没给时构建会直接停下并说明怎么配，不会拿作者机器上的路径去猜。
- 路径不对（缺 `BepInEx/core`）同样会报错并指出缺什么。
- 产物在 `src/MystiaGuestTag/bin/Release/net6.0/MystiaGuestTag.dll`；把它放到 `<游戏根目录>/BepInEx/plugins/` 并命名为 `GuestTag.dll`，就和发布包里的文件一致。
- 仓库不收 `bin/`、`obj/`、`_downloads/` 以及 BepInEx 运行时文件（见 `.gitignore`）；给玩家的完整包见 Releases。

## 取数链路（interop 元数据已核实）

```
NightScene.GuestManagementUtility.GuestsManager.SpawnSpecialGuestGroup(int id, ...)  →  SpecialGuestsController
    →  .SpecialGuest : GameData.Core.Collections.NightSceneUtility.SpecialGuest
        →  .stringId (string，内部 label，如 "Wriggle") / .Id (int)
```

- Hook 1：`GuestsManager.SpawnSpecialGuestGroup` 的 **postfix**，读 `__result.SpecialGuest`；不改 `__result`，对游戏逻辑零影响。
- Hook 2：`NightScene.SceneManager.OnGUI()`（营业场景常驻的私有 OnGUI）的 **postfix**，用 IMGUI 画面板。

未注入自定义 MonoBehaviour：Il2CppInterop 注入类的 `Update`/`OnGUI` 不触发是已知问题
（MystiaAI 实测其注入通道 Update 从未触发），挂到游戏自己的 `OnGUI` 更稳，也天然只在夜晚营业场景生效。

## 为什么不是 DayScene.DaySceneMap

用户最初提示的 `DayScene.DaySceneMap$$RefreshNPCs` / `GetMapNPCs` 已核对：

- `DayScene.DaySceneMap` 存在 `void RefreshNPCs(bool rotateCharacter)`；
- **全程序集搜索 `GetMapNPCs` / `MapNPC` 类型与方法：0 命中**（4.4.0e interop 里不存在）；
- `RefreshNPCs` 只维护 `GameData.RunTime.DaySceneUtility.Collection.TrackedNPC` 的白天地图可见性，
  与「营业时进店的稀客」不是同一条链路。

营业场景的稀客由 `GuestsManager.SpawnSpecialGuestGroup` 生成，故 Hook 点选它。

## 硬编码喜好标签

| stringId | 名称 | 喜好标签（食物） | 喜好标签（酒水，按人设推） |
| --- | --- | --- | --- |
| `Wriggle` | 莉格露 | 甜 / 猎奇 / 生 / 肉 | 甘 / 水果 / 低酒精 / 气泡 |
| `Akyuu`（兼容 `Akyu`） | 阿求 | 汤羹 / 清淡 / 甜 / 和风 / 文化底蕴 | 无酒精 / 甘 / 古典 / 可加热 |
| `Rumia` | 露米娅 | 猎奇 / 肉 / 招牌 / 生 / 饱腹 | 高酒精 / 辛 / 烧酒 / 直饮 |

匹配时统一小写并去掉非字母数字字符，避免拼写差异漏识别。

**酒水那列是推断的，不是从游戏数据里读的**：运行时只要 `SpecialGuest.LikeBevTag` 读得出来，
就以游戏数据为准（见「推荐酒水」一节），硬编码只做读不到时的兜底，所以推错也不会显示错。

酒水标签取值来自游戏内真实标签（对照 BWIKI 酒水一览的 Tag 过滤器）：
无酒精 / 低酒精 / 中酒精 / 高酒精 / 可加冰 / 可加热 / 烧酒 / 清酒 / 鸡尾酒 / 西洋酒 /
利口酒 / 啤酒 / 直饮 / 水果 / 甘 / 辛 / 苦 / 气泡 / 古典 / 现代 / 提神。

## 未硬编码稀客的兜底：读运行时标签

面板不只认那 3 个：未命中的稀客会退到游戏自己的数据 ——

```csharp
guest.LikeFoodTag            // int[]，喜好标签 id 数组
guest.GetFoodTagText(int id) // 标签 id → 当前语言文本
```

结果按 `stringId` 缓存在 `SpecialGuestTracking.LiveTagCache`（`RefreshPresent()` 每帧都会调用
`Record()`，不缓存的话每帧都会走一遍 IL2CPP 取文本）。读不到时面板显示「喜好未收录（stringId = …）」，
日志同时留一行 `发现未收录稀客`，方便把 stringId 补进硬编码表。

## UI 布局与配置

面板默认停在**屏幕左上角、避让顶部招财猫/金钱条**（`TopLeft` + 96px 上边距）：碰不到右边的 COMBO 区，也压不到底部金钱栏/按键提示。底板走「日式暖色食堂风」：深棕 RGBA(60,40,30,200) 半透明底 + 2px 米金细边。
配置写在 `BepInEx/config/cc.mystia.guesttag.cfg`（改完重开游戏生效）：

| 配置项 | 默认 | 说明 |
| --- | --- | --- |
| `UI.Corner` | `TopLeft` | 停靠位置：`TopLeft` / `TopRight` / `MiddleLeft` / `MiddleRight` / `BottomLeft` / `BottomRight` |
| `UI.Layout` | `Single` | 稀客多时的排版：`Single` 单列 / `TwoColumns` 两列并排 |
| `UI.PanelWidth` | `320` | 单列宽度（像素，220~900）：别再拉宽，横向太长不好读 |
| `UI.MaxWidth` | `360` | 单列宽度上限（200~1200），实际取 `min(PanelWidth, MaxWidth, 屏幕宽×0.45)` |
| `UI.FontSize` | `16` | 正文字号（10~32） |
| `UI.BackgroundAlpha` | `0.7` | 暖棕底板不透明度（0=全透明，1=纯实色）；0.7 对应 RGBA(60,40,30,200) |
| `UI.BottomMargin` | `250` | 仅 `Bottom*` 生效：面板底边离屏幕底部多远 |
| `UI.UI_X` | `-1` | 面板左上角 X；拖动标题栏后自动写回，`-1` = 按 `Corner` 自动算 |
| `UI.UI_Y` | `-1` | 面板左上角 Y；同上 |
| `Display.MaxGuestsToDisplay` | `3` | 面板上最多同时显示几位稀客（1~10），超出的折叠进标题「已显示 x/y 位」 |
| `Display.MaxRecipesPerGuest` | `3` | 每位稀客「高价推荐 / 低价推荐」**各**列几条（1~10，面板上 `菜品 [-] [+]` 可当场调；**实际每组最多 2 条**，多出来的会被截掉） |
| `Display.MaxDrinksPerGuest` | `2` | 每位稀客「高价酒水 / 低价酒水」**各**列几条（1~10，面板上 `酒水 [-] [+]` 可当场调；**实际每组最多 1 条**，多出来的会被截掉） |
| `Prices.CustomPrices` | 空 | 自定义价格：`冷豆腐 = 20; 二天一流 = 120`，分号分隔；填了的菜按这个价显示，没填的读游戏实际售价 |

排版规则：每个稀客一个区块 ——

```
稀客喜好（3）                          ← 暗金标题 #F2D08A
  莉格露  Wriggle　预算 ¥420            ← 明黄 #FFE066 加粗；后面跟内部 label 和这一顿的预算
  ────────────────────────────         ← 名字下方：暗金横线 #B8933F
  喜好　甜 · 猎奇 · 生 · 肉 …          ← 米白 #F2E6C9，标签之间用 · 分隔，最多 5 个，多的用 …
  [高价推荐] 二天一流 (¥90)、力量汤 (¥45)   ← 暖橙 #FFB86B，售价从高到低取前 N 个
  [低价推荐] 冷豆腐 (¥14)、豆腐味噌 (¥15)   ← 草绿 #9BE86B，售价从低到高取前 N 个（不与高价重复）
  [高价酒水] 天狗踊 (¥70)、金星 (¥45)       ← 淡紫 #E5A0FF
  [低价酒水] 梅酒 (¥32)                     ← 浅蓝 #7EC8E3，价格读 Sellable.TrueValue/BaseValue
  [推荐酒水] 绿茶                      ← 浅橙 #FFC078，最多 1 杯
  （所有推荐都已按「预算」过滤：超过客人钱包的菜和酒不会出现在这里）
  （块与块之间 15px 间距 + 暗棕分隔线 #73593A）
  星熊勇仪  Yuugi
  ────────────────────────────
  喜好　…
```

多稀客时的排版（2026-11-01 第 3 版，按 5 稀客实测反馈重做）：

- 面板默认**停靠中左侧**（`MiddleLeft`），也可以按住标题栏拖到任意位置，松手即写进 `UI.PositionX/PositionY`，下次启动沿用；
- 高度**按内容动态算**：每行文字都用 `GUIStyle.CalcHeight` 按当前列宽量出真实行高（含折行），再逐行累加，底部固定留 ≥15px，所以最后一行不会贴着边框、更不会被切掉；
- **显示几位由玩家决定**（`Display.MaxGuestsToDisplay`，默认 3）：超出的人折叠掉，标题变成「已显示 3/5 位」，不让玩家以为漏识别；设成 1 面板就只有一位那么高；
- 万一内容仍然比可用高度高（字体调很大 / 拖到屏幕边上），自动切成**单列 + 滚动条**，面板高度永远不超过屏幕，绝不把底部截到屏幕外；
- 每位稀客的推荐**菜品条数 / 酒水条数** 分别由 `Display.MaxRecipesPerGuest`（默认 3）与 `Display.MaxDrinksPerGuest`（默认 2）控制，每项都带售价 `名字 (¥xx)`，放不下自动折行；没有匹配的酒水时整行不显示；
- 先来的稀客排在上面（按 `FirstSeenUtc` 升序），新客接在末尾，老面孔不会被顶下去；
- 每位稀客之间留 15px 间距（`Metrics.Spacing`）+ 一条暗棕分隔线，块与块不会再连在一起；
- 名字下方有一条暗金横线（`RowKind.NameRule`），区块层次一眼能分；
- 没有稀客在场时面板整块隐藏（`SnapshotVisible()` 为空直接 return）；
- 「喜好」标签最多显示 5 个（`GuestTagOverlay.MaxVisibleTags`），多出来的用 `…`；
- 「厌恶」标签读 `SpecialGuest.HateFoodTagOriginal`（`Il2CppStructArray<int>`），用 `DataBaseLanguage.GetFoodTag` 转短名，浅红 `#FF9E9E` 排在「喜好」下面；没有厌恶标签的稀客不占这一行，所以不会多出空白；
- 面板默认宽度收到 320px（`MaxWidth` 360），文字自动换行，横向不会拉太长；
- 稀客之间固定「半个间距 + 分隔线 + 半个间距」，所以魔理沙/红美铃/立空汐之间的上下留白完全一致；
- 极端情况（屏幕特别矮）才会逐档缩字号（每次 -2px，最低 10px），仍放不下才裁客并补「…另有 N 位」；
- 想要两列并排就把 `UI.Layout` 改成 `TwoColumns`（每列宽度 = 屏幕宽×0.62/2 与 `MaxWidth` 取小）；一旦需要滚动会自动回单列。

字体用 `Font.CreateDynamicFontFromOSFont`（Microsoft YaHei UI 等）——IMGUI 默认 Arial 没有中日韩字形。

## 拖动与位置记忆

面板顶部有一条 **「稀客助手 · N 位稀客」标题栏**（比底板略深的暖棕条 + 底部金线）：

- 鼠标**按住标题栏**拖动就能整块挪位，松手时把当前左上角坐标写回 `BepInEx/config/cc.mystia.guesttag.cfg` 的 `UI.UI_X` / `UI.UI_Y`；
- 拖动用 `Event.current.delta` **增量累加**（不是绝对坐标对齐），所以鼠标和面板不会脱节、不会闪；
- 按下瞬间设 `GUIUtility.hotControl`，拖动期间独占 IMGUI 事件；**面板正文不再吞事件**（以前 `Event.current.Use()` 会把游戏底层的点击/光标更新一起掐掉，导致鼠标一上面板就冒出 Windows 白箭头）；
- 光标：鼠标在面板上时，按游戏原本的状态把系统光标压回去（营业时游戏是 `Cursor.visible = false` + 自己画像素光标），离开面板完全不管；同时面板自己画一个 7px 的「暗边 + 白点 + 暖色芯」小指针，万一底层光标没画出来，玩家也知道鼠标在哪；
- 下次启动优先用这两个坐标；想恢复自动位置就把它们改回 `-1`，再按 `UI.Corner` 走；
- 位置始终 clamp 在屏幕内，拖不出界；
- 只有**标题栏**能起拖；正文完全穿透（点击照常落到游戏上）。注意：游戏的场景点击走 EventSystem 而不是 IMGUI，IMGUI 层只能吃到「先到 OnGUI」的那部分事件——拖的时候抓标题栏最稳。

## 推荐菜品：先按喜好过滤，再按售价分高低两档

算法（`RecipeRecommender.Compute`）：

1. **过滤**：候选菜的标签与稀客 `LikeFoodTag` 有交集才留（`命中喜好 ×2 − 命中厌恶 ×3 > 0`）；
2. **排序**：按 `Sellable.TrueValue`（读不到退 `BaseValue`，再读不到当 0）排序；
3. **分组**：高价 = 售价从高到低前 N 个；低价 = 售价从低到高前 N 个，已经在高价档里的不重复（匹配数不足 2N 时能显示几个显示几个，不硬凑）。

N 就是 cfg 的 `Display.MaxRecipesPerGuest` / `Display.MaxDrinksPerGuest`，面板顶部 `[-] [+]` 也能直接改。

### 旧版说明：按喜好标签给菜单打分

候选来源按优先级：

1. **当晚玩家自己选的菜单**：`IzakayaConfigure.Instance.DailyRecipes`（`List<Recipe>`，营业时已填充）
2. 兜底：**全菜谱表** `DataBaseCore.Recipes`（静态 `Dictionary<int, Recipe>`，interop 已核实 `static=True`）

打分（`RecipeRecommender.Compute`）：

```csharp
dishTags = recipe.Food.Tags          // Il2CppStructArray<int>，菜的标签 id
likeHits = |dishTags ∩ guest.LikeFoodTag|       // 喜好标签命中
hateHits = |dishTags ∩ guest.HateFoodTagOriginal| // 讨厌标签命中（×3 扣分）
score    = likeHits * 2 - hateHits * 3           // 只保留 >0
菜名 = recipe.Food.Text.Name                      // LanguageBase.Name，当前语言
```

按分数降序取前 3；结果按 `stringId` 缓存 30 秒（换菜单/换日会变，但不值得每帧重算）。

坑位记录：`SpecialGuest.HateFoodTag` 的类型是 `Il2CppSystem.Collections.Generic.IEnumerable<int>`，
它的枚举器没有 `MoveNext`/`Current`，**不能 foreach**（编译报 `CS0117`/`CS0202`）；
改用 `HateFoodTagOriginal`（`Il2CppStructArray<int>`）即可正常遍历 —— 代码里已加注释。

## 推荐酒水：按酒水喜好标签给酒水单打分

候选来源按优先级：

1. **当晚玩家自己上的酒水**：`IzakayaConfigure.Instance.DailyBeverages`（`List<Sellable>`）
2. 兜底：**全酒水表** `DataBaseCore.Beverages`（静态 `Dictionary<int, Sellable>`）

喜好标签来源（同样优先运行时）：

```csharp
guest.LikeBevTag                                   // Il2CppStructArray<int>，酒水喜好标签 id
DataBaseLanguage.GetBeverageTag(int id)            // 标签 id → 短名（「甘」「无酒精」这种）
// 读不到时退回 GuestTagDatabase 里硬编码的那份（按人设推的）
```

打分比菜品简单（`SpecialGuest.HateBevTag` 是 `IEnumerable<int>`，同样不能 foreach，本版先不用它）：

```csharp
drinkTags = drink.Tags                       // Il2CppStructArray<int>
likeHits  = |drinkTags ∩ 喜好酒水标签|
score     = likeHits * 2                     // 只保留 >0
酒名 = drink.Text.Name                        // LanguageBase.Name，当前语言
```

按分数降序取前 3；一个都匹配不上时返回空数组，面板那一行显示「推荐酒水　无匹配」。
结果同样按 `stringId` 缓存 30 秒。

## 运行要求：默认 D3D11 即可（2026-11-02 复核，`-force-vulkan` 不再是必需）

游戏本体在 **D3D11** 下启动即崩（`APPCRASH`，故障模块 `UnityPlayer.dll`，`c0000005`，偏移 `0x5501e0`），
日志里先出现三条 `The resource Internal-ErrorShader.shader could not be loaded from the resource file!`；
禁用 BepInEx（`winhttp.dll` 改名）后同样崩，Steam「验证文件完整性」通过、`StateFlags=4`、无缺文件 —— 
与本插件无关，是游戏/图形驱动这条链路的问题。

当时的结论是「必须 `-force-vulkan`」，**后来被推翻了**：2026-11-02 复核发现 Steam 在 13:37:25 校验时补回了 6 个缺失文件（含 `Touhou Mystia Izakaya_Data/Resources/unity default resources`），补回之后 D3D11 直接能跑（`Direct3D 11.0 [level 11.1]`，进游戏→营业无异常，插件 1.2.0 正常加载）。`-force-vulkan` 现在只是崩溃时的兜底选项。

## 独立运行：这个插件不依赖 DSH / 我这边的开发环境

结论先写前面：**玩家只需要把 `MystiaGuestTag.dll` 放进 `BepInEx\plugins\`，从 Steam 正常启动游戏即可**，
不需要 DSH、不需要后台脚本、不需要本仓库里的任何其它文件。

已核对的事实：

| 检查项 | 结果 |
| --- | --- |
| 插件源码里的绝对路径 / 文件读写 / 进程启动 / 网络 | 0 处（`grep` 过 `File.`、`Directory.`、`Path.`、`Environment.`、`Process.Start`、`HttpClient`、`E:\\`） |
| 编译进 DLL 的程序集引用 | 只有游戏/BepInEx 自带：`BepInEx.Core`、`BepInEx.Unity.IL2CPP`、`0Harmony`、`Il2CppInterop.Runtime`、`Assembly-CSharp(-firstpass)`、`DEYU.Singletons`、`UnityEngine.*`、`Il2Cppmscorlib/System.Core`；`System.Runtime/Linq/Collections/Threading` 属框架程序集，由游戏自带 coreclr 提供 |
| 运行时产物 | 只有 `BepInEx\config\cc.mystia.guesttag.cfg`（配置，游戏目录内）和 `BepInEx\LogOutput.log`（日志） |
| 与外部进程的通信 | 无（没有 socket / pipe / 环境变量依赖） |

`csproj` 里的 `GameDir` 只是**编译期**的引用路径（由 `-p:GameDir=` 或环境变量 `TOUHOU_GAME_DIR` 提供），不会写进 DLL。

### 已经拆掉的「看起来像被 DSH 绑住」的东西

1. **BepInEx 控制台窗口**：以前每次启动都会弹一个黑色控制台（`[Logging.Console] Enabled = true`）。
   **那个窗口一关，游戏进程就一起退出** —— 这最像「关掉页面后游戏闪退」的元凶。
   已改成 `Enabled = false`（备份：`BepInEx/config/BepInEx.cfg.bak-20261101-console`），日志照旧写 `BepInEx/LogOutput.log`。
2. **开发路径的启动参数**：之前调试时用 `-logFile <工程目录>/_tools/player_vk3.log` 启动。
   现在只需要 `-force-vulkan`，不再指向任何开发目录。
3. **后台监控脚本**：之前有两个 PowerShell 后台任务在轮询日志、给游戏窗口截图（`PrintWindow`）。
   已全部停掉；插件本身不依赖它们。

## 纯手工启动验证步骤（关掉 DSH 页面之后照样能用）

1. 关掉这个对话页面（以及所有 DSH 窗口），确认后面这些进程都不在了：
   任务管理器里搜 `Touhou Mystia Izakaya.exe` —— 应该没有；有就结束掉，从干净状态开始。
2. 不需要启动选项；只有在 Steam 校验后仍闪退时，才填 `-force-vulkan` 兜底。
   （这台机器不加会在标题前崩，原因见上一节）。其余参数一个都不要加。
3. 从 Steam 点「开始游戏」（或库里的绿色按钮）。
   正常表现：任务栏只有游戏一个窗口，**不再有黑色控制台窗口**。
4. 进标题 → 读取存档 → 走完白天 → 21:00 自动进入营业（也可以直接在白天准备完后开营业）。
5. 稀客进店后，屏幕**右下角**（默认停靠位）出现半透明黑框面板：
   - 第一行：彩色加粗的稀客名字 + 灰色 stringId
   - 第二行：`喜好　甜 / 猎奇 / 生 / 肉`
   - 第三行：`推荐　<菜名>、<菜名>`
   - 第四行：`推荐酒水　<酒名>`（无匹配时是「无匹配」）
   多稀客时面板向上生长；放不下会先缩字号、再把最早的折叠成「…另有 N 位」。
6. 想确认插件本身没出错：`<游戏目录>\BepInEx\LogOutput.log` 里应有
   `Loading [Mystia Guest Tag 0.3.0]`、`已挂载稀客生成 Hook`、`已挂载面板绘制点`、`已就绪` 四行；
   有稀客进店时还会追加 `发现未收录稀客：...` 或 `为稀客 "..." 推荐酒水（…）`。
7. 如果第 3 步就闪退：先看 `LogOutput.log` 最后一行停在哪。
   停在 `Chainloader initialized` 之前 = 加载器/游戏本体问题；停在插件某一行之后再闪退 = 插件问题，把日志发我。

## 验证步骤

1. `bash tools/deploy.sh`
2. 启动游戏 → `BepInEx/LogOutput.log` 里应出现：
   - `[GuestTag] 已挂载稀客生成 Hook: ...SpawnSpecialGuestGroup(...)`
   - `[GuestTag] 已挂载面板绘制点: NightScene.SceneManager.OnGUI()`
   - `[GuestTag] 面板字体已加载: ...`
3. 进入任意一晚营业，稀客进店后角落出现面板；面板里每个稀客最多四行：名字、喜好、推荐（菜品）、推荐酒水。
   日志里会有 `为稀客 "..." 推荐酒水（喜好 …，候选来自当晚酒水单，共 N 款）：…` 一行；
   当晚酒水单里一杯都不匹配时，那一行显示「推荐酒水　无匹配」。
4. 2026-11-01 实测（`-force-vulkan`，兽道雀食堂营业中）：
   - 面板字体加载：`[GuestTag] 面板字体已加载: Microsoft YaHei UI`
   - 连续识别到进店稀客：`Marisa`(Id=10)、`MengChengGuo`(Id=30)、`Ushio`(Id=37)、`Chen`(Id=2)
   - 说明 Hook 与面板链路在真实营业里全部走通。
5. 若有稀客连运行时标签都读不到，日志里会有
   `[GuestTag] 发现未收录稀客：stringId="..." Id=... 本地化名="..."`，把该 stringId 补进 `GuestTagDatabase.Table` 即可。

## TODO

- `// TODO: 确认 Hook 点/字段`：`SpawnManualControlledSpecialGuestGroup`（DLC/剧情受控稀客）未挂 Hook，
  若剧情稀客需要显示，同样在该方法 postfix 里 `TryCast<SpecialGuestsController>()` 即可。
- 已验证的 stringId ↔ Id 对照（可直接抄进表）：`Marisa`=10、`Chen`=2、`MengChengGuo`=30、`Ushio`=37；
  三个目标稀客（Wriggle / Akyuu / Rumia）的 `Id` 仍未在实机里撞到，靠 stringId 匹配已足够。
- 推荐菜品只做「标签命中」打分，没有考虑菜价、料理台占用、食材库存；要更准可以在 `RecipeRecommender.Compute` 里加权。
- `IzakayaConfigure.DailyRecipes` 为空时（读档瞬间/非营业）会自动退回全菜谱表，日志里会写候选来自哪一边。

## 稳定性与内存安全（v0.13.0）

结论先说：**用户态插件不可能让 Windows 蓝屏**。蓝屏（BugCheck）只由内核态代码（驱动/内核模块）触发；插件里的越界读最多让游戏进程自己收到 `0xc0000005` 并退出，系统日志里是 `Application Error`，不会出现 `BugCheck`。真蓝屏会在 `C:\Windows\Minidump` 留 dump、并在 System 日志写 `Microsoft-Windows-WER-SystemErrorReporting`，最常见元凶是显卡驱动。

即便如此，v0.13.0 把「读游戏内存」这一步全面收紧：

| 位置 | 措施 |
| --- | --- |
| `RecipeRecommender.ReadPrice` | 自定义价格（纯托管）→ `Pointer` 存活检查 → `TrueValue`（独立 try/catch）→ `BaseValue`（独立 try/catch）→ `0`；负数/超 1e6 的值直接丢弃 |
| `RecipeRecommender.ReadName` | 先查 `sellable` 与 `sellable.Text` 的 `Pointer`，再读 `Name` |
| `RecipeRecommender.ReadTagArray` / `ReadTagNames` | 先取 `Length` 并限制 1~256，`Length` 读取本身也 try/catch；读到一半失败返回已读部分 |
| `SpecialGuestTracking.Record` | `guest.Pointer == IntPtr.Zero` 直接 return（native 对象已销毁的情形） |
| `SpecialGuestTracking.RefreshPresentIfDue` | 0.5 秒最多扫一次在场名单（原来每个 OnGUI 事件都扫，一帧好几次） |
| `GuestTagOverlay.Draw` | 只有 `EventType.Repaint` 才排版绘制；非 Repaint 事件只跑拖拽命中测试（用上一帧矩形），不影响手感 |

排查用的脚本：`_tools/bsod_check.ps1`（查 41/1001/6008 与 BugCheck）、`_tools/bsod_check2.ps1`（查 4101/14/17 驱动事件、进程状态、近期应用崩溃）。2026-11-01 15:30 实测：系统自 10:22:16 起未重启，最近 2 天无 41/1001/6008、无 `WER-SystemErrorReporting` 记录，`C:\Windows\Minidump` 里只有一个 2025-09-28 的旧 dump —— 也就是说**这次没有发生蓝屏**；14:42:35 的 `RADAR_PRE_LEAK_64` 是 WER 对游戏进程的「内存泄漏预警」，属于用户态事件。

## 游戏内交互（v0.16.x）

标题栏下面那一条就是全部操作入口，不用记快捷键：

| 操作 | 效果 |
| --- | --- |
| 点 `显示 3` 左侧的 `[-]` / 右侧的 `[+]` | 面板最多显示几位稀客（1~10），点一下立刻少/多显示一位 |
| 点 `菜品 3 [-] [+]` | 每位稀客「高价推荐」「低价推荐」各列几条（1~5） |
| 点 `酒水 2 [-] [+]` | 每位稀客「高价酒水」「低价酒水」各列几条（1~5） |
| 鼠标在面板上滚轮 | 在场稀客多于「显示数量」时翻页，标题变成 `1-3/5 位 · 滚轮翻页` |
| 按住标题栏拖动 | 整块面板跟手移动，松手后坐标写进 `UI.UI_X` / `UI.UI_Y` |
| `F10` | 一键隐藏/显示整个稀客面板（紧张做菜时清屏用） |
| `F9` | 备用：打开滑条版设置面板（不常用，按钮就够） |

实现要点：
- 加减按钮是 IMGUI 的 `GUI.Button`，命中矩形在 `EventType.Repaint` 时缓存到 `_stepperHits`；`HandleDrag` 里先判 `overStepper`，命中就让拖拽让道——所以点按钮不会把面板拖走。
- 点一下直接 `GuestTagConfig.ApplyDisplaySettings(...)`（改 `ConfigEntry.Value`，内存立即生效）→ `ConfigFile.Save()` 落盘 → `RecipeRecommender.ClearCache()` 让条数变化下一帧就重算。
- 显示数量的增减会重置 `_windowOffset`，避免停在空页。
- 翻页用 `_windowOffset` 窗口切片：稀客总数不变、只换可见区间，所以「先来的在上」的排序不会被翻页打乱。


## 客人预算（v1.0.0）：只推他买得起的

- **数据来源**：`NightScene.GuestManagementUtility.GuestGroupController.MaxFundCarry`（int，这一顿最多能掏出多少钱）。
  读不到或 ≤0 时退回 `GuestGroupController.GetFund`；两者都失败=0，插件按「不限预算」处理，不会把推荐全清空。
- **显示**：名字行后面直接挂 `预算 ¥784`（暗金 #FFD98A）。
- **过滤**：`[高价推荐] / [低价推荐] / [高价酒水] / [低价酒水]` 生成前先剔掉 `price > 预算` 的条目，
  再按价格排序拆分，所以面板上不会出现他付不起的菜——上错价位他会当场黑脸走人。
- **过滤后为空**：这一行直接不显示（不写「无匹配」占位），面板自动变短。
- **缓存**：推荐缓存 key 带上了预算（`stringId@budget` / `bev:stringId@budget`），预算一变立刻重算。

## 客人离店即时移除（v1.0.0）

- 之前是「最后一次看到后 90 秒才过期」，稀客走了面板还会挂着他。
- 现在 `SpecialGuestTracking.RefreshPresent()` 每 0.5 秒扫一次 `GuestsManager.AllPresentedGuestGroupController`，
  把这一轮还活着的稀客收集成 `alive` 集合，再 `PruneAbsent(alive)` 把不在集合里的立刻从展示表删掉
  （刚落座 2 秒内的先留着，避免生成 Hook 已登记、名单还没刷新的空档把他闪掉）。
- 全场清空时展示表为空，面板自动隐藏。

### 推荐策略（v1.1.0）

- **三种策略**：`Display.Strategy` 存 `PriceLow` / `PriceHigh` / `FewIngredients`，面板顶部 `策略: … ▸ 点击切换` 整行可点，循环切换并立刻写回 cfg + 清缓存重排。
- **不再拆高低价**：`RecipeRecommender.Rank(scored, take, beverage)` 按策略统一排序（价格升序 / 价格降序 / `Recipe.Ingredients.Length` 升序再价格升序），取 `MaxRecipesPerGuest` / `MaxDrinksPerGuest` 条，前缀随策略显示 `[低价优先推荐]` / `[高价优先推荐]` / `[少料易做推荐]`。酒水没有食材概念，少料易做下按低价优先处理并如实标注。
- **缓存 key 带策略**：`stringId@budget#strategy`，切策略必须重算。
- **预算照旧**：排序前先剔掉超过 `GuestGroupController.MaxFundCarry` 的条目。

### 最后一轮微调（v1.0.0）

- **滚轮循环**：`HandleWheel` 只认方向、步长恒为 1，`_windowOffset = ((_windowOffset + step) % span + span) % span`——滚到底再滚回顶部，滚到顶再滚跳到底部，也不会因为一次 `delta.y = ±2` 一下翻两位。
- **合并档也守条数**：`SplitByPrice` 的合并分支（匹配数 ≤ 阈值）按价格从高到低 `Take(mergeLimit)`，`mergeLimit = Math.Max(1, Math.Min(perGroup, hardMaxHigh + hardMaxLow))`，不会超过顶部「菜品」设定。
- **离店即时移除**：`PruneAbsent` 配合 `PresentGrace = 2s`，稀客一走那一块立刻消失，全员离店面板自动隐藏。

### v1.4.0：查表法 + 稳定性重写（用户 m03044 / m03065）

- **查表优先**：`CuratedRecommendations.cs` 纯内存硬编码（`Dictionary<string, Entry>` + `Aliases`），
  每位稀客给 `HighFood / LowFood / FewFood / HighDrinks / LowDrinks` 五组名单；`ResolveCurated` 把菜名换成
  `Sellable.TrueValue` 真实售价、按预算过滤，`priced == 0`（一个价都查不到）时返回 `NoMatch` 自动退回动态计算。
- **彻底无 JSON**：v1.3.0 的 JSON 模板 + `MiniJson` 解析器整份删掉，运行期零文件 IO（这是崩溃怀疑点）。
- **降频**：`SpecialGuestTracking.RefreshIntervalSeconds` 0.5 → **2 秒**，进店瞬间由 `SpawnSpecialGuestGroup` 的 postfix 先记一笔。
- **GC**：`GuestTagOverlay` 加 `LineCache`，`FormatTags/HateTags/Recommendations/Beverages` 走 `CachedLine(..., ReferenceEquals(arr))` 缓存，数据不变就不重建富文本。
- **防御**：`ComputeWithCuratedTable` 外层 try-catch（出错记 Warning + `NoMatch`）；所有 IL2CPP 读取先判 `Pointer == IntPtr.Zero`。
- **目录清理**：删掉 `BepInEx/plugins/BepInEx/plugins/GuestTag.dll` 这个解压嵌套层，避免 BepInEx 同时看到 1.0.0 与新版。
- 发布包：`dist/GuestTag-v1.4.0.zip`（40,401 B，`BepInEx/plugins/GuestTag.dll` 77,312 B + `README.md`）。

### v1.5.0：低价优先改成「微利区间」（用户 m03142）

- **不再选全库最便宜**：`PriceLow` 先把候选收进「预算的 10%~30%」（`LowBandMinRatio = 0.10` / `LowBandMaxRatio = 0.30`）；
  区间里没有菜时，用 `CollectNeutralFallback` 放宽标签再找一次（`source += "（含放宽标签的微利菜）"`）；
  还是没有、且客人预算 > `RichGuestBudget = 200`，至少把 ¥`RichGuestMinPrice = 20` 以下的菜剔掉。
- **高价优先简化**：去掉 100~200 的档位偏好，直接 `OrderByDescending(Price)` 取预算内最贵的（能卖 ¥300 就卖 ¥300）。
- **查表也要守区间**：`ResolveCurated` 里若名单菜价落在低价区间外（预算 ¥557 推 ¥14 那种），直接 `continue`，
  让动态计算重新挑，避免「查表把低价优先锁死成十几块」。
- **酒水同规则**：`ComputeBeverages` 加同一段区间过滤。
- 发布包：`dist/GuestTag-v1.5.0.zip`。

### v1.6.0：标签不降级 + 高价池放宽 + 字号回调（用户 m03216）

- **标签不再合并降级**：删掉 `Rank` 里「最高价不到 60 就改写成 `[推荐菜品]`」的分支；点了哪个策略就永远显示 `[高价优先推荐]` / `[低价优先推荐]` / `[少料易做推荐]`，哪怕只剩一个菜。
- **高价优先真的去挑大菜**：PriceHigh 分支先把候选放宽成「不带厌恶标签」的整张菜单（当晚菜单 + 全菜谱表，按名字去重、保留完全匹配项的高分），再只接受 `Price >= PremiumBandLow(100)`；没有就退 `>= HighPriceFloor(60)`；再没有就 `return NoMatch`（不再硬塞二十几块的小菜）。
- **字号回调**：`GuestTagConfig.FontSize` 默认 18 → **16**（cfg 同步改），面板整体小一号。
- 发布包：`dist/GuestTag-v1.6.0.zip`。

### v1.7.0：高价优先读整本菜谱 + 超大菜档（用户 m03248）

- **不是阈值写死，是池子太窄**：之前高价模式只在「当晚菜单」（`IzakayaConfigure.Instance.DailyRecipes`，玩家自己配的几道菜）里放宽，菜单里最贵的只有 ¥105 的大奢宴，自然见不到 200+。价格本身来自 `Sellable.TrueValue`，没有上限（同一天日志里酒水能到 ¥400 的「晓」）。
- **池子扩展**：PriceHigh 分支现在把 `DataBaseCore.Recipes`（整本菜谱 = 玩家能做的所有菜）也扫进候选，只要求「不带厌恶标签、价格 > 0、不超预算」。
- **三档阈值**：`SuperPremiumFloor = 150` → `PremiumBandLow = 100` → `HighPriceFloor = 60`，逐档下降；全都没有就 `NoMatch`（面板显示无匹配），不硬塞便宜菜。
- **诊断日志**：新增 `DumpPriceLadderOnce`，开局第一次计算时打两条 `LogInfo`——`当晚菜单最贵的 8 道：…` / `整本菜谱最贵的 8 道：…`，直接看出插件读到的价格阶梯（想确认有没有高级菜就看这两行）。

### v1.8.0：候选池限制在「现在真能做」（用户 m03275）

- **不再读整本菜谱**：删掉 high 分支里的 `CollectAllRecipes()` 兜底；候选池 = `IzakayaConfigure.Instance.DailyRecipes`（当晚菜单）。
- **食材/备菜双重校验**：新增 `ReadPantrySnapshot()`（`GameData.RunTime.Common.RunTimeStorage.Ingredients` → `Dictionary<int,int>` 拷贝）、`ReadPreppedSnapshot()`（`RunTimeStorage.Foods` → 菜名集合）、`IsCraftableNow(recipe, pantry, prepped)`（备菜栏命中直接 true；否则 `Recipe.Ingredients` 每种食材需求量 ≤ 库存；库存读不到时返回 true 不乱剔除）。
- **自动降级**：`craftable.Count < candidates.Count` 时把候选换成可做的子集，`downgraded = true`；高价档位在可做子集里按 ≥150 → ≥100 → ≥60 逐档退，`Rank(..., labelSuffix)` 把 `（食材不足，已降级）` 拼到标签后面。
- **备菜优先**：高价档选中后，若 `prepped` 里有其中的菜，只保留这些已做好的。
- **诊断**：每次算推荐前打一条 `候选池：菜单 X 道，现在能做 Y 道（库存 Z 种食材，备菜 N 道）`（Debug 级）。

### v1.9.0：酒水匹配重做（用户 m03303）

- **库存过滤**：新增 `ReadBeverageStockSnapshot()`（`GameData.RunTime.Common.RunTimeStorage.Beverages` → `DataBaseCore.Beverages` 名称集合），候选只保留吧台里真有的酒；读不到就退回 `DailyBeverages` / 全表。
- **高价优先不再要求命中喜好**：`foreach (drink)` 里 `if (hits == 0 && strategy != PriceHigh) continue;`，贵酒即使标签不完全对口也进池。
- **三档取最贵**：`SuperPremiumFloor(150)` → `PremiumBandLow(100)` → `HighPriceFloor(60)`，都没有就 `OrderByDescending(Price).Take(1)` 认命取最贵那瓶；`Rank` 的 PriceHigh 分支是 `OrderByDescending(Price)`，`Take(N)` 拿到的就是最贵的前 N 瓶。
- **预算按单件**：一直只比较单瓶价格，从不把菜价和酒价相加。

### v1.10.0：酒水按「杯数」给多款（用户 m03317）

- 新增 `RankBeverages(List<(int Score, string Name, int Price, int Ingredients)> scored, int take, Dictionary<string,int> stock)`：高价模式先挑 `Price >= PremiumBandLow(100)` 的酒按价格降序填满 `take`，不够再按价格降序补；低价/少料模式按价格升序 + `stock[name]` 降序；全程 `HashSet<string> names` 去重，绝不重复同一瓶。
- `ReadBeverageStockSnapshot()` 返回 `Dictionary<string,int>`（酒名 → 瓶数），「便宜但库存多」的优先。
- `Display.MaxDrinksPerGuest` 默认 2 → **3**（客人一晚会点好几杯）。
- 面板里多款酒用 `、` 连接并带价格，`wordWrap = true` + 逐行 `CalcHeight` 量高，不会挤成一行。

### v1.10.1：价格拼接修复（用户 m03354）

- 现象：`[高价优先酒水] 水獭祭、天狗踊 (¥70)`——第一瓶没价格。
- 根因：`FindBeveragePrice()` 只在 `CollectBeverageCandidates()`（当晚酒水单）里找，没备上台的贵酒查不到价；`ResolveCurated` 里 `price <= 0` 又走 `text = name` 分支裸显示。
- 修法：`FindBeveragePrice()` 拆成 `FindBeveragePriceIn(List<Sellable>, name)`，先查当晚单、再查 `DataBaseCore.Beverages` 全表；`ResolveCurated` 里 `price <= 0` 直接 `continue` 并 `LogCuratedNoPriceOnce` 记一次；菜品/酒水动态打分也各自加 `ReadPrice(...) <= 0 → continue` 的门槛。

### v1.10.2：候选池彻底重写（用户 m03399）

- `CollectCandidates()` 三级：`IzakayaConfigure.DailyRecipes` → `RunTimeStorage.Recipes`（新增 `CollectUnlockedRecipes()`，`Dictionary<int,Recipe>.TryGetValue(id)`）→ `DataBaseCore.Recipes`；不再一上来就用全库。
- 食材闸门收紧：`pantry.Count > 0 || prepped.Count > 0` 时**严格**只留 `IsCraftableNow()` 的菜，池子空就 `NoMatch`（不再「保留原菜单」）。
- 放宽兜底：`scored.Count == 0` 时遍历候选池，给「不带厌恶标签 + 价格读得到」的菜（`score = 1`），`source += "（菜单里没有更合口味的菜，已放宽标签）"`，冴月麟这类不再显示无匹配。
- 查表名单加闸门：`ResolveCurated(..., HashSet<string>? allowed)`，`CollectAvailableFoodNames()` / `CollectStockedBeverageNames()` 提供白名单，名单里不在白名单的条目 `LogCuratedNotInMenuOnce` 后跳过。
- `PriceHigh` 完全绕开名单，直接动态排序（`Rank` 的 `OrderByDescending(Price)`）；其它策略名单命中后用动态结果补满 `MaxRecipesPerGuest` 条。

### v1.11.0：候选池只认今晚菜单 / 食材能做的菜（用户 m03461）

- `CollectCandidates()`：`DailyRecipes` → 「背包食材能做的菜」→ 空；**删掉已解锁菜谱那一档**，也不再拿全库当候选。
- `IsCraftableNow()` 收紧：`pantry.Count == 0` 返回 false（只认 `prepped` 已备好的菜）；`recipe.Ingredients` 为空或读异常返回 false——「读不到」不再当作「能做」。
- `Compute()` 食材闸门：严格模式下 `craftable.Count == 0` 时**保留原菜单**并打日志（避免食材表读不全导致整片「无匹配」）；否则只留 `craftable`。
- `CollectAvailableFoodNames()` 直接用候选池菜名（含备菜），不再二次过滤，保证查表白名单不会因为食材检查变空而失效。

### v1.11.1：菜单全便宜时不再「无匹配」（用户 m03500 实测日志）

- 日志实证：`候选池：今晚菜单 7 道，现在能做 7 道（库存 53 种食材）` + `高价优先：预算 ¥400 内没有 ¥60 以上的菜，返回空`。
- 也就是说池子没问题、食材也没问题，是「高价优先」在小菜单上把整张表判空。修法：`expensive.Count == 0` 时改推 `pool.OrderByDescending(Price).ThenByDescending(Score)`，日志打「改推池子里最贵的几道（最高 ¥xx）」；只有 `pool` 本身为空才返回 `NoMatch`。

### v1.12.0：候选池 = 已解锁 ∪ 今日菜单，逐道验算背包食材（用户 m03535）

- `CollectCandidates()` 只从 `CollectDailyRecipes()` + `CollectUnlockedRecipes()` 建池（按 `recipe.FoodID` 去重），**删掉了「退回全菜谱表」的兜底**——那条正是神仙菜的入口。
- 新增 `CountCraftable(recipe, pantry, prepped)`：备菜栏命中按份数；否则 `min(库存 / 每份需求)`，缺一样返回 0；`pantry` 与 `prepped` 都为空时才退化成「菜单/已解锁直出」并打日志。
- 新增静态 `_craftCounts`（菜名 → 份数），`FormatItem(name, price)` 在份数 >1 时追加 ` ×N`。

### v1.13.0：高价优先不再只推一道（用户 m03565）

- 菜品：删掉「≥¥150 一档 → ≥¥100 → ≥¥60」的硬分档（分档会在只有一道贵菜时把列表压成 1 条），改成整池 `OrderByDescending(Price).ThenByDescending(Score)` 后 `Take(Math.Max(3, GetMaxRecipes()))`。
- 备菜栏 `ready` 只做「排前面」的加权，不再 `expensive = readyNow` 把列表截断。
- 酒水：同一套改法，`Take(Math.Max(3, GetMaxDrinks()))`，删掉「一整柜都便宜就只取最贵一瓶」的 `Take(1)`。

### v1.13.1：今日菜单不再被「缺调料」整道踢掉（用户 m03599）

- `CollectCandidates()` 先用 `CollectDailyRecipes()` 建 `dailyIds`；`CountCraftable() <= 0` 但 `recipe.FoodID` 在 `dailyIds` 里时，仍加入池子并记 `_craftCounts[name] = 1`（菜单是玩家自己挑的，店里有食材就能做）。
- `CountCraftable()` 先调 `recipe.GetCookCountNum()`（`M System.String GetCookCountNum()`），抽取数字作为份数；异常/无数字再按 `RunTimeStorage.Ingredients` 手算。
- 高价优先分支新增日志：`高价池：可做 N 道、预算 ¥x 内，取前 K 道：…`。

### v1.14.0：酒水库存硬过滤 + 价格/数量同排（用户 m03632）

- `ComputeBeverages` 里 `stocked.Count > 0` 时严格只留 `stocked[name] > 0` 的酒；`inStock.Count == 0` 直接 `NoMatch`（以前会退回去推没库存的酒）。
- `RankBeverages` 排序加库存权重：高价 `OrderByDescending(库存>0).ThenByDescending(价格).ThenByDescending(库存)`；低价 `OrderByDescending(库存).ThenBy(价格)`。
- 显示改 `FormatPricedItem(name, price, count)` → `(¥130/x2)`；菜品份数来自 `_craftCounts`，酒水瓶数来自 `_beverageStockSnapshot`（`ResolveCurated` 也走同一格式）。

### v1.0.0 正式版：标题显示场上总人数（用户 m03657）

- `Draw()` 里先存 `_totalGuests = guests.Count`（截断前），三处 `TryBuildLayout(..., limit, _totalGuests)` 都传真实总数。
- `TryBuildLayout` 里 `var total = Mathf.Max(totalGuests, guests.Count);`，标题：截断时 `已显示 {off+1}-{off+shown}/{total} 位 · 滚轮翻页`，不截断时 `{total} 位稀客`。

### v1.1.0 厨具限制（m03688）

- 新增 `CookwareFilter.cs`：`UnityEngine.Object.FindObjectsOfType<CookController>()` → `controller.Cooker.Type` 收集台上厨具种类（2 秒缓存），`IsAvailable(CookerType)` 判定；扫不到时读 cfg `[Cookware] Override`。
- `RecipeRecommender.CollectCandidates()` 在候选池建好后加厨具闸门：`pool.RemoveAll(r => !CookwareFilter.IsAvailable(r.CookerType))`，日志「厨具闸门：台上 煮锅、烤架，剔除 N 道」。
- 游戏里 `Recipe` 只有 `CookerType`，没有「厨具等级」字段；`Cooker` 的 `Series`（Base/Sparrow/…/Trinity）是厨具本身的档次，不影响能否做某道菜。

### v1.1.0 动态兼容性（m03688）

- `SpecialGuestTracking.Record` 改成运行时标签优先：`var liveTags = GetOrReadLiveTags(...); var tags = liveTags ?? (known ? entry.Tags : null);`，酒水同理由 `liveTags != null ? null : entry.BeverageTags`。
- `RecipeRecommender.ComputeWithCuratedTableCore` 的查表分支加 `GuestTagConfig.UseCuratedHints.Value`（默认 false），默认纯动态。
- 未知稀客：`FormatRecommendationsRaw` 的 `!HasFoodPreference` 分支改成 `<color=#FFB86B>[新稀客] 数据未收录</color>`。
