using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  ItemRegistry - 新增物品/装备注册（P0 验证）
//  ---------------------------------------------------------------------------
//  反汇编确认的两件事:
//   1) Item.GlobalItems 是 Dictionary<string, ItemInfo>，在 Item.SetupItems() 里填充
//      → patch SetupItems 的 postfix 追加我们的 ItemInfo 即可注册
//   2) Utils.Create(id,pos,rot) = Instantiate(Resources.Load(id), ...)
//      → 新物品没有自己的 prefab，所以生成时"克隆一个现有物品 prefab"，
//        再改 Item.id + 替换 SpriteRenderer.sprite（贴图来自 textures/<id>.png）
//
//  ItemInfo 关键字段（护甲用）: wearable / wearSlotId / desiredWearLimb /
//   wearableArmor / wearableIsolation / wearableHitDurabilityLossMultiplier /
//   jumpHeightMultChange / wearableVisualOffset
// ============================================================================

public static class ItemRegistry
{
    // 物品定义（后续可外置到 json）
    public class FishItem
    {
        public string id;              // 物品 id（有 textures/<id>.png 时用它替换外观）
        public string template;        // 克隆用的原版 prefab（必须是同类物品，自带 Wearable 配置）
        public string displayName;
        public string description;
        public string category = "custom";  // 原版取值：custom / utility / medical / container
        public string tags = "";            // 原版护甲为空；写入后会 SetTags 解析
        public float weight = 0.5f;
        public int value = 20;
        // 护甲（反汇编确认槽位：torso/outertorso/back/hat/neck/hands/arms/wraps/thigh/knees/feet/mouth/eyes）
        public bool wearable;
        public float wearableArmor = 1f;        // 原版量级 0.25~2
        public float wearableIsolation = 0.1f;
        public float jumpHeightMultChange;
        public float hitDurabilityLoss = 0.75f;
        public int visualOffset = 2;            // 层级偏移（int，原版默认 5）
        public string wearSlotId = "";
        public string wearLimb = "";
        // 穿戴时**不**生成任何附加贴图（耳塞这种看不见的小件用）
        // 反汇编 Wearable.CreateSprites：遍历 secondaryLimbs[i] 建 SpriteRenderer 并赋
        // secondaryLimbSprites[i]，所以清空这两个数组 = 什么都不贴。
        public bool noWearSprite;
        // 使用
        public bool usableOnLimb;
        public int recMin = 0;          // 认知门槛：recognizable = !(recMin > 玩家INT)，0 = 永远认得出
        // 右键使用（原版 ItemInfo.useAction 委托，签名 void (Body, Item)）。
        // 填了就自动把物品设成 usable + onlyHoldInHands（必须拿在手上用）。
        public ItemInfo.Use useAction;
        // 模板自带 Container 时把它摘掉（medkit 这类容器改成"使用型"物品时用）
        public bool stripContainer;

        // 储物（>0 时给这个装备挂 Container 组件，像弹挂/背包一样能装东西）
        public float storageWeight = 0f;      // 总承重
        public float storagePerItem = 0f;     // 单件上限
        public float storageEncMult = 0.5f;   // 负重系数

        // 模板**本身已带** Container 时（如 medkit / toolbox），改写它现有容器的容量
        public float overrideContainerWeight;
        public float overrideContainerPerItem;

        // ---- 枪械（isGun=true 时生效；数值全部对应 GunScript 公开字段）----
        public bool isGun;
        public string cloneInfoFrom = "";     // 复制该 id 的 ItemInfo（保留 useAction/qualities 等）
        public int gunMagCapacity = 30;
        public int gunFiringMode = 2;         // FiringMode: Pump=0 / SemiAuto=1 / Auto=2
        public int gunAmmoType = 1;           // AmmoType: Pistol=0 / Rifle=1 / Shotgun=2
        // ★ 后坐力：优先按"模板枪的原始 knockBack × 这个倍率"算，保证永远比原版同类枪低。
        //   （GunScript.Start 只缓存 render/it，不改 knockBack，所以实例化那一刻读到的就是 prefab 序列化值）
        public float gunRecoilScale = 0.75f;
        public float gunKnockBack = 30f;      // 兜底绝对值：模板 knockBack 读不到（<=0）时才用它
        public float gunVerticalSpread = 0.02f;  // 散布
        public float gunGasTime = 0.12f;      // 连发间隔
        public int gunShotsPerFire = 1;
        public float gunStructureDamage = 120f;
        public float gunAnimalDamage = 80f;
        public float gunLoudness = 1f;
        public float gunConditionLoss = 0.01f;
        public string gunSpriteNormal = "";   // 有弹匣（同时作为物品图标）
        public string gunSpriteNoMag = "";    // 无弹匣
        public float gunScale = 1f;           // 相对"与原版同宽"的放大倍数（1=和 rifle 一样长）
        public float gunPivotX = -1f;         // 归一化轴心：<0 沿用参照贴图的轴心
        public float gunPivotY = -1f;
        public float gunPivotNoMagX = -1f;
        public float gunPivotNoMagY = -1f;
        public float gunDropPx;               // 贴图整体下移多少"贴图像素"（抵肩感，cfg 可调）
        public bool gunDullSound = true;      // 把原枪声做低通处理（更沉闷）

        // ---- 弹药（isAmmo=true 时生效）----
        //  游戏 AmmoType 只有 Pistol=0/Rifle=1/Shotgun=2，我们用 3 = 7.62×51 自定义口径
        public bool isAmmo;
        public bool ammoIsMagazine;           // true = 弹匣；false = 散弹
        public int ammoTypeValue = 3;
        public int ammoMaxRounds;             // 弹匣容量（散弹填 0）
        public Color tint = Color.white;      // SpriteRenderer 着色（借原版贴图改色）
        public bool darkenTemplateSprite;     // 用模板贴图现场生成"去色压暗"版（黑色双肩包）
        public float darkenAmount = 0.5f;     // 压暗系数（0=全黑，1=不变）
        public bool indestructible;           // 不因耐久归零而消失（destroyAtZeroCondition=false）
        public string spriteOverride = "";    // 用 textures/<id>.png 换掉原版贴图
    }

