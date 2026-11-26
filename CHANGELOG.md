# 更新日志

版本号即 `[BepInPlugin]` 里的字符串，游戏日志搜索 `加载 v` 可以确认当前跑的哪一版。

## 0.23.2 — 2026-11-26

- **俄文本地化（Русский）**：`chars / items / ui / voice / lore` 五份 `ru-RU` 全部翻完
  （角色说明 10 条、物品 14 件、界面 251 键、台词 89+10+8 类、档案 11 篇 + sf10/sf11 的
  三组芯片状态变体）。文风与中/英版一致：第一人称、长句、不写警句
- **语言系统支持任意语种**：`[General] Language` 增加 `ru-RU`；`auto` 会认俄语系统与
  `ru / Russian / рус` 语言名；面板循环变成 `跟随游戏 → 中文 → English → Русский`
  （包里没有 ru 文件时自动跳过，不会选中空白语言）
- **半成品翻译也能用**：新增 `Lang.Read()`，俄文以 `en-US` 为底逐键覆盖，缺的键自动保留
  英文；`lore.logs` 按 `id` 合并、空字段保留英文底稿。`UiText / ItemText / LoreData /
  FishVoice` 四个入口全部改走这里
- 内置回退（角色说明、保底台词、激光钻改名、屏蔽器 fallback）从「英文 / 中文二选一」
  改成「非中文就用英文」，俄文不会掉回中文
- `deploy_full.sh` 按 `*.zh-CN.json / *.en-US.json / *.ru-RU.json` 通配复制，加语种不用改脚本
- 新增 `tools/check_locale.py`：对照 `en-US` 报缺键、缺句、空字段
- **SF-07 自己的血（新液体 `sfblood`）**：在物品栏里对血袋「对自己使用」抽血时，
  只有 SF-07 会把实验体的黄血换成自己的红血；这是除生理盐水 / 林格氏液 / 血清之外
  唯一对他安全的血液——注射回血（30/750ml），不加病、不加脓毒。颜色偏暗红；
  DNA 检测终端只认 `redblood`，所以它不会被当成人类样本。
  非 SF-07 角色走原版（抽出来还是黄血），袋子原有的液体不会被误改
- **读档无敌窗口**：`PreRunScript.LoadRun` 时布防，玩家身体一出现就开始计时
  （默认 10s，`[Save] LoadGraceSeconds` 可调）。窗口内挡掉 `Limb.ImpactDamage` 与
  `SpiderHandler(.TBE).DamageLimb` 两个伤害入口，并每帧把血量/出血/疼痛拉回读档快照，
  避免地图分块还没加载完就被怪物咬死

## 0.23.1 — 2026-11-25

- **尖刺爬行者只在 SF-07 的局里投放**：判断写在它自己的投放函数 `EnsureSpawn`
  和改造入口 `Register` 上，**不去拦原版刷怪管线**。选原版角色时这段逻辑压根不启用，
  原版刷什么、刷多少完全不受影响
- **原版角色不再说 SF-07 的独白**：`FishVoice.SayLine` 统一加门槛（捡档案 / 开弹药箱 /
  开局配给 / 杀同类惩罚这些模块仍会调用它），原版角色一句话都不说
- **原版角色刷怪倍率归零**：新增 `SpawnDirector.ResetToVanilla()`，在局入口调用，
  把生成期倍率、禁用名单、动态投放全部恢复原版（上一局留下的状态不再跨局）
- **选角页四格重排修正单位混用**：位置用世界坐标、缩放比较用本地宽度。
  以前父画布缩放 ≠ 1 时格宽会被放大 1.3 倍以上（日志里「父宽 1728 · 剩余 1959」）
- **选角页打开前清理残留全屏图层**：模式面板 / MOD 管理 / 战术终端 / 屏蔽器覆盖层
  全部强制收起（它们都是 DontDestroyOnLoad 的全屏半透明 + 吃射线），
  同时打印全屏图层清单便于定位
