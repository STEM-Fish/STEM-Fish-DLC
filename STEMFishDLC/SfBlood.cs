using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  SfBlood - SF-07 自己的血，作为一种新的液体
//  ---------------------------------------------------------------------------
//  原版 `Item.DrawBlood(WaterContainerItem, Limb, Item)` 是"在物品栏里对自己用血袋"
//  的唯一入口（`Item.SetupItems` 里 bloodbag 的 useAction → 它）。它固定做两件事：
//      body.bloodVolume -= SpaceLeft / 25
//      bag.AddLiquid("blood", SpaceLeft)          ← 永远给黄色的实验体血液
//
//  我们只在**抽血者是 SF-07** 时，把这一次新加进去的 "blood" 换成自定义的 "sfblood"：
//    · 先记下抽血前袋子里的 blood 量（prefix）
//    · 原方法跑完后算出差值，只把这部分 Drain 掉，再加等量的 sfblood（postfix）
//  这样袋子里原本就有的血/别的液体不会被误改，非 SF-07 的角色完全走原版。
//
//  液体本身：
//    · 红色；唯一一种 SF-07 身体不会排异的血液（注射回血，不加病/脓毒）
//    · id 是 "sfblood"，DNA 检测终端只认 `AmountOf("redblood") >= 100`，
//      所以它不会被当作人类样本（这是原版逻辑，不需要额外拦）
// ============================================================================
public static class SfBlood
{
    public const string Id = "sfblood";

    public static bool Enabled = true;

    private static bool _registered;
    private static bool _patched;
    private static float _before;
    private static bool _logged;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Mechanics", "SfBlood", true,
                "SF-07 抽血时产出自己的红色血液（唯一对他安全的血液）").Value;
        }
        catch { }

        Patch();
    }

    private static string L(string zh, string en, string ru)
        => Lang.IsRussian ? ru : (Lang.IsChinese ? zh : en);

    private static void Patch()
    {
        if (_patched) return;
        try
        {
            var m = AccessTools.Method(typeof(Item), "DrawBlood",
                new[] { typeof(WaterContainerItem), typeof(Limb), typeof(Item) });
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Item.DrawBlood，SF-07 抽血液体不生效");
                return;
            }

            var harmony = new Harmony("com.stemfish.dlc.sfblood");
            harmony.Patch(m,
                prefix: new HarmonyMethod(typeof(SfBlood).GetMethod("DrawBlood_Prefix", BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: new HarmonyMethod(typeof(SfBlood).GetMethod("DrawBlood_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));

            _patched = true;
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] SF-07 抽血 hook 完成 | pre={info?.Prefixes?.Count ?? -1} post={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] SF-07 抽血 hook 失败: {e.Message}");
        }
    }

    private static void DrawBlood_Prefix(WaterContainerItem __0)
    {
        try { _before = __0 != null ? __0.AmountOf("blood") : 0f; } catch { _before = 0f; }
    }

    private static void DrawBlood_Postfix(WaterContainerItem __0, Limb __1)
    {
        if (!Enabled || __0 == null || __1 == null) return;
        if (!CharacterSelect.IsSf07(__1.body)) return;

        try
        {
            if (!_registered) Tick();          // 保险：注册表还没轮到的话先补上
            if (!_registered) return;

            float after = __0.AmountOf("blood");
            float added = after - _before;
            if (added <= 0.01f) return;

            // 只撤掉这次新加的那一份，然后换成我们的血
            var drain = __0.CalculateDrainSingleLiquid("blood", added);
            if (drain != null) __0.Drain(drain);
            float real = __0.AddLiquid(Id, added);

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"[STEMFishDLC] SF-07 抽血：{added:0}ml 实验体血液 → {real:0}ml SF-07 血液");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 转换 SF-07 血液失败: {e.Message}");
        }
    }

    /// <summary>液体名/描述会跟着语言切换重写（面板切成俄文后不用重启也能对上）</summary>
    public static void RefreshText()
    {
        try
        {
            FishLocalization.RegisterLiquid(Id,
                L("SF-07 血液", "SF-07 Blood", "Кровь SF-07"),
                L("从 SF-07 身上抽出的红色血液。这是他唯一不会排异的血——静脉注射可以安全恢复血容量。颜色像人血，但抗原和 DNA 与人类不同。不要指望用它打开那些物资仓。",
                  "Red blood drawn from SF-07. The only blood his body will not reject: injecting it safely restores blood volume. It looks like human blood, but its antigens and DNA are not human. Do not count on it to open any of those supply crates.",
                  "Красная кровь, взятая у SF-07. Единственная кровь, которую его тело не отторгает: внутривенное введение безопасно восстанавливает объём крови. На вид она как человеческая, но антигены и ДНК у неё не человеческие. Не рассчитывай, что ею можно открыть те ящики снабжения."));
        }
        catch { }
    }

    // ==========================================================================
    //  液体注册（Liquids..cctor 可能比 Init 晚，轮询到注册表就绪为止）
    // ==========================================================================
    public static void Tick()
    {
        if (_registered || !Enabled) return;
        try
        {
            var reg = Liquids.Registry;
            if (reg == null) return;

            if (reg.ContainsKey(Id)) { _registered = true; return; }

            var lt = new LiquidType
            {
                localeName = Id,
                color = new Color(0.72f, 0.06f, 0.07f, 1f),   // 比人血红一点，偏暗
                valuePerLiter = 20f,
                injectable = true,
                injectionSickness = 0f,
                qualities = new List<CraftingQuality> { new CraftingQuality("blood", 1f) },
            };

            // 注射：安全回血。原版 blood 是 30 / 750ml，这里给同样的量，但不加病、不加脓毒。
            lt.onHealthUse = (ml, limb) =>
            {
                try
                {
                    var b = limb != null ? limb.body : null;
                    if (b != null) b.bloodVolume += 30f * (ml / 750f);
                }
                catch { }
            };

            // 喝：生血，但胃不接受。少量生病，不至于像人血那样排异。
            lt.onDrink = (ml, body) =>
            {
                try
                {
                    if (body != null) body.sicknessAmount += 18f * (ml / 750f);
                }
                catch { }
            };

            reg[Id] = lt;
            _registered = true;

            RefreshText();

            Plugin.Log.LogInfo("[STEMFishDLC] 已注册液体 sfblood（SF-07 血液）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 注册 sfblood 失败: {e.Message}");
        }
    }
}