    private static readonly List<FishItem> Items = new List<FishItem>
    {
        // 战术弹挂甲：模板 carapace（胸口层 outertorso / UpTorso）
        // 数值参照原版：carapace armor=1.0 / hitDurability=0.5 / isolation=0.15 / weight=1 / visualOffset=5
        //   → 我们 armor=2.0（躯干减伤除数 1+2=3，即承伤 1/3），更重、更耐打
        new FishItem
        {
            id = "platecarrier",
            template = "carapace",
            displayName = "战术弹挂甲",
            description = "公司配发的沙色弹挂甲，正面三个弹匣袋。挡得住爪子和流弹，挡不住核废料。",
            category = "utility",
            weight = 3.0f,
            value = 320,
            wearable = true,
            // ★ 减伤公式（反汇编 Limb.GetArmorReduction）：
            //     实际承伤 = 伤害 ÷ (1 + 本肢体所有 wearable 的 wearableArmor 之和)
            //   → 想要 70% 减伤（承伤 30%）就要 1/(1+armor)=0.3 → armor = 2.333
            wearableArmor = 2.333f,
            wearableIsolation = 0.28f,
            jumpHeightMultChange = -0.06f,
            hitDurabilityLoss = 0.45f,
            visualOffset = 5,               // 与原版 carapace 一致（层级正确，不会被血污/特效盖住）
            wearSlotId = "outertorso",
            wearLimb = "UpTorso",
            // 弹挂甲自带储物：三个弹匣袋 + 杂物兜（5u ≈ 两个弹匣 + 少量杂物）
            storageWeight = 5f,
            storagePerItem = 2f,
            storageEncMult = 0.5f,
        },
        // MDRZ9 7.62×51 突击步枪（无托；克隆原版 rifle，改 GunScript 数值 + 换侧面像素图）
        //  现实原型 Desert Tech MDRX 的 .308/7.62×51 版用 SR-25 规格弹匣，标准 20 发
        new FishItem
        {
            id = "mdrz9",
            template = "rifle",
            cloneInfoFrom = "rifle",          // 复制原版步枪的 ItemInfo（useAction/qualities/rec 等）
            displayName = "MDRZ9 7.62×51",
            description = "公司给的实验品：无托布局、短枪管、.308 口径。二十发一匣，打起来像在敲铁皮。",
            weight = 3.9f,
            value = 420,
            isGun = true,
            gunMagCapacity = 20,              // MDRX 7.62×51 = SR-25 规格 20 发弹匣
            gunFiringMode = 2,                // 全自动
            gunAmmoType = 3,                  // ★ 自定义口径 7.62×51（见 AmmoCompat）
            gunRecoilScale = 0.72f,           // ★ 后坐力 = 原版 rifle 的 72%
            gunKnockBack = 26f,               // 兜底值（模板 knockBack 读不到时才用）
            gunVerticalSpread = 0.02f,        // 大口径：散布略大于 5.56
            gunGasTime = 0.118f,              // ≈ 510 RPM，比 5.56 慢
            gunShotsPerFire = 1,
            gunStructureDamage = 185f,        // ★ 7.62 威力显著高于 5.56（130）
            gunAnimalDamage = 145f,           // ★（90）
            gunLoudness = 1.45f,              // 更响
            gunConditionLoss = 0f,            // ★ 射击不消耗耐久（后续可能改成"可保养"，现在直接不掉）
            gunSpriteNormal = "mdrz9",
            gunSpriteNoMag = "mdrz9_nomag",
            gunScale = 1.4f,                  // 比原版步枪长 40%（之前与 rifle 同宽，显得太小）
            gunPivotX = 0.444f,               // 握把中心（108x34 像素图里 x=48, y≈26）
            gunPivotY = 0.235f,
            gunPivotNoMagX = 0.444f,          // 无弹匣图 108x32：底部少 2 行
            gunPivotNoMagY = 0.188f,
            gunDropPx = 4f,                   // 整体下移 4 像素 → 枪托落在肩线（cfg `[Guns] mdrz9DropPx`）
            gunDullSound = true,              // 步枪枪声过三级低通 → 沉闷的 .308 声
        },
        // 7.62×51 弹匣（克隆原版 riflemagazine，口径改 3，20 发，贴图用 MDRZ9 的弹匣图）
        new FishItem
        {
            id = "mdrz9mag",
            template = "riflemagazine",
            cloneInfoFrom = "riflemagazine",
            displayName = "MDRZ9 弹匣（7.62×51）",
            description = "二十发直弹匣，钢壳 .308。比 5.56 弹匣肥一圈，塞不进 AR 的匣口。",
            category = "",                    // 沿用原版弹匣的分类
            weight = 0.65f,
            value = 85,
            isAmmo = true,
            ammoIsMagazine = true,
            ammoTypeValue = 3,
            ammoMaxRounds = 20,
            spriteOverride = "mdrz9_mag",
        },
        // 7.62×51 散弹（借 5.56 的贴图，染色区分）
        new FishItem
        {
            id = "mdrz9round",
            template = "556round",
            cloneInfoFrom = "556round",
            displayName = "7.62×51 子弹",
            description = "一发 .308 温彻斯特。比 5.56 长一截，弹壳更粗，装不进小口径弹匣。",
            category = "",                    // 沿用原版子弹的分类
            weight = 0.03f,
            value = 12,
            isAmmo = true,
            ammoIsMagazine = false,
            ammoTypeValue = 3,
            tint = new Color(1f, 0.72f, 0.52f),   // 偏铜红，和 5.56 的黄铜色区分
        },
        // 黑色双肩包：模板 bigpack（背在 back 槽），容量更大、不损坏
        //  贴图不另存 PNG：创建时把 bigpack 的图去色 + 压暗现算一张（SpriteTint）
        new FishItem
        {
            id = "blackpack",
            template = "bigpack",
            cloneInfoFrom = "bigpack",
            displayName = "黑色双肩包",
            description = "标准的双肩背包，布面被染成了纯黑。装得比看上去多。",
            weight = 1.2f,
            value = 70,
            overrideContainerWeight = 16f,    // 比原版大背包更能装
            overrideContainerPerItem = 4f,
            darkenTemplateSprite = true,
            darkenAmount = 0.42f,
            indestructible = true,
        },
        // 弹药箱：模板 medkit（贴图由 textures/ammobox.png 覆盖），但**不是容器** ——
        //  stripContainer 把模板自带的 Container 摘掉，改成右键使用：
        //  原地倒出一堆弹药，然后箱子消失（AmmoBox.Use）。
        //  随机刷新走 ItemLootPool 注入（见 AmmoBox.Tick）
        new FishItem
        {
            id = AmmoBox.BoxId,
            template = "medkit",
            cloneInfoFrom = "medkit",
            displayName = "弹药箱",
            description = "军绿色铁皮箱，锁扣早就锈断了。拿在手里右键打开，就地倒出一堆弹药。",
            category = "utility",
            weight = 6.0f,
            value = 120,
            stripContainer = true,
            useAction = AmmoBox.Use,
        },

        // 屏蔽器（调试物品）：使用 = 太阳耀斑（芯片离线）+ 拆除芯片抉择
        new FishItem
        {
            id = "jammer",
            template = "medkit",
            displayName = "屏蔽器",
            description = "来路不明的应急屏蔽器，外壳上没有任何厂牌和编号。启动后能像太阳耀斑一样把颅内芯片压掉，并给你一次拆掉它的机会。",
            category = "utility",
            weight = 0.6f,
            value = 180,
            stripContainer = true,
            useAction = Jammer.Use,
        },
        // 战术头盔：模板 riothelmet（hat / Head）
        // 数值参照原版：riothelmet armor=2.0 / hitDurability=0.3 / isolation=0.2 / weight=0.8 / visualOffset=8
        //   → 我们 armor=2.5（头部除数 1+2.5=3.5，承伤约 29%）
        new FishItem
        {
            id = "tachelmet",
            template = "riothelmet",
            displayName = "战术头盔",
            description = "盖住头盖骨的战术头盔，前面挂一副护目镜。压得耳朵难受，但脑袋比面子重要。",
            category = "custom",
            weight = 1.2f,
            value = 280,
            wearable = true,
            wearableArmor = 2.333f,         // 同样 70% 减伤（见弹挂甲处的公式说明）
            wearableIsolation = 0.35f,
            jumpHeightMultChange = 0f,
            hitDurabilityLoss = 0.3f,
            visualOffset = 8,               // 与原版 riothelmet 一致
            wearSlotId = "hat",
            wearLimb = "Head",
        },
        // 战术耳塞：和头盔**同时**穿戴，无防护无容量、不掉耐久，抗射击噪音造成的听力损伤
        // ---------------------------------------------------------------------------
        //  ★ 槽位用 "ears"：Body.WearWearable 只用 wearSlotId 判冲突
        //    （`GetWearableBySlotID(item.Stats.wearSlotId)` → 有值就"已经穿着这个"），
        //    原版槽位只有 torso/outertorso/back/hat/neck/hands/arms/wraps/thigh/
        //    knees/feet/mouth/eyes，**没有 ears** → 独占一个槽位，永远不会和头盔打架。
        //
        //  ★ visualOffset 取负：WearWearable 把物品的 SpriteRenderer 挂到肢体下、
        //    sortingOrder = 头.sortingOrder + wearableVisualOffset。负值 = 画在头**后面**
        //    → 小件不显示在脸上，但物品栏图标照常。原版 riothelmet 是 +8，我们用 -3。
        new FishItem
        {
            id = "earplugs",
            template = "riothelmet",        // 只需要它的 Wearable 组件做壳
            displayName = "战术耳塞",
            description = "两枚橙色泡沫耳塞，连着一根细绳。挡不住枪声，但挡得住枪声之后那阵耳鸣。",
            category = "custom",
            weight = 0.05f,
            value = 45,
            wearable = true,
            wearableArmor = 0f,             // 无防护
            wearableIsolation = 0f,         // 无隔离
            jumpHeightMultChange = 0f,
            hitDurabilityLoss = 0f,         // 被打时耐久乘数 0 → 不掉耐久
            indestructible = true,          // 耐久归零也不消失（双保险）
            visualOffset = 0,               // 穿戴后整体隐藏（WearHide），偏移量无所谓
            wearSlotId = "ears",
            wearLimb = "Head",
            noWearSprite = true,            // 不生成任何穿戴贴图
        },

        // =====================================================================
        //  开局三件"贴皮衣物"（v0.17.1）
        //  ---------------------------------------------------------------------
        //  外观已经画在皮肤上了 → 全部 noWearSprite（不生成任何附加贴图）；
        //  hitDurabilityLoss=0 + indestructible → 打不坏；
        //  脱下由 Body.DropWearable 的 prefix 拦掉（见 StarterKit.PatchUnremovable）。
        //  槽位用自定义字符串（"coat" / "legs"），Body 只按字符串判冲突，不会被原版装备挤掉。
        // =====================================================================
        new FishItem
        {
            id = "fishboots",
            template = "tacticalboots",     // 原版战术靴：自带 feet 槽 + 防滑/落地缓冲
            displayName = "黑色硬胶靴",
            description = "一整块黑色硬胶压成的靴子，鞋底有细密纹路。落地的时候膝盖会替你骂人。",
            category = "utility",
            weight = 1.2f,
            value = 90,
            wearable = true,
            wearableArmor = 0f,             // 护甲由 CoverArmor 按部位统一给（见下）
            wearableIsolation = 0.2f,
            jumpHeightMultChange = 0.04f,
            hitDurabilityLoss = 0f,
            indestructible = true,
            visualOffset = 0,
            wearSlotId = "feet",
            wearLimb = "FootF",
            noWearSprite = true,
        },
        new FishItem
        {
            id = "fishcoat",
            template = "carapace",          // 借壳：只要一个 Wearable 组件
            displayName = "灰色冲锋衣",
            description = "灰扑扑的旧冲锋衣，肘部和肩口有一层薄薄的复合衬。挡风，也挡一点爪子。",
            category = "utility",
            weight = 1.4f,
            value = 120,
            wearable = true,
            wearableArmor = 0f,
            wearableIsolation = 0.3f,
            hitDurabilityLoss = 0f,
            indestructible = true,
            visualOffset = 0,
            wearSlotId = "coat",
            wearLimb = "UpTorso",
            noWearSprite = true,
        },
        new FishItem
        {
            id = "fishpants",
            template = "carapace",
            displayName = "灰黑复合长裤",
            description = "灰黑两色的复合纤维长裤，膝弯处补过一层。耐磨，而且看不出脏。",
            category = "utility",
            weight = 1.1f,
            value = 110,
            wearable = true,
            wearableArmor = 0f,
            wearableIsolation = 0.25f,
            hitDurabilityLoss = 0f,
            indestructible = true,
            visualOffset = 0,
            wearSlotId = "legs",
            wearLimb = "ThighF",
            noWearSprite = true,
        },
    };

