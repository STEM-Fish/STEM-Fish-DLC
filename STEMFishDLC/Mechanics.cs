using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  Mechanics - 三条剧情机制
//  ---------------------------------------------------------------------------
//  ① 游泳：主角是军事线改良体，水里比普通载体灵活
//     反汇编确认 Body 的速度由两个 public 字段控制：
//         Body.maxSpeed   —— 速度上限（神经增强剂就是 maxSpeed *= 1.25）
//         Body.moveForce  —— 加速度/推力
//     原版在水里靠阻力拖慢，不改这两个值。所以我们的做法是：
//         · 入水那一刻把两个值快照下来
//         · 在水里期间按倍率放大
//         · 出水立刻还原成快照值（所以不会永久变快，也不会覆盖别的加成）
//
//  ② 击杀同类（Trader）的心情惩罚
//     TraderScript.OnWillRenderObject 里有一段只跑一次的死亡处理：
//         DropInventory(); didDeathMoodDebuff = true;
//         body.skills.AddExp(1, 60);
//         body.happiness     -= 10;      ← 原版只扣 10
//         body.traumaAmount  += 30;
//         body.sicknessAmount+= 30;
//         talker.Talk("murderregret");
//     我们用 prefix/postfix 检测 didDeathMoodDebuff 由 false 变 true 的那一刻，
//     把 happiness 再补扣到目标值。（不改原版逻辑，避免 transpiler 的脆弱）
//
//  ③ 血袋（黄色 bloodbag / 人血 bloodbaghuman）
//     它们是 LiquidItemInfo + usableOnLimb，使用入口是
//     PlayerCamera.ApplyWoundItem(Item)。在它跑完之后追加：
//         sicknessAmount += 30（发烧/恶心/生病）
//         septicShock    += 8 （脓毒症倾向，和原版 TryLastStand 用的是同一个字段）
//     生理盐水 saline / 林格液 ringersolution 不在此列，保持原版。
// ============================================================================

public static class Mechanics
{
    // ---------------- ① 游泳 ----------------
    public static bool SwimEnabled = true;
    public static float SwimBoost = 1.8f;        // 水中速度/推力倍率

    private static float _savedMax = -1f;
    private static float _savedForce = -1f;
    private static bool _wasInWater;

    // ---------------- ② 交易者 ----------------
    public static bool TraderEnabled = true;
    public static float TraderHappinessLoss = 80f;   // 击杀后 happiness 的目标扣减（原版 10）

    // ---------------- ③ 血袋 ----------------
    public static bool BloodEnabled = true;
    public static float BloodSickness = 30f;     // 生病值增量
    public static float BloodSeptic = 8f;        // 脓毒症增量

    // 只认这两种：黄色血袋（bloodbag）和人血红袋（bloodbaghuman）。
    // bloodsac（血囊）/ bloodcoagulant（止血剂）不在此列，保持原版；
    // 生理盐水 saline、林格液 ringersolution 也不受影响。
    private static readonly string[] BloodBagIds = { "bloodbag", "bloodbaghuman" };

