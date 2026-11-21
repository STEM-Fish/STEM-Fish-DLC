using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  TraderTalk - 买走屏蔽器时，商人说一句自己的话
//  ---------------------------------------------------------------------------
//  原版成交时（`TraderScript.TryPurchase` 里 IL_00AC）固定播一句
//  `Locale.GetCharacter("traderbuy", character)`。我们在这句之后再补一句，
//  只对 `jammer` 生效 —— Talker.Talk 是"换气泡内容"，所以玩家看到的是我们这句。
//
//  为什么用 postfix 而不是 prefix：prefix 时还不知道买没买成（钱不够会 return）。
//  prefix 只记一件事：这件货在不在货架上；postfix 发现它被 `items.Remove` 了，
//  就说明成交了（原版流程：扣钱 → 说话 → items.Remove → 生成物品）。
// ============================================================================

public static class TraderTalk
{
    public const string JammerId = "jammer";
    public const string JammerLine = "这会很有用的，伙计。";

    private static bool _patched;

    public static void Init(Plugin plugin)
    {
        Patch();
    }

    private static void Patch()
    {
        if (_patched) return;
        try
        {
            var m = AccessTools.Method(typeof(TraderScript), "TryPurchase", new[] { typeof(TraderItem) });
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 TraderScript.TryPurchase，商人台词不生效");
                return;
            }
            var pre = typeof(TraderTalk).GetMethod("Purchase_Prefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var post = typeof(TraderTalk).GetMethod("Purchase_Postfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            new Harmony("com.stemfish.dlc.tradertalk").Patch(m,
                prefix: new HarmonyMethod(pre),
                postfix: new HarmonyMethod(post));

            var info = Harmony.GetPatchInfo(m);
            _patched = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 商人台词 hook 完成 | prefix={info?.Prefixes?.Count ?? -1} postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 商人台词 hook 失败: {e.Message}");
        }
    }

    // 记下"这一笔之前，货还在架子上"
    private static void Purchase_Prefix(TraderScript __instance, TraderItem item, out bool __state)
    {
        __state = false;
        try
        {
            __state = __instance != null && item != null &&
                      __instance.items != null && __instance.items.Contains(item);
        }
        catch { }
    }

    private static void Purchase_Postfix(TraderScript __instance, TraderItem item, bool __state)
    {
        if (!__state || __instance == null || item == null) return;
        try
        {
            // 还在架子上 = 没成交（钱不够 / 商人血量过低直接 return）
            if (__instance.items != null && __instance.items.Contains(item)) return;
            if (item.id != JammerId) return;

            var talker = __instance.talker;
            if (talker == null) return;

            talker.Talk(new List<string> { JammerLine }, null, false, false);
            Plugin.Log.LogInfo($"[STEMFishDLC] 商人（购买屏蔽器）: {JammerLine}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 商人台词播放失败: {e.Message}");
        }
    }
}
