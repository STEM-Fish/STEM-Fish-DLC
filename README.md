# STEM Fish DLC

> **状态：第一版公开。文档仍在施工中。**
> 玩法机制、数值、美术资源（皮肤 / 立绘 / 图标）都已经到位并可直接游玩，
> 但 `README`、`SOURCE_GUIDE.md` 等说明文字还有不少没写完 / 没校对，
> 后续版本会补齐。发现说明和实际行为不一致，以代码为准，也欢迎提 issue。

给 **Casualties: Unknown**（Steam 正式版）写的 BepInEx 5 插件：
在保留原版生存压力的前提下，加入一套以「实验体 SAW-01 / STEM-Fish」为主角的
皮肤、立绘、剧情文本、武器、敌人、物资与存档系统。

当前版本：**0.18.10**（插件 GUID：`com.stemfish.dlc`）

---

## 1. 它做了什么

### 开局与流程

| 机制 | 说明 |
|---|---|
| **五种模式** | 普通 / 简单 / 天堂 / 荒凉 / **急速下坠**，都是独立开局参数（怪物密度、陷阱、光照、自愈、代谢、摔落伤害等） |
| **急速下坠 = 无尽模式** | 走原版 `runSettings["endless"]`，一直往下不掉结局；无限弹匣、探照灯全开、激光钻无限电、击杀回心情 |
| **命名多存档** | 每个普通模式开局建一个命名存档；层底存档点自动写入，`F4` 随时手动保存；右侧列表可载入 / 改名 / 删除。急速下坠默认不建存档 |
| **开局装备** | 黑色双肩包（内含狗粮 + 纯净水）、MDRZ9、护甲三件套、战术头盔、耳塞、手电、绷带 |

### 战斗

| 机制 | 说明 |
|---|---|
| **MDRZ9 7.62×51** | 自定义口径（AmmoType 3）的无托步枪：20 发、单发结构伤害 185、后坐力 72%、枪声过三级低通 |
| **实验性粒子切割机** | 急速下坠里激光钻改名并强化：对结构单次 100 点伤害 |
| **尖刺爬行者** | 新敌人（克隆 `thornbackyoung`）：远程保持距离射尖刺。正常模式只在深层岩石 / 沙漠层刷，急速下坠不限层，教程不刷；生成走原版 `DistributeEntities` 管线 |
| **战斗手感** | 生物受伤喷黄血 + 小型生物被顶飞（挂在真正的伤害入口 `AnimalHit`，不会误触发）；急速下坠击杀回心情 |
| **红色血液** | 肢体血渍与流血粒子从原版的橙黄色改成红色（贴花本来就是红的） |

### 物资与道具

| 物品 | 说明 |
|---|---|
| **弹药箱** | 每层地图加载时一次性撒 14 个；右键打开倒出 3~4 个满的 MDRZ9 弹匣 + 其它口径 + 散弹 |
| **幸运方块** | 急速下坠专属，打碎随机抽效果，提示文字跟着方块位置飘 |
| **屏蔽器** | 使用＝太阳耀斑（芯片离线）+ 一次「要不要拆掉芯片」的抉择。拆＝脑损伤 + 头部出血 + **永久无芯片**（走自由结局）；不拆＝本层离线。可以从商人 / 打碎的物资箱 / 尸体上捡到 |
| **MDRZ9 弹匣 / 7.62 散弹** | 自定义口径弹药，退弹 / 卸匣流程已接管 |

### 视觉与文本

- **皮肤包** `EXP_STEMFISH_DLC`：主角整套贴图（身体 / 头 / 血迹 / 眼睛状态）
- **主菜单立绘**：坐着的 EXP（含眨眼三帧），悬停白色描边由代码**按当前立绘现场生成**
- **加载 / 生成界面立绘**：`genexpnewalt` → 自动回退兼容 `genexpnew`（下坠舱那段）
- **准星**、**健康面板**、**右上角人体图**、**右下角武器 HUD**、**HUD 按钮**
- **文本全部外置**（改完不用重编译）：

