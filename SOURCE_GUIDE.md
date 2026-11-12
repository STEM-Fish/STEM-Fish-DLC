# STEM Fish DLC — 源码包说明

> 适用版本 **v0.15.1**
> 游戏：Casualties Unknown Demo（Unity 2022.3.62 Mono）
> 框架：BepInEx 5.4.23.4 + 0Harmony 2.9.0

---

## 0. 一句话

这是一个 **BepInEx 5 插件**，给单机版《Casualties Unknown》加一条自己的剧情线（STEM-Fish 实验体）、一套自制装备、一个游戏内终端，以及一组可选的难度/机制改动。

**所有改动必须在源码包里做。** 直接改 `BepInEx/plugins/` 下的文件会被下一次「重编译 + 部署」覆盖掉。

---

## 1. 目录总览

```
STEM_Fish_MOD/
├─ STEMFishDLC/              ★ 插件源码（主工程）
│  ├─ *.cs                   44 个源文件，约 13,100 行
│  ├─ STEMFishDLC.csproj     工程文件
│  ├─ deploy.sh              一键构建 + 部署
│  ├─ items.zh-CN.json       物品名/描述（运行时读）
│  ├─ voice.zh-CN.json       台词库（运行时读）
│  ├─ lore.zh-CN.json        档案正文（运行时读）
│  ├─ LORE_BRIEF.md          世界观设定稿（游戏不读）
│  ├─ PLAN.md                功能设计/开发计划（游戏不读）
│  ├─ textures/              物品图标、准星、人体图（运行时读）
│  ├─ lore_img/              档案配图（运行时读，见 4.4）
│  ├─ skin/                  皮肤包（deploy.sh 会同步到 CustomSprites/）
│  ├─ textures_raw/          原始素材（不参与构建，可删）
│  ├─ textures_raw_mdr/      MDRZ9 高清原图（不参与构建，可删）
│  └─ _backup_20261107/      文本改动前的备份
├─ patcher/                  ★ BepInEx 预加载器源码（MOD 隔离功能）
├─ tools/
│  ├─ cu_scan/               Mono.Cecil 反汇编工具（查游戏内部结构用）
│  ├─ imgproc/               像素图工具（make / stats / pixelate / profile）
│  ├─ dollgen/               人偶图集生成器（BFS 部位分区 → doll_atlas.png + 分区预览）
│  ├─ voicemerge/            把 voice_template.json 的缺失键合并进 voice.zh-CN.json
│  ├─ voicepatch/            按 key 覆盖台词（patch.json 里只写要改的键，其余原样保留）
│  └─ art/                   像素图规格 / 人偶分区预览图
└─ CU_ServerPilot/           另一个 MOD（联机服务器管理，与 DLC 无关）
```

---

## 2. 构建与部署

### 构建

```bash
cd "C:/Users/huang/Desktop/STEM_Fish_MOD/STEMFishDLC"
"/c/Program Files/dotnet/dotnet.exe" build -c Release
```

产物：`bin/Release/netstandard2.1/STEMFishDLC.dll`

> `dotnet` 不在 PATH，必须写全路径；裸 `dotnet` 会被本机安全策略拦。

### 部署

```bash
SRC="C:/Users/huang/Desktop/STEM_Fish_MOD/STEMFishDLC"
DST="D:/Steam/steamapps/common/Casualties Unknown Demo/BepInEx/plugins"
cp "$SRC/bin/Release/netstandard2.1/STEMFishDLC.dll" "$DST/STEMFishDLC.dll"
cp "$SRC"/textures/*.png "$DST/STEMFishDLC/textures/"
cp "$SRC"/items.zh-CN.json "$SRC"/voice.zh-CN.json "$SRC"/lore.zh-CN.json "$DST/STEMFishDLC/"
```

游戏关闭时部署，改完重启游戏。

### 工程依赖（csproj）

| 引用 | 来源 | 说明 |
|---|---|---|
| `UnityEngine.Modules` | NuGet（源 nuget.bepinex.dev） | `IncludeAssets=compile` |
| `BepInEx.dll` / `0Harmony.dll` | `游戏/BepInEx/core/` HintPath | `Private=false`，不打包 |
| `Assembly-CSharp.dll` | `游戏/CasualtiesUnknown_Data/Managed/` | 要 patch 游戏自己的类就必须引用 |
| `UnityEngine.UI.dll` | 同上 | uGUI |
| `Newtonsoft.Json.dll` | 同上 | 解析文本 JSON |