- **选角页“变灰 + 点不动”的两个真正原因（已修）**
  - 点角色卡：第 4 格的 `inner`（实心白剪影）被挪到了 `Slide` 层，选中→白色、
    未选中→黑色，等于在整页上盖了一层黑白剪影。现在直接不画（原版那层是空心描边，
    我们的 char4outline 是实心剪影，画上去只会盖住立绘）
  - 点「返回」：原版会把 `CharSelect` 收起，而它的父物体 `RunSettings` 自带
    `alpha=0.91`、`raycastTarget=1` 的近全屏底板 —— 页一收就只剩这层灰底，
    点什么都没反应。现在检测到页面被收起就立刻关掉整个设置页，回主菜单
- **选角页守护**：每帧把 `cover`（整屏淡入淡出层）的 alpha 与 `raycastTarget` 归零、
  整链路 CanvasGroup 拉回可交互、`timeScale` 归 1、解锁光标；ESC / 右键可随时退出

## 0.23.0 — 2026-11-23

- **SF-07 成为独立角色（第 4 个）**
  - 进入游戏界面左栏新增「角色」一行：原版 3 个立绘 + SF-07，点一下即选中，
    结果写回原版 `PreRunScript.characterSelected`（存档 / 生成 / 读档全走原版通道）
  - `[Character] Enabled` 可关；关闭后只剩原版三个角色
  - 角色说明在 `chars.zh-CN.json` / `chars.en-US.json`：名字、生物分类（狼人等基因混合体）、
    临行之言、成功几率、备注，以及犬科烙印+ / 改造体质 / 水栖适应 / 生物荧光 / 芯片 /
    特殊训练 / 额外补给 / 终端平板 / 迟到者九条特性；`Skills.BaseSkills(3)` 返回 12/12/18
  - 生成走 `Special/Characters/Experiment` 骨架 + `CharacterSelect.Tag` 标记；
    **`Body.charType` 保持 0**，进食/穿戴/唤醒等原版分支仍按 EXPie 处理
  - 选角立绘 `Loading/char4.png` + `char4outline.png`（白色剪影），由 2K 壁纸
    去背、像素化（168px 高、48 色）生成，工具 `tools/art/make_char4.py`
  - 联机：`Together.CharacterSpawn.ClampChar` 的 0..2 限制会放开（检测到
    `CasualtiesMP.dll` 才打这个补丁）
- **与 EXPie 分离**：`FishSkin` / `BodyProportion` / `PlayerSkills` / `PlayerCarry` /
  `BloodRed` 现在只对带 `Tag` 的身体生效。选原版 EXPie / Milky / Dune 时：
  外观、身高、开局属性、负重倍率、血色全部保持原版
- **英文台词补全**：`voice.en-US.json` 的 `expand` 从 34 类补到 89 类
  （新增 55 类 / 189 句）。主角所有状态、行为、遭遇台词都用 SF-07 自己的英文重写，
  不再回退到游戏原版的 EXPie 台词；只有商人 / NPC 的回应保持原版英文
- **语言切换入口**：MOD 管理 → 模块页新增「通用 / 文本语言」一行，
  点一下按 `跟随游戏 → 中文 → English` 循环，写回 `[General] Language`。
  切换后台词库、界面文案、物品名、档案立刻按新语言重载；少数已生成的 TMP 文本
  重启后完全一致
- **主菜单「进入游戏」文字上移**：不再按固定 43px 偏移猜中心点，改成按文字框
  实际高度把字底边抬到立绘包围盒之上（`TextGapTop` 43 → 56，再加半个字高，
  上限 30px）。头顶那对竖鳍不会再压住字
- **血袋**：名称去掉「（与 SF-07 不匹配）」后缀，警告句改成红色富文本
  （`<color=#E05A5A>`），中英文同步
- **可可碱不耐受**：`Toxin` 给 `Liquids.Registry` 里 coffee / chocolatemilk /
  energydrink 的 `onDrink` 包一层，SF-07 额外吃 `CocoaMultiplier-1` 倍疾病
  （默认 ×3）；固体巧克力棒走 `Body.Eat`，每根 +90×(倍率-1) 疾病。
  `[Mechanics] CocoaToxin` / `CocoaMultiplier` 可调。原版三个角色不受影响

## 0.22.0 — 2026-11-23