| 文件 | 内容 |
|---|---|
| `voice.zh-CN.json` | 台词库：`expand` 128 类 452 句（主角全部情境）/ `chatter` 10 类 / `lines` 7 类零散台词 |
| `items.zh-CN.json` | 物品名与描述 |
| `lore.zh-CN.json` | 战术终端档案（11 篇，按层自动解锁） |
| `ui.zh-CN.json` | 界面文案：模式卡片、开局面板标题与说明 |

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

3. 启动游戏。日志里出现 `[STEMFishDLC] ===== 加载 v0.18.10 =====` 即成功。

> 联机：可与 **Casualties: Together v5.4.0**（KrokMP 的续作）共存。
> 进游戏前建议先在 `Settings > General` 里确认联机开关状态。

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
- Unity 引擎模块走 NuGet（`UnityEngine.Modules 2022.3.*`，源 `nuget.bepinex.dev`）。

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
├── HealthPanel.cs / HealthDoll.cs / WeaponHud.cs / DlcPanel.cs
├── FishVoice.cs / UiText.cs / ItemText.cs / FishLocalization.cs
├── voice.zh-CN.json / items.zh-CN.json / lore.zh-CN.json / ui.zh-CN.json
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
| `[General]` | 总开关、隔离其它 MOD、调试工具、红色血液 |
| `[Run]` | 开局界面、负重上限倍率、怪物全局倍率 |
| `[Spawn]` | 动态投放间隔 / 距离 / 每层上限 |
| `[Loot]` | 弹药箱权重、每层数量、间距 |
| `[Combat]` | 黄血、击退力度、三件衣物的护甲值 |
| `[Save]` | 多存档开关、存档点自动存档、存档键（默认 `F4`） |
| `[HUD]` | 人体图缩放/位置、武器 HUD 缩放、终端缩放 |
| `[Hype]` | 急速下坠击杀回心情（默认 12，无上限） |
| `[Items]` | 屏蔽器开关与战利品权重 |
| `[Skin]` | 皮肤包名、立绘描边宽度、文字微调 |

改完保存，游戏内热重载的只有 `[Fx]` 与 `voice.zh-CN.json`；其余需要重启。

---

## 6. 已知问题 / 计划

- **文档未完成**（本 README 与 `SOURCE_GUIDE.md` 仍在补，代码注释是全的）
- **角色选择未实现**：游戏本体已经预置了 SAW-03 `Milky` / SAW-12 `Dune` 的立绘与文案，
  但没有任何选择入口（原版 1.0 也没做）。本 MOD 目前只有 SAW-01
- **部分加载界面立绘仍是原版占位**（选人界面的 char1~3、站台底座），正在重绘
- **地图不随存档保存**：存档沿用原版格式（人物 / 物品 / 层数 / 设置），
  读档后当前层会重新随机生成，这是原版行为
- 与联机 MOD 的兼容性只做了基础验证，未覆盖所有场景

---

## 7. 致谢

- **Orsoniks** —— *Casualties: Unknown* 的开发者
- **BepInEx / Harmony** —— 插件加载与运行时补丁
- **Krokosha666 / Creature** —— KrokMP → Casualties: Together（联机）
- **SuperGodFistTool** —— 服务器管理思路与部分实现的参考

---

## 8. 许可

**MIT + Commons Clause**：可以自由使用、修改、分发、二次开发，
**但不得用于商业用途**（不得出售，也不得基于它提供收费服务）。
转载 / 二次发布请保留出处与本许可。详见 [LICENSE](LICENSE)。

美术与文本：本项目原创素材同样适用上述许可；`Casualties: Unknown` 的原始素材
版权归 Orsoniks，本仓库不包含游戏程序集。

本项目是非官方玩家内容，与 Orsoniks 无关。**MOD 有损坏存档的风险，请先备份。**
