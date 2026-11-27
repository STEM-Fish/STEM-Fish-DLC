# STEM Fish DLC

> **状态：第一版公开。文档仍在施工中。**
> 玩法机制、数值、美术资源（皮肤 / 立绘 / 图标）都已经到位并可直接游玩，
> 但说明文字、剧情文字尚未完善和校对完全，
> 后续版本会补齐。发现说明和实际行为不一致，以代码为准，也欢迎提 issue。

为 **Casualties: Unknown**（Steam 正式版）写的 BepInEx 5 插件：
加入一套以「实验体 STEM-Fish」为主角设定的 DLC 式扩充体验、包含扩充的物品、剧情、机制、游戏模式、美术资源等。如果你想在这款游戏里面临更小的资源和发育压力，但希望增加 PVE 的“乐趣”，这个MOD也许会适合你。

说明：是非官方MOD，此角色设定并不是 Casualties: Unknown 或 Gunsaw 原设定之中的。设定版权归开发者所有。但这不影响你可以免费获得这个 MOD 进行游玩，并可以任意进行开源的二次开发。详见 License。

当前版本：**0.24.0**（插件 GUID：`com.stemfish.dlc`）

---

## 1. MOD 内容

### 游戏模式与基本机制修改

| 机制 | 说明 |
|---|---|
| **五种模式** | 普通 / 简单 / 天堂 / 荒凉 / **急速下坠**，在原版的基础上调整过各项参数，并包含娱乐模式。 |
| **独立角色** | SF-07 是选角界面里的第 4 个角色，与 EXPie / Milky / Dune 并列。选原版角色就完全是原版（皮肤、身高、属性、负重、血色都不动）；选 SF-07 才是这套外观与 10/9/18。**选角入口在主菜单右上角「选择角色」**，点开就是原版 CharSelect 页（第 4 格是 SF-07，永远可选）；底部按钮文案改成「选择角色」，点它只确认、不直接开局。角色说明写在 `chars.*.json`。 |
| **急速下坠** | 与原版 `runSettings["endless"]`一致。模式机制包含无限弹匣、世界明亮、核动力激光钻、击杀奖励心情、**幸运方块刷新**、摔落伤害减免、卡壳取消等。这意味着你可以在这个模式里进行非正式游玩和放松。是真EXPie就下100层！ |
| **存档** | MOD自带存档系统。每个正常模式开局建一个存档并要求命名；层底存档点自动写入，通过`F4`或UI按钮 手动保存；进入游戏右侧列表可载入 / 改名 / 删除。急速下坠默认不建存档。**存档会连同本层地图方块与角色坐标一起保存，读档后地形和站位都跟存档时一致**（`[Save] SaveMap` 可关）。读档是「原地续玩」：原版会把层号 +1（存档点=本层到底 → 读档进下一层），我们把层号掰回存档那一层，所以读档后还在同一层，走到底再按「继续前进」才下去。**读档后有 10 秒无敌窗口**（`[Save] LoadGraceSeconds`），避免地形还在分块加载时被刷在脸上的怪咬死。 |
| **开局装备** | 黑色双肩包*（内含狗粮 + 纯净水）、MDRZ9*、弹挂甲、战术头盔、耳塞、手电、冲锋衣、复合裤子、硬橡胶鞋子 （包含大量非原版物品） |
| 取消退出自爆 | 自带取消退出自爆机制。这会让你无需使用 Alt+F4 离开你的游戏以避免目睹这场悲剧。 |
| **体型** | 按设定身高 **1.70m** 放大外观（原版 EXPie 基准 145.5cm → 贴图 ×1.168）。只改**贴图显示大小**，碰撞箱 / 刚体 / 关节完全不动 —— 所以不会悬浮、不卡窄洞。`[Body] HeightCm` 可调。 |
| **负重** | 开局力量/韧性 **10/9**（原版 EXPie 是 9/8），但同样的力量等级下**背上限比 EXPie 高 40%**（`[Skills] CarryMult`）。数值下调是为了不在投掷/近战上过分超模，负重则由倍率补回来，开局不超重。 |