    // 枪械贴图尺寸的配置覆盖（cfg 里可调，1 = 与原版同型枪一样长）
    private static readonly Dictionary<string, float> _gunScaleCfg = new Dictionary<string, float>();
    private static readonly Dictionary<string, float> _gunDropCfg = new Dictionary<string, float>();

    public static void Init(Plugin plugin)
    {
        try
        {
            // 生成时读这里，改 cfg 重启即可生效（不用重编译）
            foreach (var it in Items)
                if (it.isGun)
                {
                    _gunScaleCfg[it.id] = plugin.Config.Bind("Guns", it.id + "Scale", it.gunScale,
                        $"「{it.id}」贴图相对原版同型枪的长度倍数（1=一样长，越大越长）").Value;
                    _gunDropCfg[it.id] = plugin.Config.Bind("Guns", it.id + "DropPx", it.gunDropPx,
                        $"「{it.id}」贴图整体下移多少贴图像素（正数=往下，用来调到抵肩位置）").Value;
                }

            var harmony = new Harmony("com.stemfish.dlc.items");
            var target = AccessTools.Method(typeof(Item), "SetupItems");
            if (target == null)
            {
                Plugin.Log.LogError("[STEMFishDLC] 找不到 Item.SetupItems");
                return;
            }
            var postfix = typeof(ItemRegistry).GetMethod("SetupItems_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            var info = Harmony.GetPatchInfo(target);
            Plugin.Log.LogInfo($"[STEMFishDLC] 物品注册 hook 完成 | postfix={info?.Postfixes?.Count ?? -1}");

            // 废用容器（弹药箱这类 stripContainer 物品）禁止打开
            var open = AccessTools.Method(typeof(PlayerCamera), "OpenContainer");
            if (open != null)
            {
                var pre = typeof(ItemRegistry).GetMethod("OpenContainer_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(open, prefix: new HarmonyMethod(pre));
                Plugin.Log.LogInfo("[STEMFishDLC] 容器打开拦截 hook 完成");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 物品注册 hook 失败: {e}");
        }
    }

    // 游戏建好原版物品表之后，追加我们的
    private static void SetupItems_Postfix()
    {
        try
        {
            if (Item.GlobalItems == null) return;
            foreach (var it in Items)
            {
                var info = BuildInfo(it);
                Item.GlobalItems[it.id] = info;
                // 兼容旧存档：v0.4.0 把 "mdr" 改名成 "mdrz9"，老物品仍要有 ItemInfo（否则 KeyNotFound）
                if (it.id == "mdrz9" && !Item.GlobalItems.ContainsKey("mdr")) Item.GlobalItems["mdr"] = info;
                // 显示名/描述（游戏查不到 key 时由 FishLocalization 补位）
                // 文本：先登记内置文案，再由 ItemText 决定最终用哪一份（JSON 优先）
                ItemText.SetBuiltin(it.id, it.displayName, it.description);
                FishLocalization.Register(it.id, ItemText.Name(it.id) ?? it.displayName,
                                                    ItemText.Desc(it.id) ?? it.description);
                Plugin.Log.LogInfo($"[STEMFishDLC] 注册物品: {it.id} ({it.displayName})");
            }

            // 物品名/描述的外部文本表（plugins/STEMFishDLC/items.zh-CN.json）
            ItemText.EnsureLoaded();
            ItemText.ExportTemplateIfMissing();
            Plugin.Log.LogInfo($"[STEMFishDLC] 物品注册完成：{Items.Count} 件");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 追加物品失败: {e}");
        }
    }

    private static ItemInfo BuildInfo(FishItem it)
    {
        // 需要沿用原版某件物品的完整配置（尤其 useAction 委托）时，先整份复制再覆盖
        ItemInfo info = null;
        if (!string.IsNullOrEmpty(it.cloneInfoFrom))
        {
            try
            {
                if (Item.GlobalItems != null && Item.GlobalItems.TryGetValue(it.cloneInfoFrom, out var src) && src != null)
                {
                    info = CloneItemInfo(src);
                    Plugin.Log.LogInfo($"[STEMFishDLC] {it.id}: 已复制原版 ItemInfo '{it.cloneInfoFrom}'");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 复制 ItemInfo 失败: {e.Message}"); }
        }
        if (info == null)
        {
            // ⚠ ItemInfo 默认值（反汇编 ctor）：wearableVisualOffset=5, tags="", rec=new Recognition(2), qualities=new List
            info = new ItemInfo
            {
                category = it.category,          // ★ 必须用原版分类值（custom/utility/medical/container）
                scaleWeightWithCondition = true,
                destroyAtZeroCondition = true,
                slotRotation = 0f,
                tags = it.tags ?? "",
            };
            // ★ Recognition.recognizable = !(rec.min > 玩家INT)：默认 2 会让低 INT 时显示"未知物体"
            info.rec = new Recognition(it.recMin);
        }

        // 覆盖为我们的名字/描述/重量/价格
        info.fullName = it.displayName;
        info.description = it.description;
        info.weight = it.weight;
        info.value = it.value;
        if (!string.IsNullOrEmpty(it.category)) info.category = it.category;

        // 复制来的原版步枪 rec=10（INT<10 会显示"未知物体"）→ 我们的装备一律可识别
        if (it.isGun) info.rec = new Recognition(0);

        // 不损坏：耐久归零也不消失
        if (it.indestructible) info.destroyAtZeroCondition = false;

        // ★★★ 关键：actualTags 只由 SetTags() 生成，不调用则 ItemInfo.HasTag 里
        //     actualTags.Contains(...) 抛 ArgumentNullException → Body.Update/PlayerCamera.LateUpdate 每帧刷屏卡死
        info.SetTags();

        if (it.wearable)
        {
            info.wearable = true;
            info.wearableArmor = it.wearableArmor;               // 原版量级 0.25~2（不是 0~1 比例）
            info.wearableIsolation = it.wearableIsolation;
            info.jumpHeightMultChange = it.jumpHeightMultChange;
            info.wearSlotId = it.wearSlotId;
            info.desiredWearLimb = it.wearLimb;
            info.wearableCanBeHeld = true;
            info.wearableHitDurabilityLossMultiplier = it.hitDurabilityLoss;
            info.wearableVisualOffset = it.visualOffset;         // 原版 0.1~2（默认 5 会盖住一切）
        }
        if (it.usableOnLimb)
            info.usableOnLimb = true;

        // 自定义"使用"：原版 useAction 签名是 void(Body, Item)。
        // usable=true 才会进右键菜单；onlyHoldInHands 保证必须拿在手里才触发；
        // usableOnLimb 必须关掉，否则游戏会走 useLimbAction（医疗类那条路）。
        if (it.useAction != null)
        {
            info.usable = true;
            info.onlyHoldInHands = true;
            info.usableWithLMB = false;
            info.usableOnLimb = false;
            info.useLimbAction = null;
            info.useAction = it.useAction;
        }

        return info;
    }

    // ==========================================================================
    //  生成自定义物品（克隆模板 prefab + 换 id / 贴图）
    // ==========================================================================
    public static Item Create(string id, Vector2 pos, float rot = 0f)
    {
        var def = Find(id);
        if (def == null)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 未注册的物品: {id}");
            return null;
        }
        try
        {
            var prefab = GetTemplate(def);
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 模板 prefab 不存在: {def.template}");
                return null;
            }
            var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, 0f, rot));
            if (go == null) return null;

            var item = go.GetComponent<Item>();
            if (item != null) item.id = def.id;  // 关键：换成我们的 id（在 SetActive 之前，Awake 就能拿到正确 id）

            // 模板自带 Container 时摘掉（medkit 这类容器 → 改成"右键使用"的消耗型物品）。
            // 必须 DestroyImmediate：Destroy 是帧末执行，Item.Awake 里 GetComponent<Container>()
            // 仍会拿到它，于是物品又能被拖起来打开空容器。
            if (def.stripContainer)
            {
                var cont = go.GetComponent<Container>();
                if (cont != null)
                {
                    // ★ 不能 Destroy 这个组件：Item.Awake 已经把 cont 缓存进私有字段，
                    //   销毁后 Item.HandleDecay 每帧调 cont.itemCount → 空引用（实测一局 19 万条）。
                    //   改成「容量清零 + 加入禁止打开名单」：组件还在（字段有效），但装不了东西、
                    //   也打不开容器 UI，对外仍然是"使用型"物品。
                    cont.maxWeight = 0f;
                    cont.maxWeightPerItem = 0f;
                    try { _blockedContainers.Add(cont.GetInstanceID()); } catch { }
                    Plugin.Log.LogInfo($"[STEMFishDLC] {def.id}: 模板自带的 Container 已废用（容量 0，禁止打开）");
                }
            }

            // ---- 枪械参数：必须在激活前设好（GunScript.Start 用这些字段初始化贴图/状态）----
            if (def.isGun)
            {
                var gun = go.GetComponent<GunScript>();
                if (gun != null) ApplyGunConfig(gun, def, go);
                else Plugin.Log.LogWarning($"[STEMFishDLC] {id}: 模板 prefab 上没有 GunScript");
            }

            // ---- 弹药：口径/容量/贴图 ----
            if (def.isAmmo) ApplyAmmoConfig(go, def);

            // ---- 去色压暗的模板贴图（黑色双肩包）----
            if (def.darkenTemplateSprite)
            {
                var srr = go.GetComponent<SpriteRenderer>();
                if (srr == null) srr = go.GetComponentInChildren<SpriteRenderer>(true);
                if (srr != null && srr.sprite != null)
                {
                    var dark = SpriteTint.Darken(srr.sprite, def.darkenAmount, 0f);
                    if (dark != null)
                    {
                        srr.sprite = dark;
                        ItemCollider.Fit(go, dark, def.id);
                        Plugin.Log.LogInfo($"[STEMFishDLC] {def.id}: 已生成去色压暗贴图 {dark.rect.width}x{dark.rect.height}");
                    }
                }
            }

            // ---- 模板自带容器（medkit/toolbox 等）：改写容量 + 染色 ----
            if (def.overrideContainerWeight > 0f)
            {
                var c = go.GetComponent<Container>();
                if (c != null)
                {
                    c.maxWeight = def.overrideContainerWeight;
                    c.maxWeightPerItem = def.overrideContainerPerItem > 0f ? def.overrideContainerPerItem : def.overrideContainerWeight;
                    Plugin.Log.LogInfo($"[STEMFishDLC] {def.id}: 容器容量 {c.maxWeight}u / 单件 {c.maxWeightPerItem}u");
                }
                else Plugin.Log.LogWarning($"[STEMFishDLC] {def.id}: 模板上没有 Container 组件");

                if (def.tint != Color.white)
                {
                    var srr = go.GetComponent<SpriteRenderer>();
                    if (srr == null) srr = go.GetComponentInChildren<SpriteRenderer>(true);
                    if (srr != null) srr.color = def.tint;
                }
            }

            // 外观：textures/<id>.png 存在时，同一张侧面图同时用于"物品外观"和"穿戴贴图"
            //（枪械的贴图在 ApplyGunConfig 里按世界尺寸单独处理，这里跳过）
            if (!def.isGun && !def.isAmmo && FishAssets.HasTexture(def.id))
            {
                // ---- ① 物品外观：PPU/轴心沿用模板物品自己的图标贴图 ----
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr == null) sr = go.GetComponentInChildren<SpriteRenderer>(true);
                var iconRef = sr != null ? sr.sprite : null;
                var iconSprite = FishAssets.GetSpriteLike(def.id, iconRef);
                if (sr != null && iconSprite != null)
                {
                    sr.sprite = iconSprite;
                    ItemCollider.Fit(go, iconSprite, def.id);   // 碰撞箱贴合物品轮廓
                }

                // ---- ② 穿戴外观：PPU/轴心沿用每一个肢体原本的贴图（保证像素级对齐） ----
                var wearable = go.GetComponent<Wearable>();
                if (wearable != null && def.noWearSprite)
                    ClearWearSprites(wearable, def.id);
                else if (wearable != null)
                    ApplyWearSprites(wearable, def, def.id);

                Plugin.Log.LogInfo($"[STEMFishDLC] 自定义外观已应用: {id}（物品{(def.noWearSprite ? "" : " + 穿戴")}）");
            }
            else if (def.noWearSprite)
            {
                // 没有贴图也要清：否则会残留模板（头盔）的镜片/护目层
                var wearable = go.GetComponent<Wearable>();
                if (wearable != null) ClearWearSprites(wearable, def.id);
            }

            // 穿戴后不显示自身贴图（耳塞：挂在肢体上就把 renderer 关掉，物品栏照常可见）
            if (def.noWearSprite && go.GetComponent<WearHide>() == null)
                go.AddComponent<WearHide>();

            // 带储物的模板是隐藏的，实例需要激活才会跑 Awake（此时 id/Container 都已就绪）
            if (!go.activeSelf) go.SetActive(true);

            return item;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成物品失败 {id}: {e.Message}");
            return null;
        }
    }

