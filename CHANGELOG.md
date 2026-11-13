# 更新日志

版本号即 `[BepInPlugin]` 里的字符串，游戏日志搜索 `加载 v` 可以确认当前跑的哪一版。

## 0.19.2 — 2026-11-13

- **体型改用正确做法重做**：新增 `BodyProportion`，**只放大贴图外观**，碰撞 / 刚体 / 关节一个字节不动
  - 原理：`FishSkin` 的 PPU 算法下 **世界尺寸 = baseSize / BasePpu，跟 PNG 像素数无关**；
    所以建 sprite 时把 ppu 除以 k（`ppu = BasePpu * (tex.width / baseSize.x) / k`）就是纯外观放大
  - 换算：原版 EXPie 身高 `Random.Range(130,161) × heightMultiplier(1.0)`，中值 145.5cm；
    设定 1.70m ⇒ `k = 170 / 145.5 ≈ 1.168`
  - 同时把 `WoundView.cInfo[0]` 写成 170（健康面板 `170cm | … | #id`，存档 `cHeight` 同步）
- **为什么不再用 `transform.localScale` / `heightMultiplier`**：
  - 缩放 Body 会把 15 个肢体的 Collider2D 一起放大，脚底基准没补偿 → 物理求解器把人撑在空中（0.19.0 的悬浮）
  - 拉 `heightMultiplier` 会每帧改写肢体 `localPosition`，而肢体间是 `HingeJoint2D` 铰接、
    joint anchor 是刚体局部坐标不跟着变 → 关节与代码互相对抗，抖动/撕裂
- 代价（诚实记录）：肢体中心没动，相邻贴图会重叠 (k−1) ≈ 17%，视觉上是"整体大了一圈"而不是"腿变长"。
  真要改骨架长度，必须同时重算每个 HingeJoint2D 的 anchor 与 collider 形状

## 0.19.1 — 2026-11-13

- **撤销主角体型缩放**（0.19.0 引入）。放大 `Body.transform.localScale` 之后角色会**悬浮**：
  各肢体 Collider2D / HingeJoint2D 一起被放大，但落地基准（脚底接触地面）没有同步补偿，
  物理求解器把人撑在空中。要正确做得同时重算脚部锚点与全部关节，风险远大于收益，因此整体移除
  `BodyScale` 与 `[Body] HeightCm` 配置项。**主角维持原版 EXPie 的体型与身高**
- 0.19.0 的其余改动保留：STR/RES 12/12、主角负重上限 ×1.4
- ⚠ 0.19.0 期间开过的新档里，档案身高（`cHeight`）被写成了 170cm，读档后会显示 170。
  想恢复原版随机值就新开一局（删档重开即可），不影响其它数据

## 0.19.0 — 2026-11-13

- **主角体型按设定身高 1.70m 等比放大**：新增 `BodyScale`，缩放 `Body.transform.localScale`。
  换算依据：原版新开局身高 = `Random.Range(130,161) * body.heightMultiplier`
  （`PlayerCamera.Start`，EXPie 的 heightMultiplier = 1.0）→ 中值 145.5cm，
  所以 `k = 170 / 145.5 ≈ ×1.168`。同时把档案身高 `WoundView.cInfo[0]` 固定写成 170
  （健康面板显示 `170cm | … | #id`，存进存档的 `cHeight` 也是 170）
  原版玩家结构是 `Dune/Milky → Body（物理 ragdoll 骨架）→ 各肢体` + 同级屏幕外 `Anim` 动画骨骼，
  `Body.HandleVisuals` 每帧把 `Anim` 的姿势写进肢体的 localPosition —— 所以缩放 Body 就是
  **骨架 / 贴图 / 碰撞箱 / 关节一起等比放大**，身高和肩宽同比例，不是把贴图纵向拉长。
  保留 `localScale.x` 的符号：原版 `Body.SwitchDir` 用 ±x 表示朝向，写死正数会把转身掰回来。
  ⚠ 缩放基准用**原版常量**而不是"读当前 cHeight 再除"——后者会把 170 存进存档，
  读档时 170/170 = 1.0 就把体型缩回原版了
- **开局力量/韧性下调**：`[Skills]` STR/RES 18/16 → **12/12**（原版 EXPie 是 9/8），INT 仍 18。
  带一次性迁移（老 cfg 里等于 18/16 的才改，自己调过的保留）