---

## 3. 源文件逐个说明

### 3.1 入口与驱动

| 文件 | 作用 | 关键点 |
|---|---|---|
| `Plugin.cs` | BepInEx 入口。`[BepInPlugin]` + `Awake()`，按顺序初始化所有模块，`Safe()` 逐模块包异常 | **一个模块抛异常会中断整个 `TickAll`**，所以每个调用都套 `Safe` |
| `EngineTick.cs` | 引擎级每帧驱动：`Application.onBeforeRender`（主）+ `Camera.onPreRender`（备） | 不依赖任何 MonoBehaviour，插件对象被销毁也能跑 |
| `TickRunner.cs` | 可重建的每帧加速器，`sceneLoaded` 之后创建 | 提前 `DontDestroyOnLoad` 会被第一个场景加载清掉，所以延迟建 |
| `FishLog.cs` | 独立 `Logger.CreateLogSource` + 文件后备 | 插件对象 Dispose 时日志源不死 |

### 3.2 状态与配置

| 文件 | 作用 |
|---|---|
| `ModState.cs` | MOD 启用/停用、调试工具开关、MOD 隔离（写预加载器配置）。隔离必须重启才生效 |
| `ItemText.cs` | 物品名/描述的**外部 JSON 表**（`items.zh-CN.json`），缺 key 时退回代码内置名 |
| `FishLocalization.cs` | 把自制物品的名字注入游戏的 `Locale` 系统 |

### 3.3 物品与装备

| 文件 | 作用 | 关键点 |
|---|---|---|
| `ItemRegistry.cs` | **最核心的物品表**。定义 8 件物品，`Create()` 克隆原版模板 → 改 id/贴图/参数 | 所有数值（护甲、容量、枪械参数）都在这里 |
| `FishAssets.cs` | PNG → `Sprite`，PPU/轴心沿用模板图标 | 贴图放 `textures/<物品id>.png` 自动生效 |
| `ItemCollider.cs` | 把碰撞箱收到贴图的**不透明轮廓**上 | 点了才有手感，不然是方块判定 |
| `SpriteTint.cs` | 运行时贴图去色/压暗（黑色双肩包用） | |
| `SpriteRecolor.cs` | 图集按区域重着色（健康面板图标绿→蓝） | 必须「整张图集读入→内存裁剪」，不能依赖 `Graphics.Blit` 的 ST |
| `AmmoCompat.cs` | 7.62×51 独立口径（`AmmoType=3`），接管 `UnloadMag` / `UnloadRound` | |
| `AmmoBox.cs` | 弹药箱：**右键使用型**（不是容器），倒出弹药后自毁 | 原版没有右键使用，这里自己加了 `GetMouseButtonDown(1)` |
| `CrateScatter.cs` | 弹药箱：**每层在加载时一次性摆好整层**（整图随机、10 单位间距、避开出生点 10 单位内） | `ItemLootPool` 只给商人和打碎掉落用，不能靠它生成；落点必须用 `SpotFinder.FindBlockFloor`（方块数组判空气+地板），不能用"随机点+向下 4 单位射线" |
| `SpotFinder.cs` | 统一落点搜索：① 玩家附近（物理，向下 12 单位）② 整层方块坐标（只用方块数组，不依赖物理，供地图加载时预摆） | 原版 `DistributeEntities` 就是这么做的；`GetBlock < 3` = 空气，1 方块 = 1 世界单位 |
| `FreezeOnSpawn.cs` | 预摆物品先 `rb.simulated=false` 冻 4 秒再放开，避免区块碰撞体没就绪时穿过地板 | 放开时清零速度 |
| `SpikeCrawler.cs` | 新敌人「尖刺爬行者」：**用原版 `WorldGeneration.DistributeEntities` 投放**（和 shadecrawler 同一条管线）+ 接管移动意图 + 自建尖刺投射物 | 投放后靠 `FindObjectsOfType<SpiderHandler>` 做差集，改造新增的实例；`SpiderHandler.target` 每帧被它自己重算，只能在其后覆盖；投射物不用物理，**分段 CircleCastAll**；**腿是 `IKHandle.lineRenderer`（LineRenderer）不是 SpriteRenderer**，染色必须走 `startColor`/`colorGradient` + 材质贴图重着色 |
| `LaserDrill.cs` | 「急速下坠」激光钻无限电量（`BatteryItem.DrainCharge` postfix 把电池按回满） | 电量存在**电池物品**的 `condition` 上，钻本身没有电 |
| `LuckyBlock.cs` | 幸运方块：克隆 `foodbox` 当载体，右键/打碎触发，加权抽效果 + **在方块位置飘出的提示文字**（世界坐标投影到屏幕） | 破碎判定在 `BuildingEntity.Update` 的 **prefix**（原版会在那一帧掉落并销毁）；每层 70 个（`[Lucky] PerLayer`），间距 6 单位 |
| `FreefallTweaks.cs` | 急速下坠：弹挂甲/头盔免耐久（`Limb.DamageWearables` prefix）+ 枪不卡壳（`GunScript.JamChance` postfix） | 只对玩家肢体生效，且只在 `code == FREEFALL` 时 |
| `StarterKit.cs` | 开局物资（黑背包/弹挂甲/头盔/耳塞/MDRZ9/弹匣/手电/水/狗粮） | 只在新开局发一次，读档不发 |
| `PlayerSkills.cs` | 初始技能 18/16/18，四档难度相同 | 带一次性迁移标记，旧 cfg 的低值会被抬上来 |
| `WearHide.cs` | 穿戴状态不显示自己的贴图（耳塞） | 必须用 `Body.HoldingItem` 区分手持 |
| `SpawnBridge.cs` | 控制台 `spawn <id>` 接入自制物品 + F7 补给 | |