    // ==========================================================================
    //  清空穿戴贴图（耳塞这种"看不见"的小件）
    //  ---------------------------------------------------------------------------
    //  Wearable.CreateSprites 的实现：
    //      ClearSprites();
    //      for (i = 0; i < secondaryLimbs.Length; i++)
    //          LimbByName(secondaryLimbs[i]) → 建 SpriteRenderer → 赋 secondaryLimbSprites[i]
    //  所以把两个数组都清成空数组，穿上时循环 0 次 = 身上不出现任何东西。
    //  ★ 不能只把 sprites 设成 null：CreateSprites 会对每一项调 sprite.name，直接 NRE。
    // ==========================================================================
    private static void ClearWearSprites(Wearable wearable, string id)
    {
        try
        {
            wearable.secondaryLimbs = new string[0];
            wearable.secondaryLimbSprites = new Sprite[0];
            wearable.secondaryObjects = new GameObject[0];
            Plugin.Log.LogInfo($"[STEMFishDLC] {id}: 穿戴贴图已清空（身上不显示外观）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 清空穿戴贴图失败 {id}: {e.Message}");
        }
    }

    // ==========================================================================
    //  替换穿戴贴图
    //  ---------------------------------------------------------------------------
    //  Wearable.CreateSprites(Body) 由 Body.WearWearable 在"穿上的瞬间"调用，
    //  它遍历 secondaryLimbs[i] → LimbByName → 在肢体下建 SpriteRenderer 并赋
    //  secondaryLimbSprites[i]（localPosition=0 / localScale=1）。
    //  所以：① 我们在实例化后改数组是安全的（Awake 不生成）；
    //        ② 每一项必须用"它自己原本那张贴图"的 PPU/轴心来创建，才能严丝合缝贴住。
    //  只替换与目标肢体同名的那几项，其余原样保留（模板可能绑了多个部位）。
    // ==========================================================================
    private static void ApplyWearSprites(Wearable wearable, FishItem def, string id)
    {
        try
        {
            var limbs = wearable.secondaryLimbs;
            var sprites = wearable.secondaryLimbSprites;
            if (sprites == null || sprites.Length == 0)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] {id}: 模板 Wearable 没有 secondaryLimbSprites，穿戴外观沿用原版");
                return;
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] {id}: 模板 Wearable limbs=[{(limbs != null ? string.Join(",", limbs) : "null")}] sprites={sprites.Length} 目标肢体={def.wearLimb}");