- **同等力量下背得更多**：新增 `PlayerCarry`，挂 `Body.HandlePeriodicChecks` 的 prefix+postfix，
  只对 `PlayerCamera.main.body` 把 `maxEncumberance` 乘 `[Skills] CarryMult`（默认 1.4）并重算
  `overEncumberance`。原版公式是
  `(11 + Clamp(weightOffset+15,-60,0)*0.1 - sickness*0.025 ± 饥渴修正 + Min(STR-10,RES-10)*0.5 + (STR-10)*0.5) * encumbrancecap`，
  所以 STR12/RES12 时是 13 × 倍率，EXPie 的 STR9/RES8 只有 9.5
  ⚠ 原版那段是 `if (halfSecondCheckTime > 0.5)` 才执行，postfix 必须比对"这次是否真的重算过"，
  否则每帧乘一次会让上限指数爆炸（已用 prefix 记录前值判断）
- 日志新增一行 `负重：x / y（zz%）`，开局负不负重一眼能看出来

## 0.18.12 — 2026-11-13

- **修：读档会往下跳一层（第 11 层存档读进去直接到结局层）**
  反汇编确认原版 `SaveGame` 写的是 `biome = biomeDepth + 1`，`TryLoadGame` 读回来是
  `biomeDepth = biome`，整整差一层。原版是"进层式"存档（存档点=本层到底，读档=进下一层），
  我们是"原地续玩"，所以现在在 `TryLoadGame` 之后把层号掰回 `.map` 里记的那一层。
  `biomeDepth == 11` 在 `Body.PlaceBody` 里就是 `ENDLABSPAWN`（实验室结局层），
  这就是之前读档被直接丢进结局的原因
- **新增：结局 / 过场插画替换接口**（`Image.sprite` + `SpriteRenderer.sprite` 双钩子），
  皮肤包里新增 `Ending/` 目录，按 sprite 名匹配，画好同名 PNG 放进去即生效
- **原版结局贴图整套导出**到 `Ending/_reference/`（13 张过场 + 7 张科学家），
  并反查出 `cutsceneImages[]` 的真实索引表，写在 `Ending/README.txt` 和主 README
  → **结局有关的美术资源替换仍未完工**（只有参考图，没有生效的新图）
- 传送复核：写回地图 1 秒后再检查一次，若被原版 `WorldPlacePlayer` 摆回出生点就补一次传送

## 0.18.11 — 2026-11-13

- **存档现在会保存地图和角色坐标**：新增 `<槽位>.map`（gzip 的整层方块表 + 坐标），
  读档时等新世界生成完毕后整份写回并按 CHUNK 批量刷新，再把角色传送到存档位置。
  原版存档格式没动，`[Save] SaveMap` 可以关掉
- **物品文本表修好了**：UI 读的是 `ItemInfo.fullName / description`，以前只塞了 Locale，
  所以改 `items.zh-CN.json` 游戏里看不到；现在注册时就把 json 文本写进 ItemInfo
- 刷怪密度整体上调（2.2/1.7/1.4/3.0/1.6 → 2.6/2.0/1.65/3.5/1.9）
- 弹药箱每层 14 → 17（老 cfg 一次性迁移）

## 0.18.10 — 2026-11-13

- **急速下坠改用「无尽模式」**：写原版 `runSettings["endless"]`，一直往下不掉结局；其它模式显式复位
- **击杀回心情修好了**：正式版生物销毁在 `BuildingEntity.CheckDamage()`（不是 `Update`），
  之前挂错入口所以几乎不涨。奖励 4 → **12**，取消上限
- **UI 放大**：人体图 0.7 → 0.8；新增武器 HUD 缩放 1.12（锚点在右下，不会压加速条）；战术终端 1.15
- **屏蔽器可以捡到了**：注入战利品池 → 商人货架 / 打碎的物资箱 / 尸体遗留
- **战斗手感重做**：黄血与击退改挂真正的伤害入口 `SpiderHandler.AnimalHit(float)`，
  不再用「撞到物品 + 相对速度」猜子弹（生物互撞 / 撞墙不再冒血）；击退力度 14、硬直 0.4s

## 0.18.9 — 2026-11-12

- 主角的血改成红色（肢体 `_BloodDark`/`_BloodLight` + 流血粒子；贴花本来就是红的）
- 三件贴皮衣物护甲大幅下调：0.8/0.7/1.0 → **0.25/0.20/0.30**（`GetArmorReduction` 是除数）
- **界面文案外置**：新增 `ui.zh-CN.json`，模式卡片与开局面板主要文字改成读 json
- 撤销「往原版弹药箱容器里补 MDRZ9 弹匣」，只保留自己撒的弹药箱