### 3.4 武器手感

| 文件 | 作用 |
|---|---|
| `GunHandling.cs` | R 键换弹（弹匣优先塞弹挂甲 → 身上空位 → 落地）；「急速下坠」模式下换下的弹匣自动补满 |
| `GunAudio.cs` | 把原版枪声做低通，听感更闷 |
| `WeaponHud.cs` | 右下角 FPS 风格武器状态（弹药/模式） |
| `TwoHanded.cs.disabled` | 双手持枪实验（未启用，改扩展名才会编译） |

### 3.5 特效

| 文件 | 作用 | 关键点 |
|---|---|---|
| `GunFx.cs` | 枪口闪光、枪线光点、命中粒子、像素碎片调度；`[Fx]` 段参数热重载 | 枪线亮度要按「重叠补偿」算：`perDot = 目标亮度 / (覆盖数 × 平均衰减)` |
| `LightFx.cs` | 克隆模板生成 `Light2D`、强度曲线、半径扩张 | `falloffIntensity` **越大越柔**（1.0 = 最柔），别往下压 |
| `PixelParticle.cs` | 4×4 白方块 + Point 过滤，带重力与自转 |
| `GlowFx.cs` | 早期的一次性光点（已被 `LightFx` 取代，保留未删） |

### 3.6 界面