- **英文版 / English localization**
  - 新增 `items.en-US.json`、`voice.en-US.json`、`lore.en-US.json`、`ui.en-US.json`；
    `[General] Language` 支持 `auto` / `zh-CN` / `en-US`，`auto` 跟随游戏语言
    （`Locale.currentLangName`：`English` → en-US，`简体中文` → zh-CN）
  - 11 篇档案、sf10 / sf11 三版芯片状态分支、剧情关键台词、全部界面文案都已英文化
  - 界面里原先硬编码在 `ModMenu` / `DlcPanel` / `NewRunPanel` / `Jammer` / `SaveSlots` /
    `WeaponHud` / `HudButton` 的文字，搬进 `ui.*.json` 的 `strings` 段（221 条）
  - 英文 `expand` 只收录剧情关键情境；未收录的通用状态台词**保留游戏自带的英文台词**，
    不会退回中文（`FishVoice` 的“没有我的就保留原版”逻辑）
- **血袋提示**：黄色血袋 `bloodbag` 和红血袋 `bloodbaghuman` 的名称/描述都强制改写为
  「与 SF-07 不匹配」，描述里写明输注会导致严重恶心、发烧和排斥反应。
  中英文同步（`items.*.json` + `FishLocalization.ForceText`）
- **修 F4 快速存档后的鬼影 / 商人舱歪倒**
  - 写回地图后调用 `WorldGeneration.ClearBlockDamages()`，销毁爆炸留下的方块伤害贴花
  - `RefreshChunks` 在 `SetTiles` 之后补发 `ChunkUpdated`，刷新碰撞体 / `ChunkScript`
    （只换贴图不刷碰撞体，是商人舱陷进地形、歪倒的根因）
  - 新增 `ResetBuildings`：读档后把 `BuildingEntity` 的速度/角速度归零、旋转归零
- **关掉 DLC 后仍有中文独白**：`FishVoice.SayLine / Sequence / RandomLine` 增加
  `ModState.Enabled` 硬门槛；停用时即使有残留调用也不会再出 mod 台词。
  如果听到的是游戏自带中文，那是游戏语言设置，不是本 MOD 的文本。
- **主菜单双描边**：`MenuArt` 换轮廓 sprite 时把主立绘的 RectTransform 一并抄给
  outline Image（原来只换图、不换矩形，旧美术的框和新轮廓错开就成了双线）；
  轮廓 alpha 阈值从 24 降到 0，消除抗锯齿边缘的透明缝
- **界面/文本同步**：`HealthPanel.BodyStat` 改为读 `BodyProportion.HeightCm`
  （之前固定写 171cm，和 170cm 的体型设置打架）

## 0.21.1 — 2026-11-21

- **档案全文重写（sf04~sf11），统一到前三篇的语气**
  - 长句为主，心理、动作、环境都写足；删掉「这一点，我认」「台阶走到头，是天」
    「皮先裂开，然后是肌肉」这类短促的警句式，也不再出现「不是……而是……」
    「流程我记得住：……」这种句式
  - 叙事视角固定在 STEM-Fish 自己身上：他会犹豫、会自嘲、会算账，不用旁白替他总结
  - sf04 补上 EPDA 的来处（从尸体手里掰下来的）；sf05 把「计时杀人」整场放进正文，数到四十三；
    sf06 补冰崖、冻尸、自己动刀失败的全过程；sf07 补记录员的死状与那行「耀斑那两天」；
    sf08 的商人只按买卖规矩办事，屏蔽器是从舱里递出来的；sf09 明确货物是无线供能装置，
    「给舱供电」只是顺带；sf10 写足钻地舱、三个干尸、裂口、货物钻进腹部的全过程；
    sf11 写足猩红长阶、对公司「不留签名」的谴责，以及到实验室后怎么用手术前的空档发难
  - 三层变体（`unchipped` / `declined` / `none`）全部重写，三版结尾各自成立
- **屏蔽器独白重写**（`voice.zh-CN.json` 的 `lines.jammer`）：五段改成连贯的自述，
  不再用「背上的东西刚钉进去」这种和实际（腹部）不符的说法；面板加大到 860×620，
  日志区 804×420，五行独白不再溢出