### 战斗有关机制

| 机制 | 说明 |
|---|---|
| **尖刺爬行者** | 新敌人（类似红色的 `thornbackyoung`）：远程保持距离射尖刺，造成流血和弹片伤。正常模式只在深层岩石(2) / 沙漠层(3)刷，急速下坠不限层；**只有选 SF-07 的局才会出现**（判定写在这个生物自己的投放函数里，原版角色的刷怪管线一行都没被拦）。注意这会大大增加第二层的 PVE 强度与弹药紧张程度。 |
| **反馈优化** | 生物受伤喷黄血 + 小型生物被击飞 + 枪线与命中点的光照和特效扩充；急速下坠击杀回心情。 |
| **红血** | 主角的血走守卫（人类）那一套：肢体血渍贴图、流血粒子、落地/墙面血渍全红。其它生物保持实验体的黄血。`[General] RedBlood` 可关，`BloodDark/BloodLight` 可改色。 |
| **战斗键位** | 出于角色设定，你将拥有最好用的R键，在智能检测手持物品状态的基础上，同时具备自动更换弹匣、拉栓等功能。但请注意原版健康面板的键位。这将保证战斗的连贯性并大幅提升战斗体验。 |

### 物资与道具

| 物品 | 说明 |
|---|---|
| **弹药箱** | 作为额外的资源容器刷新，包含各种弹药。旨在为正常游玩提供弹药补充。 |
| **SF-07 血液** | 在物品栏里对血袋「对自己使用」是**抽血**。只有 SF-07 抽出来的是自己的暗红色血液（新液体 `sfblood`）：除生理盐水 / 林格氏液 / 血清之外**唯一对他安全的血液**，注射可恢复血容量且不会引起排异。它不会被人类 DNA 检测终端当成人类样本。其它角色抽血仍是原版的黄血。 |
| **幸运方块** | 急速下坠专属，打碎随机抽效果。你玩过我的世界吗？是的。       |
| **屏蔽器** | 剧情关键道具，**平时右键没有反应**。带着它下到第 10 层（拿到货物那一层）时会自动定住你，播完几段自言自语后让你选：拆芯片，还是留着。拆 → 脑损伤 + 头部出血 + 受伤音效，此后永久**无芯片**（会改变结局走向）；不拆 → 芯片留着、屏蔽器还在兜里，**这一层之内**还能再按一次，离开第 10 层就彻底失效。商人 / 物资箱 / 尸体遗留物都可能出 |
| **MDRZ9 弹匣 / 7.62 散弹** | 自定义口径弹药与枪械。 |

### 语言 / Language

- 首次启动按**游戏语言**自动选择：英文游戏 → `en-US`，中文游戏 → `zh-CN`，俄语游戏 → `ru-RU`。
- **游戏内也能切**：主菜单右上角「MOD 管理」→ 模块页 → 通用 → **文本语言**，
  点一下按 `跟随游戏 → 中文 → English → Русский` 循环（包里没有 ru 文件时自动跳过 Русский）。
  词库 / 界面文案 / 物品名 / 档案立刻重载，少数已经生成到屏幕上的 TMP 文本重启后完全一致。
- 也可以直接改 `BepInEx/config/com.stemfish.dlc.cfg` 的 `[General] Language`
  （`auto` / `zh-CN` / `en-US` / `ru-RU`）。
- 五个文本表都有中 / 英 / 俄三份：`items.*.json` / `voice.*.json` / `lore.*.json` / `ui.*.json` / `chars.*.json`；
  界面里原本硬编码的文字也已经搬进 `ui.*.json` 的 `strings` 段。
- 俄文是**逐键合并**的：以 `en-US` 为底，`ru-RU` 里有的键覆盖上去，没翻到的键保留英文。
  所以只翻一半也能直接用，不会突然冒出一句中文；`lore` 的 11 篇按 `id` 合并。