| 文件 | 作用 | 关键点 |
|---|---|---|
| `DlcPanel.cs` | 游戏内战术终端：体征 / 归档 / 链路三页 | 归档列表**不能每帧重建**，否则按钮点不动 |
| `HudButton.cs` | 左下角「战术面板」按钮 |
| `ModMenu.cs` | MOD 管理面板：模块开关 / 热键 / 存档（含重命名、删除、立即存档） | 关闭必须 `_root.SetActive(false)`，只关 Panel 会留下吃点击的遮罩 |
| `NewRunPanel.cs` | 进入游戏界面（盖住原版难度页）：**左栏模式卡片 + 右栏存档列表（载入/改名/删除）**，选模式会弹「给存档起名」 | 显示条件靠 `AdaptiveButton.Clicked` 的 action==0，不靠猜 prefab 状态；急速下坠跳过命名、不建槽 |
| `SaveSlots.cs` | 命名多存档：游戏只有单槽 `save.sv`，我们在外面包一层 `persistentDataPath/STEMFishDLC_Saves/<id>.sv|.lore|.json`。挂 `SaveSystem.SaveGame` 后置（Priority.Last）自动同步；层底存档点打开时自动存档；`F4` 手动存档；载入 = 复制回 `save.sv(+.lore)` 再 `PreRunScript.LoadRun()` | 自定义物品读档要拦 `Resources.Load(string)` 返回内存模板（原版 TryLoadGame 只认 Resources，拿不到就丢物品）；写档前把 `body.forceWalk` 压掉 |
| `HealthPanel.cs` | 健康面板：姓名 STEM-Fish / 身高 171 / 编号 #AAA01 + 绿色转天蓝 |
| `HealthDoll.cs` | 右上角人体图（一张 `doll_atlas.png` → 16 个区域 / 15 个部位 + 尾巴）；染色 **灰白 → 淡卡其 → 琥珀 → 橙红 → 红**（健康端不用绿色），满血整体 38% 透明，出血按量闪 |
| `CursorFeature.cs` | 准星替换（三层覆盖 `Cursor.SetCursor`） |
| `FishSkin.cs` | 内置皮肤替换，走 `plugins/CustomSprites/<包名>/` |

### 3.7 叙事层

| 文件 | 作用 |
|---|---|
| `LoreData.cs` | 档案数据：加载 `lore.zh-CN.json`、已读记录、`noteLater` 按层显示 |
| `LoreNote.cs` | 世界里的纸片投放（每局一张，捡到解锁一篇） |
| `LoreProgress.cs` | **按层自动解锁**：每下一层补一篇未读档案 |
| `FishVoice.cs` | 台词库：`expand`（原版情境混入）+ `chatter`（主动独白） |
| `Vitals.cs` | 生理「变化速率」采样，供终端体征页显示 |

### 3.8 玩法机制

| 文件 | 作用 | 关键点 |
|---|---|---|
| `RunConfig.cs` | **五个游戏模式**的定义与应用（普通/简单/天堂/荒凉/急速下坠） | `runSettings` 是 `Dictionary<string,object>`，写值必须保持原类型 |
`SpawnDirector.cs` | 敌人数量：生成期倍率（prefix 放大 `DistributeEntities`）+ 可选动态投放 + **按模式的禁用名单**（急速下坠不刷潜行者/蜱虫） | 动态投放只在「急速下坠」开 |
| `HypeMode.cs` | 「急速下坠」的娱乐加成：击杀敌对生物 +心情、命中特效放大 | 敌人是 `BuildingEntity` 不是 `Body`，判定点 `health < 0.5` |
| `Mechanics.cs` | 游泳强化 / 击杀同类心情惩罚 / 血袋败血症 / 摔落减免 / 容器诊断 / **F2 解除禁足**（游泳补丁只对玩家 Body 生效，避免串体把 maxSpeed 写成 0） | |
| `MenuArt.cs` | 主菜单 Experiment 立绘的配套美术：用新图 alpha 现场生成白色悬停轮廓（半分辨率膨胀），写进 `MenuExperimentBlink.outlineSprites[]` + `outline.sprite`；文字/点击框按新造型 alpha 包围盒重摆（原版关系：文字=角色中心+120、顶部+43，点击框=中心+132）。`[Skin] MenuAutoLayout` / `MenuOutlineRadius` / `MenuTextOffsetX/Y` | **轮廓在 `MenuExperimentBlink` 里**（`outlineSprites` / `outline`），别按类型名过滤掉带 `Blink` 的组件；生成的 sprite 名不能和皮肤包 PNG 同名，否则被 LoadingArt 换回主立绘 |
| `BloodRed.cs` | 主角的血改成红色：`Limb.Awake` 后置把材质 `_BloodDark`/`_BloodLight` 和流血粒子 startColor 换成红（原版是克隆体的橙黄色）；商人的 `TraderLimbMat` 跳过 | 只改人形 `Limb`；贴花 `wallbloodhuman` 本来就是红的 |
| `CombatFeel.cs` | 生物手感：**挂在伤害入口 `SpiderHandler.AnimalHit(float)` 的后置**（不是碰撞！），受伤才喷黄血 + 小型生物被顶飞（方向=玩家→生物，`stunTime` 0.4s） | 以前挂 `OnCollisionEnter2D` + 「撞到 Item + 相对速度」猜子弹，生物互撞/撞墙都会喷血；`AnimalHit` 由子弹/近战/炮塔/无人机统一调用 |
| `UiText.cs` | 界面文案外置：读 `plugins/STEMFishDLC/ui.zh-CN.json`，覆盖 `RunConfig.All` 的模式 label/desc/perks 和开局界面主要文字；文件缺失就用代码默认值 | 改文案不用重编译，重启生效 |
| `Jammer.cs` | 调试物品「屏蔽器」：使用 = 太阳耀斑（`SetUnchipped(true)`）+ 自绘选择框「拆/不拆」；拆 = 脑损伤 + 头部出血 + 永久无芯片（`runSettings["unchipped"]=true`），不拆 = 本层离线、下层次恢复；界面 90s 无操作自动按不拆 | 控制台 `spawn jammer player` / 调试 F3 |
| `Hearing.cs` | 战术耳塞：听力保护（拦截 `GunScript.Fire` 增量）+ 全局闷声（覆写 `SoundCutoff`） | 读不到 mixer 值时**必须放弃覆盖**，否则会把受损听力"治好" |