    // ==========================================================================
    public static void Init(Plugin plugin)
    {
        var cfg = plugin.Config;
        try
        {
            SwimEnabled = cfg.Bind("Mechanics", "SwimEnabled", true,
                "主角游泳能力强化（水中移动更快）").Value;
            SwimBoost = Mathf.Clamp(cfg.Bind("Mechanics", "SwimBoostMul", 1.8f,
                "水中移动倍率（1=原版，1.8≈接近陆地速度，2.5=明显快）").Value, 1f, 4f);

            TraderEnabled = cfg.Bind("Mechanics", "TraderKillPenalty", true,
                "击杀同类（Trader）时大幅扣心情").Value;
            TraderHappinessLoss = Mathf.Clamp(cfg.Bind("Mechanics", "TraderHappinessLoss", 80f,
                "击杀 trader 后 happiness 一共扣多少（原版 10，负值范围 -100~100）").Value, 10f, 110f);

            BloodEnabled = cfg.Bind("Mechanics", "BloodBagRisk", true,
                "使用黄色/人血血袋会导致败血症（生理盐水、林格液不受影响）").Value;
            BloodSickness = Mathf.Clamp(cfg.Bind("Mechanics", "BloodBagSickness", 30f,
                "输血后的生病值增量").Value, 0f, 100f);
            BloodSeptic = Mathf.Clamp(cfg.Bind("Mechanics", "BloodBagSeptic", 8f,
                "输血后的脓毒症增量").Value, 0f, 100f);
        }
        catch { }

        var harmony = new Harmony("com.stemfish.dlc.mechanics");
        int n = 0;

        // ---- ① 游泳 ----
        if (SwimEnabled)
        {
            var phys = AccessTools.Method(typeof(Body), "HandlePhysics");
            if (phys != null)
            {
                harmony.Patch(phys, postfix: new HarmonyMethod(
                    typeof(Mechanics).GetMethod("Physics_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                if (Harmony.GetPatchInfo(phys)?.Postfixes != null) n++;
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Body.HandlePhysics");
        }

        // ---- ② 交易者 ----
        if (TraderEnabled)
        {
            var tor = AccessTools.Method(typeof(TraderScript), "OnWillRenderObject");
            if (tor != null)
            {
                harmony.Patch(tor,
                    prefix: new HarmonyMethod(typeof(Mechanics).GetMethod("Trader_Prefix", BindingFlags.NonPublic | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(Mechanics).GetMethod("Trader_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                n++;
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 TraderScript.OnWillRenderObject");
        }

        // ---- ③ 血袋 ----
        if (BloodEnabled)
        {
            var apply = AccessTools.Method(typeof(PlayerCamera), "ApplyWoundItem");
            if (apply != null)
            {
                harmony.Patch(apply, postfix: new HarmonyMethod(
                    typeof(Mechanics).GetMethod("ApplyWoundItem_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                n++;
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 PlayerCamera.ApplyWoundItem");
        }

        // ---- ④ 摔落伤害减免（按当前模式；只有「急速下坠」开了这一项）----
        var impact = AccessTools.Method(typeof(Limb), "ImpactDamage");
        if (impact != null)
        {
            harmony.Patch(impact, prefix: new HarmonyMethod(
                typeof(Mechanics).GetMethod("Impact_Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
            n++;
        }
        else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Limb.ImpactDamage");

        // ---- ⑤ 容器交互诊断（弹药箱这类容器被打开时打一行）----
        var open = AccessTools.Method(typeof(PlayerCamera), "OpenContainer");
        if (open != null)
        {
            harmony.Patch(open, postfix: new HarmonyMethod(
                typeof(Mechanics).GetMethod("OpenContainer_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
            n++;
        }
        else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 PlayerCamera.OpenContainer");

        Plugin.Log.LogInfo($"[STEMFishDLC] 机制就绪 | 合力={n} | 游泳×{SwimBoost:0.0} | " +
                           $"杀同类心情 -{TraderHappinessLoss:0} | 血袋 病+{BloodSickness:0}/脓毒+{BloodSeptic:0}");
    }

    // ==========================================================================
    //  ④ 摔落伤害
    //  ---------------------------------------------------------------------------
    //  Limb.ImpactDamage(float force, Vector2 dir) 开头就判断
    //      if (force <= body.jumpSpeed) return;      // 没超就没伤害
    //  之后 muscleHealth / skinHealth 都按 force 线性扣。
    //  所以把 force 乘一个倍率，等于同时抬高"受伤门槛"和削减伤害量，正是"摔落减免"。
    //  副作用：掉毛、脏污、镜头震动、装备耐久损耗也会按比例减弱 —— 同一次冲击，合理。
    // ==========================================================================
    private static void Impact_Prefix(ref float force)
    {
        try
        {
            var d = RunConfig.Current;
            if (d == null) return;
            if (d.fallDamageMul < 0.999f) force *= d.fallDamageMul;
        }
        catch { }
    }

    // ==========================================================================
    //  ⑤ 容器交互诊断
    //  ---------------------------------------------------------------------------
    //  原版打开容器的路径（反汇编）：按住地上的容器物品拖起来 → 原地松手（位移 < 10px）
    //  → PlayerCamera.HandleReleaseDragging → OpenContainer(Container)。
    //  这里只打日志，不改逻辑：确认弹药箱能走到这一步、里面填充了几件。
    // ==========================================================================
    private static void OpenContainer_Postfix(Container __0)
    {
        try
        {
            if (__0 == null) return;
            var item = __0.GetComponent<Item>();
            float w = 0f, max = 0f;
            try { w = __0.GetHoldingWeight(); max = __0.maxWeight; } catch { }
            Plugin.Log.LogInfo($"[STEMFishDLC] 打开容器: {(item != null ? item.id : "(无 Item)")} | " +
                               $"内容 {__0.itemCount} 件 | 承重 {w:0.0}/{max:0.0}");
        }
        catch { }
    }

    // ==========================================================================
    //  ① 游泳
    //  ---------------------------------------------------------------------------
    //  HandlePhysics 每帧算一次 inWater（用 FluidManager.WaterInfo 判定），
    //  所以 postfix 里读到的 body.inWater 是本帧的最新值。
    //  入水时快照、出水时还原，保证不会永久改变角色数值、也不会跟
    //  「神经增强剂」那类临时加成打架。
    // ==========================================================================
    private static void Physics_Postfix(Body __instance)
    {
        if (__instance == null) return;
        try
        {
            // ★ 只处理玩家自己的 Body。场景里的商人/剧情 NPC 也有 Body，它们的 Update
            //   同样会走到这里；以前用静态 _wasInWater/_savedMax 记状态，会被别的 Body
            //   覆盖，最坏情况把玩家的 maxSpeed/moveForce 写成 0 → 再也走不动。
            var pc = PlayerCamera.main;
            if (pc == null || pc.body != __instance) return;

            bool inWater = __instance.inWater;

            if (inWater && !_wasInWater)
            {
                _savedMax = __instance.maxSpeed;
                _savedForce = __instance.moveForce;
            }

            if (inWater)
            {
                if (_savedMax > 0f) __instance.maxSpeed = _savedMax * SwimBoost;
                if (_savedForce > 0f) __instance.moveForce = _savedForce * SwimBoost;
            }
            else if (_wasInWater)
            {
                // 出水：还原。只在快照有效时还原，避免把别人改的值冲掉
                if (_savedMax > 0f) __instance.maxSpeed = _savedMax;
                if (_savedForce > 0f) __instance.moveForce = _savedForce;
                _savedMax = -1f;
                _savedForce = -1f;
            }

            _wasInWater = inWater;
        }
        catch { }
    }

    // ==========================================================================
    //  调试：解除"不能动"（F2）
    //  ---------------------------------------------------------------------------
    //  剧情过场（BlueHeart / EndLabScript 之类）会把 forceNoMovement 置真，正常情况
    //  协程结束会复位；万一同款问题再出现，这里给一个手动逃生门。
    // ==========================================================================
    public static void Unstick()
    {
        try
        {
            var pc = PlayerCamera.main;
            var b = pc != null ? pc.body : null;
            if (b == null) { SaveSlots.Toast("不在局内"); return; }

            b.forceNoMovement = false;
            b.forceWalk = false;

            var f = AccessTools.Field(typeof(Body), "movingAllowed");
            try { if (f != null) f.SetValue(b, true); } catch { }

            if (b.maxSpeed <= 0f) { b.maxSpeed = 5.5f; Plugin.Log.LogWarning("[STEMFishDLC] maxSpeed<=0，已重置为 5.5"); }
            if (b.moveForce <= 0f) { b.moveForce = 50f; Plugin.Log.LogWarning("[STEMFishDLC] moveForce<=0，已重置为 50"); }

            Plugin.Log.LogWarning($"[STEMFishDLC] 手动解除禁足：forceNoMovement=false forceWalk=false movingAllowed=true | maxSpeed={b.maxSpeed:0.0} moveForce={b.moveForce:0.0}");
            SaveSlots.Toast("已解除禁足");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 解除禁足失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  ② 击杀同类
    //  ---------------------------------------------------------------------------
    //  TraderScript.body 是 **私有属性**，这里用反射取一次并缓存访问器。
    //  didDeathMoodDebuff 是从 false 变 true 的那一帧才补扣，保证只扣一次。
    // ==========================================================================
    private static PropertyInfo _bodyProp;
    private static FieldInfo _deadFlag;

    // didDeathMoodDebuff 是 private 字段，只能反射读
    private static bool DidDeathDebuff(TraderScript t)
    {
        try
        {
            if (_deadFlag == null) _deadFlag = AccessTools.Field(typeof(TraderScript), "didDeathMoodDebuff");
            return _deadFlag != null && t != null && (bool)_deadFlag.GetValue(t);
        }
        catch { return false; }
    }

    private static void Trader_Prefix(TraderScript __instance, out bool __state)
    {
        __state = false;
        try { __state = DidDeathDebuff(__instance); } catch { }
    }

    private static void Trader_Postfix(TraderScript __instance, bool __state)
    {
        if (__instance == null || !TraderEnabled) return;

        try
        {
            // 只有"这一帧刚从 false 变成 true"才处理
            if (__state || !DidDeathDebuff(__instance)) return;

            if (_bodyProp == null)
                _bodyProp = AccessTools.Property(typeof(TraderScript), "body");
            var body = _bodyProp != null ? _bodyProp.GetValue(__instance) as Body : null;
            if (body == null) return;

            // 原版已经扣了 10，这里补到目标值
            float extra = Mathf.Max(0f, TraderHappinessLoss - 10f);
            if (extra <= 0f) return;

            float before = body.happiness;
            body.happiness = Mathf.Max(-100f, before - extra);

            Plugin.Log.LogInfo($"[STEMFishDLC] 击杀同类：心情 {before:0} → {body.happiness:0}（额外 -{extra:0}）");
            try { FishVoice.SayLine(FishVoice.RandomLine("corpse", "……他也是被扔下来的。")); } catch { }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 交易者心情惩罚失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  ③ 血袋
    //  ---------------------------------------------------------------------------
    //  ApplyWoundItem(Item) 是"对选中肢体使用物品"的统一入口。
    //  我们只认三种血袋 id，其余（saline / ringersolution）完全不碰。
    // ==========================================================================
    private static void ApplyWoundItem_Postfix(Item __0)
    {
        try
        {
            if (__0 == null) return;
            if (!IsBloodBag(__0.id)) return;

            var pc = PlayerCamera.main;
            var body = pc != null ? pc.body : null;
            if (body == null) return;

            if (BloodSickness > 0f) body.sicknessAmount += BloodSickness;
            if (BloodSeptic > 0f) body.septicShock += BloodSeptic;

            Plugin.Log.LogInfo($"[STEMFishDLC] 输血（{__0.id}）：生病 +{BloodSickness:0} 脓毒 +{BloodSeptic:0}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 血袋副作用失败: {e.Message}");
        }
    }

    private static bool IsBloodBag(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        for (int i = 0; i < BloodBagIds.Length; i++)
            if (string.Equals(id, BloodBagIds[i], StringComparison.Ordinal)) return true;
        return false;
    }
}