## 0.18.8 — 2026-11-12

- 台词库补全正式版新增剧情 22 类（`cargoimpale` / `lab*` / `endlessloop*` / `wwmr` 等），共 128 类 452 句
- 硬编码台词全部搬进 `voice.zh-CN.json` 的 `lines`；存档时不再播台词
- 主菜单版本提示从屏幕底部移到**顶部**
- 自带弹药箱保证掉 MDRZ9 弹匣（3~4 个），日志分出「MDRZ9 / 其它口径 / 散弹」

## 0.18.7 — 2026-11-12

- 修「进一次游戏再回主菜单，悬停描边恢复原版」：改为每帧校验绑定，场景重建后自动重绑

## 0.18.6 — 2026-11-12

- 悬停描边改成**细描边**（原版 `experimentOverlay` 实测 4~5px 宽的环，之前填成了实心白影）
- 全分辨率取 alpha（半分辨率会抹掉电缆等 1~2px 细节），线宽默认 5

## 0.18.5 — 2026-11-12

- 找到悬停轮廓的真正载体：`MenuExperimentBlink.outlineSprites` + `outline`
- 文字 / 点击框改为按**新造型的 alpha 包围盒**重摆（不再用新旧差值）

## 0.18.4 — 2026-11-12

- 主菜单立绘的配套美术适配第一版（轮廓生成 + 文字平移）

## 0.18.3 — 2026-11-12

- 修「教程里用过屏蔽器后新开档全是无芯片」：`StartRun` 前缀统一复位 `unchipped`
- 屏蔽器文案去掉「公司配发」

## 0.18.2 — 2026-11-12

- **修第 10 层货物植入后永久不能动**：台词「完全替换」模式对未知情境返回空表
  → `Talker.TalkDelayed` 里 `PickRandom(空表)` 抛异常 → 过场协程中断 → `forceNoMovement` 不复位。
  现在没我们的台词就保留原版，并给 `TalkDelayed` 加了空表保护
- 新增调试物品「屏蔽器」；急速下坠击杀回心情 1 → 4（并计入尖刺爬行者）
- 顺手修三处刷屏：`Container` 空引用 19 万条、读档模板 3510 条、游泳补丁串体

## 0.18.1 — 2026-11-12

- 修读档后自定义物品全丢（`Resources.Load` 的重载歧义导致钩子没挂上）
- 存档列表信息改为直接解压存档读（层数 / 时长 / 物品数）
- 战术终端空白修复（`lore.zh-CN.json` 里的 NBSP 与缺失引号）
- 暂停菜单「保存」按钮改为纯净保存，不再顺带打开 MOD 面板

## 0.18.0 — 2026-11-12

- **命名多存档**：外部包装原版单槽 `save.sv`；层底存档点自动保存、`F4` 手动保存；
  进入游戏界面改成左右两栏（左选模式、右存档列表可载入 / 改名 / 删除）

## 0.17.x — 2026-11-11 / 12

- 皮肤补全正式版新增的眼部状态贴图（`experimentEyeLockedIn*`）
- 主菜单 / 加载界面立绘替换（`Image.sprite` 钩子 + 眨眼三帧）
- 黄血特效、小型生物击退、结构伤害 100 的粒子切割机
- 开局物资调整；新增三件「贴皮衣物」（不可脱下 / 不可破坏 / 不显示外观）+ 分部位护甲
- 幸运方块加密、提示文字跟到方块位置、换弹台词进 json 并支持热重载

## 0.16.x — 2026-11-11

- **移植到正式版 1.0**：BepInEx 5.4.23.6 + Casualties: Together v5.4.0
- 三个工程参数化 `-p:GameDir=`，新增 `deploy_full.sh`
- 「停用退出自爆」重写（正式版把 `PauseHandler.paused` / `TogglePause()` 改成了静态成员）
- 尖刺爬行者改用原版 `WorldGeneration.DistributeEntities` 投放；教程不刷

## 0.15.x 及更早

- 弹药箱 / 幸运方块改为「整层加载时一次性摆好」（不再边跑边刷）
- 台词系统、档案系统、健康面板 / 人体图、准星替换、听力与耳塞、游泳强化等基础模块