---

## 4. 外部资源

### 4.1 文本（改完重启，不用重编译）

部署位置：`BepInEx/plugins/STEMFishDLC/`

| 文件 | 内容 |
|---|---|
| `lore.zh-CN.json` | 11 篇档案。字段：`id` / `kind` / `title` / `subtitle` / `body` / `note` / `noteLater` / `noteLayer` / `autoUnlock` |
| `voice.zh-CN.json` | 台词库。`expand` 含全部 106 个情境：**主角的 26 个已按 STEM-Fish 语气重写**，商人/NPC 的（`trader*` / `*response` / `swingresponse`）保留原版；`chatter` 按状态循环；`lines` 是零散台词（`reload` = 换弹时随机一句） |
| `items.zh-CN.json` | 8 件物品的 `name` / `desc` |

> **归档进度不在插件目录**：它写到游戏存档旁边（`Application.persistentDataPath/save.sv.lore`），
> 和 `save.sv` 同生共死 —— 新开局清空、读档恢复、覆盖存档跟着换。
> 旧的 `lore_unlocks.json` 只在首次启动时迁移一次，之后可以删。
>
> 教程模式（`StartTutorial`）下所有档案默认可读，且「第 N 层补记」全部显示，**不记录进度**。

### 4.2 贴图（同名覆盖即可）

目录：`BepInEx/plugins/STEMFishDLC/textures/`

| 分组 | 文件（尺寸） |
|---|---|
| 武器 | `mdrz9.png` 108×34 / `mdrz9_nomag.png` 108×32 / `mdrz9_mag.png` 18×25 |
| 装备 | `tachelmet.png` 28×16 / `platecarrier.png` 10×12 / `earplugs.png` 12×12 |
| 消耗 | `ammobox.png` 24×16 |
| 敌人 | `spike.png` 8×12（尖刺爬行者的投射物，占位） |
| 方块 | `luckyblock.png` 24×20（幸运方块，占位；金块 + 问号） |
| 准星 | `cursor.png` 32×32 / `cursor_act.png` 32×32 |
| 人体图 | **`doll_atlas.png` 128×307**（一张图集，坐标见 4.5；旧的 9 张 `doll_*.png` 已不再读取） |

> 人体图必须是**纯白**填充，颜色由代码运行时染。
> 改尺寸会改变物品在游戏里的大小（世界尺寸 = 像素 ÷ 模板 PPU），碰撞箱会自动重算。

### 4.3 皮肤包

`BepInEx/plugins/CustomSprites/EXP_STEMFISH_DLC/`

```
baseSizes.json     各部位基准尺寸
Body/              躯干、四肢、尾巴 + 同名 .txt（锚点）
Head/              头、后脑、各种眼睛、毁容状态
Blood/             血粒子、肢体血
```

包名在 cfg 的 `[Skin] PackName`。要换整套皮肤，复制一份改名再指过去。