- **物品描述修正**：屏蔽器底部有 `M-114` 编号（原来写「没有任何编号」，和档案矛盾）
- **部署脚本改成覆盖式拷贝**：本机的批量删除保护会在清理皮肤子目录时把目录移走再失败，
  导致 `cp` 没执行、皮肤包缺目录。现在只覆盖不删除（源码里删掉的贴图如需清理请手动删）
- 顺手删掉皮肤包里误放的 Windows 快捷方式 `.lnk`

## 0.21.0 — 2026-11-21

- **档案整体重写（sf04~sf11），按官方层名重排**
  `1 砾石荒原 / 2 深层砾石带 / 3 干旱沙漠 / 4 辐射废土 / 5 深林丛渊 / 6 覆冰断崖 /
   7 真菌洼地 / 8 晶蚀空穴 / 9 地热喷口 / 10 必须抵达之地 / 11 猩红长阶`
  —— 每篇的 subtitle 直接写层名，不再是干巴巴的"第 N 层"
- **屏蔽器线索贯穿第 4~10 层**（每层只推进一格，不重复）：
  L4 档案里的「维护附注 4.3：现场维护须先以便携式屏蔽器（列装件 M-114）使装置离线」→
  L5 看完计时杀人，"先得把它弄到手" → L6 自己动刀失败，"刀走不通，先让它闭嘴" →
  L7 死者的观测笔记"耀斑那两天整层没人被计时" → L8 舱里商人的货架标签"一次性，只认一层" →
  L9 算出"必须在上第十层之前拿到" → L10 到手（买的/没买到，见变体）
- **档案支持"按芯片状态分支"**：`Entry.variants`（`when: unchipped / declined / none`）
  sf10、sf11 的批注各有三版：拆了 / 留着 / 根本没拿到。
  状态由 `LoreData.ChipState()` 现算（`WorldGeneration.unchipped` → `runSettings` 标记 → 背包里还揣着屏蔽器）
- **世界观校正**（照原版反汇编与 `Lang/zh-CN.json`）：
  - 货物是 `BlueHeart`：巴掌大、蓝色、心形、脉动发光；玩家靠近约三步时它自己飞过来**钻进腹部**
    （`Body.EmbedCargo`，播 gore，四肢出血 ×0.2，随后 `Heal(false)`）→ 受伤 + "充能"
  - 钻地舱 `highpowereddrillpoddsc` = "……你需要某种大功率电源才能让其正常工作" →
    **货不是给公司的快递，是你上楼用的电池**
  - 其它 SAW 只有两种形态：尸体、和坐在舱里卖东西的商人（EXPie / Milky / June）→
    删掉"活着的攀爬者"，改成冰崖上的抓痕 + 冻死的尸体 + 自己动刀失败的尝试
  - 第 11 层只有他一个人走；到地表是**维生舱**来接

## 0.20.1 — 2026-11-21

- **屏蔽器商人价降到 20**（原 180）。实际成交价 = `value × 声望曲线`，落在 10~30 区间
- **买走屏蔽器时商人补一句**：`这会很有用的，伙计。`（`TraderTalk`，挂 `TraderScript.TryPurchase`
  的 prefix+postfix：记下"货还在架上"→ 成交后它被 `items.Remove` 才算买成，钱不够不会误播）
- **修：尸体旁边刷出来的弹药箱经常右键没反应**。两个原因叠在一起：
  - `CorpseScript.Start` 固定把掉落物生成在 `尸体位置 + (随机x, +3)` —— 洞穴常常只有 3~4 格高，
    箱子直接落在**岩层里**；
  - 我们的 `ItemRegistry.GetLoadPrefab` 为了让读档模板别乱掉把 `rb.simulated = false` 了，
    而原版 `Instantiate` 会**连这个字段一起克隆** → 尸体掉出来的箱子永远冻在半空，不落地。
  - `Body.DoPickupCheck` 的硬判据是「物品碰撞体中心 `OverlapPoint(Ground)` 必须为空」，
    埋在方块里就直接判"被挡住"（连提示都不出）。
  - 修法：① 读档模板不再写 `simulated=false`（只清速度）；② 新增 `ItemSettle`，
    每 1.5 秒把世界里的自定义战利品（弹药箱 / 屏蔽器）检查一遍：埋在方块里就往上挪到第一个空格、
    还冻着就放开。`[General] SettleLootItems` 可关