            int replaced = 0, fallbackIdx = -1;
            for (int i = 0; i < sprites.Length; i++)
            {
                string limb = (limbs != null && i < limbs.Length) ? limbs[i] : null;
                bool match = string.IsNullOrEmpty(def.wearLimb) || (limb != null && limb == def.wearLimb);
                if (!match) continue;

                var orig = sprites[i];
                var worn = FishAssets.GetSpriteLike(id, orig);
                if (worn != null) { sprites[i] = worn; replaced++; }
                if (fallbackIdx < 0) fallbackIdx = i;
            }

            // 模板里没有同名肢体（比如模板绑的是别的位置）→ 退化为替换第一项
            if (replaced == 0)
            {
                int i = fallbackIdx >= 0 ? fallbackIdx : 0;
                var worn = FishAssets.GetSpriteLike(id, sprites[i]);
                if (worn != null)
                {
                    sprites[i] = worn;
                    replaced = 1;
                    Plugin.Log.LogWarning($"[STEMFishDLC] {id}: 模板中没有肢体 '{def.wearLimb}'，已替换第 {i} 项（limb={((limbs != null && i < limbs.Length) ? limbs[i] : "?")}）");
                }
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] {id}: 穿戴贴图替换 {replaced} 项");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 替换穿戴贴图失败 {id}: {e.Message}");
        }
    }

    // ==========================================================================
    //  枪械参数（全部对应 GunScript 公开字段，反汇编确认）
    //  后坐力 knockBack：Fire 里 body.velocity -= transform.right * knockBack
    //  散布 verticalSpread：FireInfo.dir = right + up * Random * verticalSpread
    //  连发间隔 desiredGasTime：每次开火 gasTime = desiredGasTime
    // ==========================================================================
    private static void ApplyGunConfig(GunScript gun, FishItem def, GameObject go)
    {
        try
        {
            var refSprite = gun.normalSprite;          // 原版 rifle 贴图：拿它的世界尺寸当参照
            float vanillaKnockBack = gun.knockBack;    // 模板（原版 rifle）prefab 上的后坐力原值
            float texScale = _gunScaleCfg.TryGetValue(def.id, out var s) && s > 0f ? s : def.gunScale;
            float dropPx = _gunDropCfg.TryGetValue(def.id, out var d) ? d : def.gunDropPx;

            gun.magCapacity = def.gunMagCapacity;
            gun.firingMode = (GunScript.FiringMode)def.gunFiringMode;
            gun.feedType = GunScript.FeedType.Mag;
            gun.ammoType = (GunScript.AmmoType)def.gunAmmoType;
            // 后坐力：按原版同型枪的原始值乘系数，天然保证"比原版轻"
            float scale = Mathf.Clamp(def.gunRecoilScale, 0.1f, 1.5f);
            gun.knockBack = vanillaKnockBack > 0.01f ? vanillaKnockBack * scale : def.gunKnockBack;
            gun.verticalSpread = def.gunVerticalSpread;
            gun.desiredGasTime = def.gunGasTime;
            gun.shotsPerFire = def.gunShotsPerFire;
            gun.structureDamage = def.gunStructureDamage;
            gun.animalDamage = def.gunAnimalDamage;
            gun.loudness = def.gunLoudness;
            gun.conditionLossPerShot = def.gunConditionLoss;

            gun.roundsInMag = 0;
            gun.hasMag = false;
            gun.racked = false;
            gun.safe = false;

            // 耐久拉满：不消耗耐久的前提下让枪看起来/用起来都是完好状态
            var gunItem = go.GetComponent<Item>();
            if (gunItem != null) gunItem.condition = 1f;

            // 贴图：世界宽度 = 原版 rifle × gunScale，轴心放到握把 → 手正好握住握把
            // dropPx：轴心再往上抬，贴图相对手往下 → 枪托落到肩线（抵肩感）
            var sNormal = string.IsNullOrEmpty(def.gunSpriteNormal) ? null
                : FishAssets.GetGunSprite(def.gunSpriteNormal, refSprite, texScale, def.gunPivotX, def.gunPivotY, dropPx);
            var sNoMag = string.IsNullOrEmpty(def.gunSpriteNoMag) ? null
                : FishAssets.GetGunSprite(def.gunSpriteNoMag, refSprite, texScale, def.gunPivotNoMagX, def.gunPivotNoMagY, dropPx);
            if (sNormal != null) { gun.normalSprite = sNormal; gun.rackedSprite = sNormal; }
            if (sNoMag != null) { gun.normalSpriteNoMag = sNoMag; gun.rackedSpriteNoMag = sNoMag; }

            // 枪声：把原版 rifle 的开火声过 N 级低通 → 更沉闷（读不到采样就保留原声）
            if (def.gunDullSound && gun.fireSound != null)
            {
                var dull = GunAudio.MakeDull(gun.fireSound, GunAudio.Cutoff, GunAudio.Gain, GunAudio.Stages);
                if (dull != null) gun.fireSound = dull;
            }

            // 物品自身的 SpriteRenderer 也换成我们的图（背包/地上的图标）
            if (sNormal != null)
            {
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr == null) sr = go.GetComponentInChildren<SpriteRenderer>(true);
                if (sr != null) sr.sprite = sNormal;
                // 碰撞箱贴合枪的轮廓（原版 rifle 的箱子比我们高/宽都不对）
                ItemCollider.Fit(go, sNormal, def.id + "_gun");
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 枪械配置 {def.id}: 弹匣={def.gunMagCapacity} 后坐力={gun.knockBack:0.##}（原版 {vanillaKnockBack:0.##} × {scale:0.##}）散布={def.gunVerticalSpread} 间隔={def.gunGasTime}s 伤害={def.gunStructureDamage}/{def.gunAnimalDamage}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 枪械配置失败 {def.id}: {e.Message}");
        }
    }

    // ==========================================================================
    //  弹药配置（弹匣 / 散弹）
    //  AmmoScript 只有 4 个字段：itemType(0=散弹,1=弹匣) / ammoType / maxRounds / rounds
    //  ammoType 用 3 = 7.62×51 自定义口径（反汇编确认全程序集只有 4 处读它，
    //  都只是相等比较，所以加第 4 个值不会越界；卸载弹匣/退弹由 AmmoCompat 接管）
    // ==========================================================================
    private static void ApplyAmmoConfig(GameObject go, FishItem def)
    {
        try
        {
            var ammo = go.GetComponent<AmmoScript>();
            if (ammo == null) ammo = go.GetComponentInChildren<AmmoScript>(true);
            if (ammo == null)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] {def.id}: 模板上没有 AmmoScript");
                return;
            }

            ammo.itemType = (AmmoScript.AmmoItemType)(def.ammoIsMagazine ? 1 : 0);
            ammo.ammoType = (GunScript.AmmoType)def.ammoTypeValue;
            if (def.ammoIsMagazine)
            {
                if (def.ammoMaxRounds > 0) ammo.maxRounds = def.ammoMaxRounds;
                ammo.rounds = ammo.maxRounds;            // 生成即满弹匣
            }

            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null) sr = go.GetComponentInChildren<SpriteRenderer>(true);
            if (sr != null)
            {
                if (def.tint != Color.white) sr.color = def.tint;
                if (!string.IsNullOrEmpty(def.spriteOverride))
                {
                    var s = FishAssets.GetSpriteMatchSize(def.spriteOverride, sr.sprite);
                    if (s != null)
                    {
                        sr.sprite = s;
                        ItemCollider.Fit(go, s, def.id);      // 弹匣的碰撞箱贴合新贴图
                    }
                }
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 弹药配置 {def.id}: {(def.ammoIsMagazine ? "弹匣" : "散弹")} 口径={(int)ammo.ammoType} 容量={ammo.maxRounds} 现有={ammo.rounds}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 弹药配置失败 {def.id}: {e.Message}");
        }
    }

    // 复制一份 ItemInfo（保留 useAction 委托、qualities、rec 等原版配置）
    private static ItemInfo CloneItemInfo(ItemInfo src)
    {
        var dst = new ItemInfo();
        foreach (var f in typeof(ItemInfo).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            try { f.SetValue(dst, f.GetValue(src)); } catch { }
        }
        return dst;
    }

    // ==========================================================================
    //  模板 prefab：需要储物时，先在隐藏模板上挂好 Container，
    //  这样实例化并激活时 Item.Awake 就能缓存到 container（否则 UI 不认）
    // ==========================================================================
    private static readonly Dictionary<string, GameObject> _templates = new Dictionary<string, GameObject>();

    // 被"废用"的容器（stripContainer 的物品）。PlayerCamera.OpenContainer 会拦掉它们。
    private static readonly HashSet<int> _blockedContainers = new HashSet<int>();

    private static bool OpenContainer_Prefix(Container cont)
    {
        try
        {
            if (cont == null) return true;
            return !_blockedContainers.Contains(cont.GetInstanceID());
        }
        catch { return true; }
    }

    // ==========================================================================
    //  读档用"内存模板"
    //  ---------------------------------------------------------------------------
    //  原版 SaveSystem.TryLoadGame 是 `Resources.Load(id)` + Instantiate —— 我们的
    //  自定义物品没有 Resources 资源，读档时会拿到 null、物品直接丢。
    //  这里先用 Create() 造一个配置齐全的实例（id / 贴图 / 穿戴 / 容器全都在），
    //  挪到世界外很远的地方当模板；SaveSlots 的 Resources.Load 前缀把它交回原版，
    //  原版克隆出来的实例 Awake/Start 正常跑，id 也还是我们自己的。
    //  ★ 模板必须保持 active：Instantiate 会连 activeSelf 一起复制，模板关着的话
    //    读出来的物品也是关着的（被拿在手上就不会跑 Update 了）。
    // ==========================================================================
    private static readonly HashSet<string> _loadLogged = new HashSet<string>();

    public static GameObject GetLoadPrefab(string id)
    {
        id = Normalize(id);

        var item = Create(id, new Vector2(0f, -100000f), 0f);
        if (item == null) return null;

        var go = item.gameObject;
        go.name = id + "_loadprefab";

        var rb = go.GetComponent<Rigidbody2D>();
        if (rb != null) { rb.simulated = false; rb.velocity = Vector2.zero; rb.angularVelocity = 0f; }

        // ★ 用完就 Destroy（帧末生效）：原版紧接着会 Instantiate 它，克隆体不受影响。
        //   不能长期留着当模板 —— Item.Update 会对世界外的坐标算 GetClosestChunkRenderer()，
        //   返回 null 后 `.enabled` 每帧空引用（实测刷屏）。每次读档重建一次也不贵。
        UnityEngine.Object.Destroy(go);

        if (_loadLogged.Add(id))
            Plugin.Log.LogInfo($"[STEMFishDLC] {id}: 已生成读档模板（用后即毁）");
        return go;
    }

    private static GameObject GetTemplate(FishItem def)
    {
        if (_templates.TryGetValue(def.id, out var cached) && cached != null) return cached;

        var prefab = Resources.Load<GameObject>(def.template);
        if (prefab == null) return null;

        if (def.storageWeight <= 0f)
        {
            _templates[def.id] = prefab;
            return prefab;
        }

        try
        {
            var holder = UnityEngine.Object.Instantiate(prefab);
            holder.name = def.id + "_template";
            holder.SetActive(false);                    // 常驻隐藏模板
            UnityEngine.Object.DontDestroyOnLoad(holder);

            var cont = holder.GetComponent<Container>();
            if (cont == null) cont = holder.AddComponent<Container>();   // 未激活时不跑 Awake，激活时才初始化
            cont.maxWeight = def.storageWeight;
            cont.maxWeightPerItem = def.storagePerItem > 0f ? def.storagePerItem : def.storageWeight;
            cont.encumberanceMult = def.storageEncMult > 0f ? def.storageEncMult : 1f;

            _templates[def.id] = holder;
            Plugin.Log.LogInfo($"[STEMFishDLC] {def.id}: 已注入储物容器（{cont.maxWeight}u / 单件 {cont.maxWeightPerItem}u）");
            return holder;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 注入储物失败 {def.id}: {e.Message}");
            _templates[def.id] = prefab;
            return prefab;
        }
    }

    public static List<FishItem> All => Items;

    // 这个 id 是不是我们注册的自定义物品（控制台 spawn / Utils.Create 拦截用）
    public static bool IsCustom(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var real = Normalize(id);
        return Items.Exists(x => x.id == real);
    }

    public static FishItem Find(string id) => Items.Find(x => x.id == Normalize(id));

    // 旧 id → 新 id（v0.4.0：mdr → mdrz9）
    private static string Normalize(string id) => id == "mdr" ? "mdrz9" : id;

    // 调试/测试：把指定物品直接塞进玩家背包
    public static void GiveToPlayer(string id)
    {
        try
        {
            var body = PlayerCamera.main?.body;
            if (body == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 生成物品失败：不在世界里");
                return;
            }
            var def = Find(id);
            if (def == null)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 未注册的物品: {id}");
                return;
            }
            var pos = (Vector2)body.transform.position;
            var item = Create(def.id, pos);
            if (item == null)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 物品生成失败: {def.id}");
                return;
            }
            body.AutoPickUpItem(item);
            Plugin.Log.LogInfo($"[STEMFishDLC] 已给予 {def.id}（{def.displayName}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] GiveToPlayer 失败: {e.Message}");
        }
    }
}