源码包里也有一份：`STEMFishDLC/skin/EXP_STEMFISH_DLC/`，`deploy.sh` 会把它同步到
`plugins/CustomSprites/`。改皮肤时改源码里那份就不会丢。

### 4.4 档案配图

1. 把 PNG 放进 `BepInEx/plugins/STEMFishDLC/lore_img/`（源码对应 `STEMFishDLC/lore_img/`）
2. 在 `lore.zh-CN.json` 对应条目里加一行：

```jsonc
{
  "id": "sf06",
  "sprite": "tunnel_wall",          // → lore_img/tunnel_wall.png
  // 多张用英文逗号分隔： "sprite": "a, b, c"
}
```

3. 重启游戏，打开那篇档案就会带图。

> **显示位置是终端「归档」页**（`DlcPanel.ShowCodexEntry` 自己摆 Image），不是游戏原生阅读器。
> 反汇编确认 `ScrollableText` 收了 `sprites` 也从不读它（`sprites` 全程序集只有写入点），
> 所以世界里的纸片只显示文字；带图的档案请在终端里看。

### 4.5 人偶图集（`doll_atlas.png`）

右上角人体图用的是**一张 160×197 的图集**，代码按固定区域切。
改贴图只换这一张，不会再出现"换完图人偶整体变大变小"的问题。

**切分不是矩形**：同一条胳膊和腿在原图上会重叠，矩形切会把大腿像素算进手臂（颜色串部位）。
生成器 `tools/dollgen` 用**沿身体走的 BFS 分区**（geodesic Voronoi）：每个部位放一个锚点，
从所有锚点同时只在**不透明像素**上扩张，每个像素归给"沿身体走过来最短"的那个锚点。
手臂不会越过空气去抢腿，分界线也贴着轮廓走。

坐标是**左上原点**、单位像素（由生成器输出，改图后以生成器打印的表格为准）：

| 区域 | x | y | w | h | 说明 |
|---|---|---|---|---|---|
| head | 0 | 0 | 54 | 44 | 头（含颈） |
| thorax | 55 | 0 | 43 | 45 | 胸 |
| stomach | 99 | 0 | 42 | 34 | 腹 / 胯 |
| arm_u_b | 142 | 0 | 17 | 43 | 左上臂（远侧） |
| arm_d_b | 0 | 46 | 16 | 36 | 左前臂 |
| hand_b | 17 | 46 | 15 | 15 | 左手 |
| arm_u_f | 33 | 46 | 21 | 41 | 右上臂（近侧） |
| arm_d_f | 55 | 46 | 17 | 38 | 右前臂 |
| hand_f | 73 | 46 | 16 | 15 | 右手 |
| leg_u_b | 90 | 46 | 22 | 51 | 左大腿 |
| leg_d_b | 113 | 46 | 22 | 49 | 左小腿 |
| foot_b | 0 | 98 | 25 | 22 | 左脚 |
| leg_u_f | 26 | 98 | 21 | 56 | 右大腿 |
| leg_d_f | 48 | 98 | 22 | 49 | 右小腿 |
| foot_f | 71 | 98 | 25 | 21 | 右脚 |
| tail | 0 | 155 | 98 | 42 | 尾巴（灰色，装饰，固定半透明） |

绘制要求：

- 必须是**纯白**（`#FFFFFF`）填充，颜色全部由代码运行时染 —— 画成彩色的话染色会叠乘得一塌糊涂
  （尾巴是例外：生成器按"灰 = 尾巴"单独抠，代码给固定灰，不参与健康染色）
- 每个区域是**独立像素集合**，互不重叠（生成器保证 0 重叠），所以不会串色
- 透明背景；这张图是柔边剪影（不是像素画），`filterMode` 用 Bilinear

画布上的摆放位置由 `HealthDoll.Parts`（每个区域一个 X/Y）和 `TailX/TailY` 控制，
画布 **140×230**；改布局改那里，跟贴图无关。

**重画全身图**：

```bash
cd tools/dollgen
dotnet run -c Release -- ../../bodypart.png ../../STEMFishDLC/textures/doll_atlas.png
```