## 0.20.0 — 2026-11-13

- **屏蔽器改成剧情岔路口**（不再是一件随手用的道具）
  - 平时右键：没有反应、不消耗
  - 带着它下到**第 10 层**（拿到货物那一层）→ 进层后自动定住玩家（借游戏的暂停，
    时间停住 = 谁也动不了），播 5 段自言自语（`voice.zh-CN.json` 的 `lines.jammer`），
    然后弹出「拆 / 留」两个按钮
  - 拆 → 中度脑损伤 + 头部出血 + **受伤音效（`Body.DoGoreSound`）**，
    `runSettings["unchipped"]=true` → 此后永久无芯片，走向自由结局线
  - 留 → 芯片还在、屏蔽器还在兜里，**本层之内**还能再手动按一次；
    离开第 10 层它就彻底失效（芯片换层换密钥，对不上了）
  - `[Items] JammerDepth` 可改触发层（默认 10）
- **档案改成按层精确解锁**：`lore.zh-CN.json` 每篇新增 `layer` 字段，
  `LoreData.UnlockForDepth(depth)` 只发这一层该出的那篇（没配就退回"下一篇未读"）
- **档案整体重排 + 重写**（对齐 1→10 下降 / 10 层拿货 / 11 层上行 的真实层级）
  - sf01 开局自带（第 1 层），sf02~sf11 分别钉在 2~11 层
  - 第 10 层 sf10 补上「钻地舱里四个背着货的前任」「屏蔽器只认这一层」与抉择
  - 第 11 层 sf11 收到「我来送货。货我不交。」
- 层级语义修正记录：`0` 教程 / `1..10` 下降 / `10` 货物 / `11` 上行 / 之后地表与实验室

## 0.19.3 — 2026-11-13

- **主角的血改成红色（照搬守卫 / 人类那套）**。之前只改了材质上的 `_BloodDark/_BloodLight`
  两个颜色，看着还是黄的，因为黄血其实是**三处独立的东西**：
  - 肢体血渍是一张贴图 `Texture2D "limbBlood"`（均色 223,176,0），挂在 shader graph 烘出来的
    哈希属性上（`_SampleTexture2D_722a364…Texture2D`），只能按"当前贴图叫 limbBlood"反查；
    现在换成皮肤包里的 `Blood/limbBlood.png`（红）
  - 流血粒子 `BleedParticle` / `Special/UnderwaterBleedParticle` 的 `startColor` 是常量
    (1, 0.788, 0)；现在按"颜色等于黄血"识别，改成守卫 `HumanBlood` 的 (0.717, 0, 0.057)
  - 落地/溅墙血渍由 `BleedParticle.isHuman` 决定：false → `blockblood`（黄）/`wallblood`（黄图）。
    现在把 `isHuman` 置真并把 `wallBleed/groundBleed` 换成 `wallbloodhuman` / `Special/blockbloodhuman`
  - 截肢小游戏的 `bloodImage` 是黄图，用 `Image.color` 乘成红
  - 全部只作用于 `PlayerCamera.main.body`（`Limb.Awake` 本来就给每条肢体克隆了一份材质），
    敌人/商人的血不变；**去掉了原来挂在 `Limb.Awake` 上的全局 postfix**
- **档案重写（sf05 的批注 + sf06~sf11 全文）**：换成第一人称、平铺但有重量的写法，
  清掉「不是…而是…」这类句式；主线改为从麻木 → 目睹同类之死 → 想清楚 → 决定反抗
  - sf06 新增《往上爬的那个》全文（原来 body 是空的）
  - sf10 补齐「先让芯片闭嘴才能拆」的因果，明确点名**某种信号屏蔽器**
  - sf11 落到"箱子我带着，货我不交"
- **修 `lore.zh-CN.json` 的 JSON 语法**：文件用 U+00A0（不换行空格）当缩进，Newtonsoft 直接解析失败
  （整份档案会读不出来）。现在全部换成普通空格并统一两格缩进
- 顺手修两个错字：sf01「很幸运的时」→「很幸运的是」、sf02 批注开头的孤立句号

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