- 英文版里，**档案 + 全部主角台词 + 界面**都是重写的；只有商人 / NPC 的回应保留
  游戏原版英文（说话的不是 SF-07）。

### 视觉与文本

- **皮肤包** `EXP_STEMFISH_DLC`：主角的整套贴图（身体 / 头 / 血迹 / 眼睛状态）
- **主菜单立绘**：坐着的 EXP（含眨眼三帧）资源替换
- **加载 / 生成界面立绘** 资源替换
- **结局 / 过场插画** 资源替换接口已就位（🚧 **结局有关美术资源替换未完工**，见下方「待补美术」）
- **新准星**、**健康面板改动**。
- **右下角HUD提示**
- **右上角状态人偶 与 体力条、声音条**
- **文本信息全部外置**：

| 文件 | 内容 |
|---|---|
| `voice.zh-CN.json` / `voice.en-US.json` | 台词库：`expand` / `chatter` / `lines`；英文版主角台词全量重写（89 类 / 303 句），只有商人 / NPC 回应保留游戏英文 |
| `chars.zh-CN.json` / `chars.en-US.json` | SF-07 的角色说明：名字、生物分类、临行之言、成功几率、备注、九条特性（选角界面右侧显示） |
| `items.zh-CN.json` / `items.en-US.json` | 物品名与描述（含血袋不兼容提示） |
| `lore.zh-CN.json` / `lore.en-US.json` | 战术终端档案（11 篇；`layer` 字段把每篇钉在 1~11 层；sf10 / sf11 的批注按芯片状态分三版） |
| `ui.zh-CN.json` / `ui.en-US.json` | 界面文案：模式卡片、开局面板、MOD 管理页、战术终端、HUD 与存档提示 |

---

## 2. 安装

### 前置