它会打印新的坐标表 → 抄进 `HealthDoll.Parts`；同时生成 `doll_atlas_regions.png`
（每个部位一个颜色），用来核对分区有没有跑到别的部位上。
锚点在 `dollgen/Program.cs` 的 `anchors` 数组里，改体型后如果某个部位分歪了，挪锚点即可。
## 5. 配置文件

`BepInEx/config/com.stemfish.dlc.cfg`，首次启动生成。

| 段 | 用途 |
|---|---|
| `[General]` | 诊断日志开关 / `RedBlood`（主角血色改红）+ `BloodDark`/`BloodLight`（#RRGGBB） |
| `[Modules]` | 各模块总开关（StarterKit 等） |
| `[Skills]` | 初始 STR/RES/INT + 一次性迁移标记 |
| `[Run]` | 开局界面开关、负重上限倍率、怪物全局倍率 |
| `[Spawn]` | 动态投放间隔/距离/安全期；`BudgetPerLayer` = **每层累计**刷出上限（不是同时存活）；`Pool`/`DynamicPool` 见下 |
| `[Loot]` | 弹药箱权重、每层撒几个（`AmmoBoxPerLayer`）、最小间距（`AmmoBoxMinSpacing`）、离边缘距离（`AmmoBoxMargin`）；**每层加载时一次性摆好，所有模式都生效**（v0.18.9 起**不再**往原版弹药箱容器里塞东西，只保留我们自己撒的箱子） |
| `[Lore]` | 按层自动解锁开关 |
| `[Creature]` | 尖刺爬行者：开关 / **`SpikeCrawlerLayers`（只在哪些层出现，0 基，默认 `1,2` = 深层岩石/沙漠）/ `SpikeCrawlerDensity`（原版 perChunk 系数，默认 0.5）** / 保持距离 / 开火间隔 / 伤害 / 出血 / 弹片概率 / 尖刺速度（键名 `SpikeCrawlerSpikeSpeed2`） |
| `[Freefall]` | `InfiniteDrill` 激光钻不耗电 / `IndestructibleGear` 装备免耐久 + 枪不卡壳 |
| `[Lucky]` | 幸运方块：开关 / 每层几个 / 弹幕提示开关 / 冲击波 `BlastRadius` `BlastPower` |
| `[Fx]` / `[GunFx]` | 枪线亮度/柔度/半径/间距（支持热重载） |
| `[Hearing]` | 耳塞保护率、闷声截止频率 |
| `[Mechanics]` | 游泳倍率、杀同类惩罚、血袋副作用 |
| `[Combat]` | `YellowBlood` / `Knockback` / `KnockbackPower`14 / `MinDamage`0.5 / `CoverArmor`（贴皮衣物分部位护甲开关）/ `CoatArmor`0.25 `PantsArmor`0.20 `BootsArmor`0.30（**除数**：伤害÷(1+值)）/ `KnockbackPower` |
| `[Items]` | `Jammer` 屏蔽器开关 / `JammerLoot`（像普通物品一样出现在商人·物资箱·尸体，默认开）/ `JammerWeight`2 |
| `[Hype]` | 急速下坠击杀回心情（`KillHappiness` 默认 **12**，无上限）/ 命中特效倍率 |
| `[Save]` | `Enabled` 多存档总开关 / `AutoSaveAtCheckpoint` 存档点自动存档 / `SaveKey` 局内存档键（默认 `F4`） |
| `[Skin]` | 皮肤包名 / 立绘适配 `MenuAutoLayout`、`MenuOutlineRadius`、`MenuTextOffsetX/Y` |
| `[HUD]` | 人体图缩放（`HealthDollScaleV2` 默认 0.8）/位置、健康面板配色、`WeaponHudScale`1.12、`TerminalScale`1.15 |

---

## 6. 改东西的正确姿势

