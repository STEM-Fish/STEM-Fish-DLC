using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  SpawnBridge - 把自定义物品接进游戏自带的控制台
//  ---------------------------------------------------------------------------
//  反汇编确认：
//   1) 游戏自带控制台，默认键位 `~`（KeyBinds "console" = KeyCode 96）；
//      世界里的 Terminal 终端建筑按使用也能开
//   2) spawn 命令：spawn <id> [position] [condition] [amount]
//      position 支持 cursor / player / random / x,y
//      ★ 弹匣（AmmoScript.itemType==1）会自动灌满：rounds = maxRounds × condition
//   3) spawn 内部走 Utils.Create(id,pos,rot) = Resources.Load(id) + Instantiate
//      → 我们的自定义物品不在 Resources 里，直接 spawn 会报 "Could not find entity"
//      → 用 Harmony 拦 Utils.Create，命中我们的 id 就交给 ItemRegistry.Create
//
//  所以：spawn mdrz9 player / spawn mdrz9mag player / spawn mdrz9round player 1 30 都能用。
// ============================================================================

public static class SpawnBridge
{
    public static void Init(Plugin plugin)
    {
        try
        {
            var target = AccessTools.Method(typeof(Utils), "Create",
                new[] { typeof(string), typeof(Vector2), typeof(float) });
            if (target == null)
            {
                Plugin.Log.LogError("[STEMFishDLC] 找不到 Utils.Create(string,Vector2,float)");
                return;
            }
            var pre = typeof(SpawnBridge).GetMethod("Create_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.spawn").Patch(target, prefix: new HarmonyMethod(pre));
            var info = Harmony.GetPatchInfo(target);
            Plugin.Log.LogInfo($"[STEMFishDLC] 控制台 spawn 接入完成 | prefix={info?.Prefixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] spawn 接入失败: {e}");
        }
    }

    // 原版实现：Resources.Load(id) + Instantiate。我们的物品没有 Resources 资源，在这里接手。
    private static bool Create_Prefix(string id, Vector2 pos, float rot, ref GameObject __result)
    {
        try
        {
            // 幸运方块：控制台 `spawn luckyblock player` 可用
            if (id == "luckyblock")
            {
                __result = LuckyBlock.Spawn(pos);
                Plugin.Log.LogInfo("[STEMFishDLC] spawn 幸运方块");
                return false;
            }

            // 我们的新敌人：控制台 `spawn spikecrawler player` 可用
            if (id == "spikecrawler")
            {
                __result = SpikeCrawler.SpawnGo(pos);
                Plugin.Log.LogInfo("[STEMFishDLC] spawn 尖刺爬行者");
                return false;
            }

            if (!ItemRegistry.IsCustom(id)) return true;
            var item = ItemRegistry.Create(id, pos, rot);
            __result = item != null ? item.gameObject : null;
            Plugin.Log.LogInfo($"[STEMFishDLC] spawn 自定义物品: {id}");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] spawn {id} 失败: {e.Message}");
            return true;
        }
    }

    // ==========================================================================
    //  补给（F7）：满弹匣 + 散装子弹
    //  ---------------------------------------------------------------------------
    //  弹匣优先直接放进弹挂甲容器（GunHandling 换弹只从弹挂甲里摸），
    //  没穿弹挂甲或塞不下 → 退回普通拾取（进背包/掉地上）。
    // ==========================================================================
    public static void GiveAmmoToPlayer(int magCount, int rounds)
    {
        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 补给失败：不在世界里");
                return;
            }
            var pos = (Vector2)body.transform.position;
            var carrier = GetCarrierContainer(body);
            int magCap = ItemRegistry.Find(AmmoCompat.Mdrz9MagId)?.ammoMaxRounds ?? 20;

            int magsGiven = 0, inCarrier = 0;
            for (int i = 0; i < magCount; i++)
            {
                var mag = AmmoCompat.SpawnMagazine(AmmoCompat.Mdrz9AmmoType, pos, magCap);
                if (mag == null) continue;
                magsGiven++;
                if (carrier != null && carrier.CanHoldItem(mag))
                {
                    carrier.LoadItem(mag);
                    inCarrier++;
                }
                else body.AutoPickUpItem(mag);
            }

            int roundsGiven = 0;
            for (int i = 0; i < rounds; i++)
            {
                var it = AmmoCompat.SpawnRound(AmmoCompat.Mdrz9AmmoType, pos + UnityEngine.Random.insideUnitCircle * 0.25f);
                if (it == null) continue;
                body.AutoPickUpItem(it);
                roundsGiven++;
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 补给: 7.62 弹匣 {magsGiven}（进弹挂甲 {inCarrier}）+ {roundsGiven} 发");
            FishVoice.SayLine(inCarrier > 0 ? "弹药就位。" : "弹匣先塞包里了，得自己挪到弹挂甲上。");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 补给失败: {e.Message}");
        }
    }

    private static Container GetCarrierContainer(Body body)
    {
        try
        {
            var carrier = body.GetWearable("platecarrier");
            if (carrier == null)
            {
                var all = body.GetAllItemsThorough();
                if (all != null)
                    foreach (var it in all)
                    {
                        if (it == null || it.id != "platecarrier") continue;
                        carrier = it;
                        break;
                    }
            }
            return carrier != null ? carrier.GetComponent<Container>() : null;
        }
        catch { return null; }
    }
}
