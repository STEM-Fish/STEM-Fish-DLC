using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  AmmoCompat - 自定义口径（7.62×51 = AmmoType 3）
//  ---------------------------------------------------------------------------
//  游戏 GunScript.AmmoType 只有 Pistol=0 / Rifle=1 / Shotgun=2。
//  反汇编确认：全程序集里读 AmmoScript.ammoType 的只有 4 处，且都是"相等比较"：
//      AmmoScript.LoadRound ×2（同口径才能装填）
//      AmmoScript.UnloadRound（退一发 → AmmoTypeToItem 查 prefab）
//      GunScript.LoadMag（同口径才能上匣）
//  所以加第 4 个值 3 是安全的，只要把两处"按口径查 prefab"的卸载流程接过来：
//      Resources.Load("mdrz9mag") 会返回 null（我们的物品不是 Resources 资源）
//  这里做两件事：
//    ① 用 prefix 接管 UnloadMag / UnloadRound（口径 3 时用我们自己的物品生成）
//    ② 提供 SpawnMagazine/SpawnRound 给 GunHandling 等地方复用
// ============================================================================

public static class AmmoCompat
{
    public const int Mdrz9AmmoType = 3;                 // 7.62×51（自定义值）
    public const string Mdrz9MagId = "mdrz9mag";
    public const string Mdrz9RoundId = "mdrz9round";

    public static void Init(Plugin plugin)
    {
        try
        {
            var harmony = new Harmony("com.stemfish.dlc.ammo");
            int ok = 0;

            var unloadMag = AccessTools.Method(typeof(GunScript), "UnloadMag");
            if (unloadMag != null)
            {
                var pre = typeof(AmmoCompat).GetMethod("UnloadMag_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(unloadMag, prefix: new HarmonyMethod(pre));
                ok += Harmony.GetPatchInfo(unloadMag)?.Prefixes?.Count ?? 0;
            }

            var unloadRound = AccessTools.Method(typeof(AmmoScript), "UnloadRound");
            if (unloadRound != null)
            {
                var pre = typeof(AmmoCompat).GetMethod("UnloadRound_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(unloadRound, prefix: new HarmonyMethod(pre));
                ok += Harmony.GetPatchInfo(unloadRound)?.Prefixes?.Count ?? 0;
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 自定义口径 7.62×51 就绪 | prefix 合计={ok}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 自定义口径 hook 失败: {e}");
        }
    }

    // ==========================================================================
    //  生成弹药物品
    // ==========================================================================
    public static Item SpawnMagazine(int ammoType, Vector2 pos, int rounds)
    {
        try
        {
            if (ammoType == Mdrz9AmmoType)
            {
                var it = ItemRegistry.Create(Mdrz9MagId, pos);
                var ammo = it != null ? it.GetComponent<AmmoScript>() : null;
                if (ammo != null) ammo.rounds = Mathf.Clamp(rounds, 0, ammo.maxRounds);
                return it;
            }

            string id = AmmoScript.AmmoTypeToMagazine((GunScript.AmmoType)ammoType);
            if (string.IsNullOrEmpty(id)) return null;
            var go = Utils.Create(id, pos, 0f);
            if (go == null) return null;
            var a = go.GetComponent<AmmoScript>();
            if (a != null) a.rounds = Mathf.Clamp(rounds, 0, a.maxRounds);
            return go.GetComponent<Item>();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成弹匣失败: {e.Message}");
            return null;
        }
    }

    public static Item SpawnRound(int ammoType, Vector2 pos)
    {
        try
        {
            if (ammoType == Mdrz9AmmoType) return ItemRegistry.Create(Mdrz9RoundId, pos);

            string id = AmmoScript.AmmoTypeToItem((GunScript.AmmoType)ammoType);
            if (string.IsNullOrEmpty(id)) return null;
            var go = Utils.Create(id, pos, 0f);
            return go != null ? go.GetComponent<Item>() : null;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成子弹失败: {e.Message}");
            return null;
        }
    }

    // ==========================================================================
    //  GunScript.UnloadMag：卸下弹匣（原版走 Resources.Load(AmmoTypeToMagazine)）
    // ==========================================================================
    private static bool UnloadMag_Prefix(GunScript __instance)
    {
        try
        {
            if (__instance == null || (int)__instance.ammoType != Mdrz9AmmoType) return true;   // 其他口径交给原版
            if (!__instance.hasMag) return false;

            int rounds = __instance.roundsInMag;
            __instance.hasMag = false;
            __instance.roundsInMag = 0;

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            var mag = SpawnMagazine(Mdrz9AmmoType, __instance.transform.position, rounds);
            if (mag != null && body != null) body.AutoPickUpItem(mag);
            Plugin.Log.LogInfo($"[STEMFishDLC] 卸下 7.62 弹匣（{rounds} 发）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 卸弹匣失败: {e.Message}");
        }
        return false;
    }

    // ==========================================================================
    //  AmmoScript.UnloadRound：从弹匣里退一发（原版走 Resources.Load(AmmoTypeToItem)）
    // ==========================================================================
    private static bool UnloadRound_Prefix(AmmoScript __instance)
    {
        try
        {
            if (__instance == null || (int)__instance.ammoType != Mdrz9AmmoType) return true;
            if (__instance.rounds <= 0) return false;

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            var round = SpawnRound(Mdrz9AmmoType, __instance.transform.position);
            if (round != null && body != null) body.AutoPickUpItem(round);
            __instance.rounds--;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 退弹失败: {e.Message}");
        }
        return false;
    }
}