| 想改什么 | 改哪里 | 要重编译吗 |
|---|---|---|
| 剧情正文 / 批注 | `STEMFishDLC/lore.zh-CN.json` | 不用，重启 |
| 台词 | `STEMFishDLC/voice.zh-CN.json` | 不用，**改完 2 秒内自动热重载**（不用重启） |
| 物品名 / 描述 | `STEMFishDLC/items.zh-CN.json` | 不用，重启 |
| **界面文案**（模式卡片名称/描述/特色、开局界面标题与说明、存档区提示） | `STEMFishDLC/ui.zh-CN.json` | 不用，重启 |
| 开局界面按钮（取消/开始/载入/改名/删除/打开目录）与弹窗标题 | `NewRunPanel.cs` | **要** |
| MOD 管理面板全部文字 / 暂停菜单「保存」/ 主菜单顶部状态行 | `ModMenu.cs` | **要** |
| 右下角武器 HUD / 战术终端按钮 / 健康面板文字 | `WeaponHud.cs` / `HudButton.cs` / `HealthPanel.cs` | **要** |
| 屏幕底部提示（已保存 / 存档失败 …） | `SaveSlots.cs` 里的 `Toast(...)` | **要** |
| 屏蔽器选择框 | `Jammer.cs` | **要** |
| 物品图标 / 准星 / 人体图 | `STEMFishDLC/textures/*.png` | 不用，重启 |
| 皮肤 | `STEMFishDLC/skin/EXP_STEMFISH_DLC/`（`deploy.sh` 同步到 CustomSprites） | 不用，重启 |
| 档案配图 | `STEMFishDLC/lore_img/*.png` + `lore.json` 的 `sprite` 字段 | 不用，重启 |
| 物品数值 / 护甲 / 枪械参数 | `ItemRegistry.cs` | **要** |
| 模式名称 / 描述 / 特色 | **优先改 `ui.zh-CN.json`**；`RunConfig.cs` 里只是兜底默认值 | 改 json 不用；改 cs 要 |
| 开局界面布局 / 按钮 | `NewRunPanel.cs` | **要** |
| 终端页面文案 | `DlcPanel.cs` | **要** |
| 换弹台词 | `STEMFishDLC/voice.zh-CN.json` → `lines.reload` | 不用（和上面一样热重载） |

**改完源码后必须重新部署**，否则游戏里跑的还是旧的 dll / 旧 JSON。

> 部署目录里的 JSON 和贴图不是"源"，只是拷贝。每次部署会把源码版本盖过去 —— 所以在部署目录里改东西等于白改。

---

## 7. 已知坑（踩过，别再踩）

| 现象 | 根因 |
|---|---|
| MOD 完全不生效、游戏变纯原版 | 非 Steam 启动时 BepInEx 不注入。已删除所有自动重启脚本，只提示手动从 Steam 启动 |
| `PatchAll()` 静默 0 patch | 本环境不可靠。**一律手动 `harmony.Patch(...)` + `GetPatchInfo` 验证数量** |
| 插件对象被销毁、所有功能停摆 | 宿主在 `Chainloader startup complete` 后被清。所以有 `EngineTick` + `TickRunner` + `FishLog` 三层兜底 |
| 自建 Canvas 场景切换后消失 | 没 `DontDestroyOnLoad`；但太早调用又会被第一个场景加载清掉 → 延迟到 `sceneLoaded` 之后 |
| 面板关一次就再也点不开 | 只关 Panel，全屏 `Dim` 还在吃 raycast。必须 `_root.SetActive(false)` |
| 按钮点不动 | UGUI 判定需要 PointerDown/Up 在**同一个** GameObject。列表每帧重建就会这样 |
| 图标变实心块 | `Graphics.Blit` 的 scale/offset 靠 `_MainTex_ST`，URP 换掉了 blit 材质。改成整张图集读入内存裁剪 |
| 弹药箱永远不生成 | `ItemLootPool` 只给商人 + 打碎掉落用，跟容器内容无关 |
| 往 `runSettings` 塞 int 直接崩 | `GetRunSettingFloat` 是 `unbox.any Single`，类型必须一致 |
| 光照怎么调都像亮斑 | `falloffIntensity` 越大越柔，之前一路往下压是反的 |
| 容器装不进东西 | `Container.LoadItem` 失败是**静默 return**，且要求物品与容器距离 < 10 |

---

## 8. 版本

当前 **v0.13.9**。版本号在 `Plugin.cs` 的 `[BepInPlugin]` 和 `Log.LogInfo` 两处，改的时候一起改。

主要里程碑见 `.workbuddy/memory/` 下的日志。