1. **Casualties: Unknown**（Steam 正式版，Unity 2022.3.62f3 **Mono**）
2. **BepInEx 5.4.23.6 x64**（[下载](https://github.com/BepInEx/BepInEx/releases)）
   - 必须 5.4.23.**5** 或更新的 5.x：旧版本在这版 Unity 上有 `get_graphicsDeviceID` 问题
   - **不要**用 BepInEx 6 / IL2CPP 版本

### 步骤

1. 把 BepInEx 解压到游戏根目录（`CasualtiesUnknown.exe` 所在处），启动一次游戏再退出，
   让 `BepInEx/plugins/`、`BepInEx/config/` 生成出来。
2. 把本仓库 Release 里的压缩包解压到**游戏根目录**，最终结构：

```
Casualties Unknown/
├── BepInEx/
│   ├── plugins/
│   │   ├── STEMFishDLC.dll
│   │   ├── No Self-Destruct on Quit.dll
│   │   ├── STEMFishDLC/          ← 资源：json / textures / lore_img / skin
│   │   └── CustomSprites/EXP_STEMFISH_DLC/   ← 皮肤包
│   ├── patchers/
│   │   └── STEMFishDLC.Patcher.dll   （可选：隔离其它 MOD）
│   └── config/com.stemfish.dlc.cfg   （首次启动自动生成）
└── winhttp.dll / doorstop_config.ini （BepInEx 自带）
```

3. 启动游戏。日志里出现 `[STEMFishDLC] ===== 加载 v0.24.0 =====` 即成功。

> 联机：可与 **Casualties: Together v5.4.0**（KrokMP 的续作）共存。
> 进游戏前建议先在 `Settings > General` 里确认联机开关状态。

**目前请不要在使用联机模式游玩时启用这个MOD。**

### 卸载

删掉 `BepInEx/plugins/STEMFishDLC.dll`、`BepInEx/plugins/STEMFishDLC/`、
`BepInEx/plugins/CustomSprites/EXP_STEMFISH_DLC/`、
`BepInEx/plugins/No Self-Destruct on Quit.dll`、`BepInEx/patchers/STEMFishDLC.Patcher.dll`
即可（存档在 `%LOCALAPPDATA%Low\Orsoniks\CasualtiesUnknown\STEMFishDLC_Saves\`，自行决定是否保留）。

---

## 3. 从源码构建

需要 **.NET SDK 8**（工程目标框架 `netstandard2.1`，编译期引用游戏程序集）。

```bash
dotnet build -c Release -p:GameDir="D:\Steam\steamapps\common\Casualties Unknown"
```

- `GameDir` 指向游戏根目录；工程用 `HintPath` 引用
  `BepInEx/core/BepInEx.dll`、`0Harmony.dll` 与
  `CasualtiesUnknown_Data/Managed/Assembly-CSharp.dll` 等，
  这些**不随仓库分发**（`Private=false`，不会进产物）。
- Unity 引擎模块：走 NuGet（`UnityEngine.Modules 2022.3.*`，源 `nuget.bepinex.dev`）。

一键构建 + 部署三个产物：

```bash
bash deploy_full.sh                                        # 默认正式版目录
GAME_DIR="D:/.../Casualties Unknown" bash deploy_full.sh    # 指定别的目录
```

| 工程 | 产物 | 作用 |
|---|---|---|
| `STEMFishDLC/` | `STEMFishDLC.dll` | 本体 |
| `NoSelfDestruct/` | `No Self-Destruct on Quit.dll` | 退出游戏不触发芯片自毁 |
| `patcher/` | `STEMFishDLC.Patcher.dll` | 预加载器：隔离其它 MOD（默认关闭） |

---

## 4. 目录结构（源码）

```
STEMFishDLC/
├── Plugin.cs                ← 入口（BaseUnityPlugin + Awake）
├── RunConfig.cs             ← 五种模式的参数 + 写 runSettings
├── NewRunPanel.cs           ← 进入游戏界面（模式卡片 / 存档列表）
├── SaveSlots.cs             ← 命名多存档（包装原版单槽 save.sv）
├── ItemRegistry.cs          ← 自定义物品表（MDRZ9 / 弹药箱 / 屏蔽器 / 衣物…）
├── AmmoBox.cs / AmmoCompat.cs
├── SpikeCrawler.cs / LuckyBlock.cs / SpawnDirector.cs
├── FishSkin.cs / LoadingArt.cs / MenuArt.cs / CursorFeature.cs
├── CharacterSelect.cs       ← SF-07 作为第 4 个角色（Tag / 立绘 / BaseSkills / SpawnCharacter）
├── Toxin.cs                 ← 可可碱不耐受（包 Liquids.Registry 的 onDrink）
├── HealthPanel.cs / HealthDoll.cs / WeaponHud.cs / DlcPanel.cs
├── FishVoice.cs / UiText.cs / ItemText.cs / FishLocalization.cs
├── voice.zh-CN.json / items.zh-CN.json / lore.zh-CN.json / ui.zh-CN.json / chars.zh-CN.json
├── skin/EXP_STEMFISH_DLC/   ← 皮肤包（Body / Head / Blood / Loading）
├── textures/                ← 物品图标、准星、人体图等
└── lore_img/                ← 档案配图
```

更细的模块说明、反汇编结论、踩坑记录见 **[SOURCE_GUIDE.md](SOURCE_GUIDE.md)**。

---

## 5. 配置

`BepInEx/config/com.stemfish.dlc.cfg`，首次启动生成。常用段落：

| 段 | 内容 |
|---|---|
| `[General]` | 总开关、语言（auto / zh-CN / en-US）、隔离其它 MOD、调试工具、红色血液、战利品落点修补 |
| `[Run]` | 开局界面、负重上限倍率、怪物全局倍率 |
| `[Spawn]` | 动态投放间隔 / 距离 / 每层上限 |
| `[Loot]` | 弹药箱权重、每层数量、间距 |
| `[Combat]` | 黄血、击退力度、三件衣物的护甲值 |
| `[Save]` | 多存档开关、存档点自动存档、存档键（默认 `F4`） |
| `[HUD]` | 人体图缩放/位置、武器 HUD 缩放、终端缩放 |
| `[Hype]` | 急速下坠击杀回心情（默认 12，无上限） |
| `[Items]` | 屏蔽器开关与战利品权重 |
| `[Skills]` | 初始 STR/RES/INT、主角负重倍率（默认 ×1.4） |
| `[Body]` | 设定身高（默认 170cm，换算成外观缩放 ≈ ×1.168） |
| `[Skin]` | 皮肤包名、立绘描边宽度、文字微调 |

改完保存，游戏内热重载的只有 `[Fx]` 与 `voice.zh-CN.json`；其余需要重启。

---

## 6. 已知问题 / 计划

### 待补美术（0.18.12 状态）

> 🚧 **结局有关美术资源替换未完工。** 机制和素材位都已经铺好，只差画：

- 结局过场共 **5 种**（逃脱 / 地表突破 / 佣兵 / 生物舱 ×3 变体），
  全部走 `WorldGeneration.cutsceneImages[13]` 这个贴图数组；
- 主角差分按 `body.charType` 取（`char1end` = SAW-03 Milky / `char2end` = SAW-12 Dune / `char3end` = 第三位）；
- 实验室结局里的科学家是 `scientist` + `endsc1~4` + `endscfall` + `endsccorpse`。

原版图已经整套导出到
`BepInEx/plugins/CustomSprites/EXP_STEMFISH_DLC/Ending/_reference/`，
**把画好的同名 PNG 挪到上一层的 `Ending/` 就会生效**（不用改代码、不用重编译），
每张的用途、索引、对应哪一幕写在同目录的 `README.txt` 里。

### 其它

- **角色选择**：入口在**主菜单右上角「选择角色」**（在「MOD 管理」下面）。打开的是
  **原版 `runSettingsScreen` 里的 CharSelect 页**，我们把 SF-07 作为第 4 格插进去
  （克隆第 3 格的台座与按钮，`rect` 按自己的宽高比，永远可选，不吃 `wonchar` 解锁）。
  页面底部的「开始冒险」文案改成「选择角色」，点它只确认并关闭页面，不会直接开局；
  主界面立绘 / 轮廓 / 文字位置跟着切换。
  `PreRunScript.characterSelected` 仍是原版字段，存档里的 `SaveInfo.character` 也照原样记 0~3。
  联机模组（Together）会放开 `CharacterSpawn.ClampChar` 的 0..2 限制，但**对端也要装
  同一个版本**，否则对方只会看到 Dune。
- **选原版角色时**：皮肤 / 身高 / 负重 / 红血 / 初始属性全部不动；不发开局装备、
  不撒弹药箱、不刷尖刺爬行者、不调刷怪倍率；台词走游戏原版（不会听到 SF-07 的独白）；
  右下角武器 HUD、左下角 HUD 按钮、F8 战术终端、R 键智能换弹全部关闭；
  健康面板保持原版绿色 + 随机编号。教程模式同样完全原版。
- **建筑实体不随存档保存**：地图方块和角色坐标已经存了，但箱子 / 商人 / 机器这类
  `BuildingEntity` 仍由世界生成时随机摆放，读档后会重新分布（原版格式里没有这部分）
- 与联机 MOD 的兼容性只做了基础验证，未覆盖所有场景

---

## 7. 致谢

- **Orsoniks** —— *Casualties: Unknown* 的开发者
- **BepInEx / Harmony** —— 插件加载与运行时补丁
- **Krokosha666 / Creature** —— KrokMP → Casualties: Together（联机）
- **SuperGodFistTool** —— 部分接口与代码实现的参考

---

## 8. 许可

**MIT + Commons Clause**：可以自由使用、修改、分发、二次开发，
**但不得用于商业用途**（不得出售，也不得基于它提供收费服务）。
转载 / 二次发布请保留出处与本许可。详见 [LICENSE](LICENSE)。

美术与文本：本项目原创素材同样适用上述许可；`Casualties: Unknown` 的原始素材
版权归 Orsoniks，本仓库不包含游戏程序集。

本项目是非官方玩家内容，与 Orsoniks 无关。**MOD 有损坏存档的风险，请先备份。**
