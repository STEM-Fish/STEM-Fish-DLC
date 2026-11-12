using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  GunHandling - "受过训练的实验体"枪械操作
//  ---------------------------------------------------------------------------
//  按枪械类型分流（反汇编 GunScript 确认）：
//    feedType == 0 → 弹匣供弹（rifle 等自动/半自动枪）
//        R = 完整换弹流程：卸旧弹匣（有空间收进背包，否则落地丢弃）
//            → 换弹计时（默认 2.0s，可在配置里改）→ 装上新弹匣 → 上膛
//    feedType == 1 → 内置弹仓（shotgun / makeshiftgun 栓动）
//        R = 拉一次枪栓（上膛或退壳），一次按下只判一次
//
//  `racked` 的真实语义（GunScript.Update 反汇编）：
//    racked = true  → 枪机被拉开（播放 gunrack 音效）
//    racked = false → 枪机闭锁（播放 gununrack）
//    开火条件：roundInChamber == 0（膛内有弹）&& !racked（已闭锁）&& !safe
//  → 所以"是否已上膛"要看 roundInChamber，不是 racked（上一版显示反了就是这个原因）
// ============================================================================

public static class GunHandling
{
    // ---- 换弹状态 ----
    private static bool _reloading;
    private static float _reloadEnd;
    private static Body _body;
    private static Item _gunItem;
    private static GunScript _gun;
    private static Item _newMag;
    private static int _newMagRounds;

    private static float _nextKeyTime;

    // 两段式枪机（拉开 → 0.22s → 推回）
    // 反汇编 GunScript.Update 确认：**上膛发生在 racked 由 true → false 的那一刻**
    //   if (lastRacked && !racked && roundsInMag > 0 && roundInChamber == None) { roundInChamber = Round; roundsInMag--; }
    // 所以一次 TryRack 只是"拉开枪机"，必须再推回才会真正上膛。
    private static GunScript _pendingRack;
    private static float _pendingRackAt;

    public static bool IsReloading => _reloading;
    public static float ReloadRemaining => _reloading ? Mathf.Max(0f, _reloadEnd - Time.unscaledTime) : 0f;
    public static float ReloadDuration { get; private set; } = 2.0f;

    // 换弹台词已移到 voice.zh-CN.json 的 "lines.reload"（可自由增删，不用重编译）。
    // 这里只留一句兜底：json 缺失 / 这一项被删空时用。
    private const string ReloadFallback = "换弹！";

    public static void Init(Plugin plugin)
    {
        try
        {
            ReloadDuration = plugin.Config.Bind("GunHandling", "ReloadSeconds", 2.0f, "自动枪械换弹耗时（秒）").Value;
            ReloadDuration = Mathf.Clamp(ReloadDuration, 0.3f, 6f);
        }
        catch { }
        Plugin.Log.LogInfo($"[STEMFishDLC] 枪械操作就绪（R：弹匣枪换弹 {ReloadDuration:0.0}s / 栓动枪拉栓）");
    }

    public static void Update()
    {
        TickPendingRack();                       // 先把"推回枪机"这一步补齐

        if (_reloading) { TickReload(); return; }

        if (!Input.GetKeyDown(KeyCode.R)) return;
        if (Time.unscaledTime < _nextKeyTime) return;
        _nextKeyTime = Time.unscaledTime + 0.25f;      // 防连按（长按也只判一次）

        var body = PlayerCamera.main?.body;
        if (body == null) return;

        Item hand;
        try { hand = body.GetItem(0); } catch { return; }
        if (hand == null) return;

        GunScript gun = null;
        try { gun = hand.GetComponent<GunScript>(); } catch { }
        if (gun == null) return;                        // 手上不是枪 → 不接管 R

        bool magazineFed = (int)gun.feedType == 0;

        // 先找可用弹匣：弹匣枪 / 半自动手枪 / makeshift rifle（栓动但用弹匣）都适用
        var mag = FindMagazine(body, hand, gun);
        bool needReload = mag != null && (!gun.hasMag || gun.roundsInMag < gun.magCapacity);

        if (needReload)
        {
            StartReload(body, hand, gun, mag);
            return;
        }

        if (magazineFed)
        {
            // 弹匣满但膛内是弹壳/空 → 走一次完整上膛
            if (gun.roundInChamber != 0) RackCycle(gun);
            return;
        }

        // 栓动 / 泵动：一次按键 = 一次完整枪机动作（拉开→推回）
        RackCycle(gun);
    }

    // ==========================================================================
    //  换弹流程
    // ==========================================================================
    private static void StartReload(Body body, Item gunItem, GunScript gun, Item mag)
    {
        try
        {
            int rounds = 0;
            var ammo = mag.GetComponent<AmmoScript>();
            if (ammo != null) rounds = ammo.rounds;
            if (rounds <= 0) return;

            _body = body;
            _gunItem = gunItem;
            _gun = gun;
            _newMag = mag;
            _newMagRounds = rounds;
            _reloading = true;
            _reloadEnd = Time.unscaledTime + ReloadDuration;

            EjectOldMagazine(body, gun);            // 卸旧弹匣（收进背包 / 落地）

            if (UnityEngine.Random.value < 0.45f)
                FishVoice.SayLine(FishVoice.RandomLine("reload", ReloadFallback));

            Plugin.Log.LogInfo($"[STEMFishDLC] 开始换弹（{ReloadDuration:0.0}s，新弹匣 {rounds} 发）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 换弹启动失败: {e.Message}");
            _reloading = false;
        }
    }

    private static void TickReload()
    {
        if (Time.unscaledTime < _reloadEnd) return;

        _reloading = false;
        try
        {
            if (_gun == null || _newMag == null) return;

            // 换弹途中切了武器 → 取消（不要把弹匣白白吃掉）
            var hand = _body != null ? _body.GetItem(0) : null;
            if (hand != _gunItem)
            {
                Plugin.Log.LogInfo("[STEMFishDLC] 换弹被打断（已切换武器）");
                return;
            }

            // 直接用弹匣里的数值装填（避免中途物品被移动）
            _gun.hasMag = true;
            _gun.roundsInMag = Mathf.Clamp(_newMagRounds, 0, Mathf.Max(1, _gun.magCapacity));

            UnityEngine.Object.Destroy(_newMag.gameObject);     // 消耗掉这个弹匣
            _newMag = null;

            // 膛内没弹（弹壳/空）就完整上一次膛
            if (_gun.roundInChamber != 0) RackCycle(_gun);

            Plugin.Log.LogInfo($"[STEMFishDLC] 换弹完成: {_gun.roundsInMag}/{_gun.magCapacity}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 换弹收尾失败: {e.Message}");
        }
        finally
        {
            _body = null; _gunItem = null; _gun = null;
        }
    }

    // 卸下当前弹匣：优先塞回弹挂甲，装不下再塞身上空位，都不行就留在地上
    // ---------------------------------------------------------------------------
    //  ★ 作者要求（v0.12.8）：空弹匣先回弹挂甲，然后才是身上的空物品栏。
    //    现实里弹匣本来就会顺手往弹挂袋里一插，也顺便避免空的满的混在背包里。
    //  ★ Container.LoadItem 有两个隐藏门槛（反汇编确认）：
    //    ① 物品与容器距离必须 < 10（bge.un → 静默 return，什么都不做）
    //       弹匣生在枪的位置 = 玩家身上，距离 0，必然通过
    //    ② 容器里已有 Container 类物品会弹 alert 并拒绝（不影响我们）
    //    所以调用后必须**回查 transform.parent** 才算成功，不能只看没报错。
    // ==========================================================================
    private static void EjectOldMagazine(Body body, GunScript gun)
    {
        if (!gun.hasMag) return;

        int rounds = gun.roundsInMag;
        gun.hasMag = false;
        gun.roundsInMag = 0;

        // ★「急速下坠」模式：换下来的弹匣自动补满
        //   SpawnMagazine 用 int.MaxValue 表示"灌满"（内部按该口径的 maxRounds 截断），
        //   原来是 0 就自动变成满弹匣，玩家只需照常换弹，不用管弹药数。
        bool refill = false;
        try { refill = RunConfig.Current != null && RunConfig.Current.infiniteMags; } catch { }
        if (refill) rounds = int.MaxValue;

        try
        {
            // 自定义口径（7.62×51 等）没有 Resources prefab，统一交给 AmmoCompat 生成
            var magItem = AmmoCompat.SpawnMagazine((int)gun.ammoType, gun.transform.position, rounds);
            if (magItem == null) return;

            if (TryStoreInCarrier(body, magItem, out var carrierName))
            {
                Plugin.Log.LogInfo($"[STEMFishDLC] 旧弹匣（{(refill ? "已补满" : rounds + " 发")}）→ {carrierName}");
                return;
            }

            body.AutoPickUpItem(magItem);      // 弹挂甲满了 → 身上空位；身上也满 → 留在地上
            Plugin.Log.LogInfo($"[STEMFishDLC] 旧弹匣已卸下（{(refill ? "已补满" : rounds + " 发")}），弹挂甲放不下 → 走身上空位");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 卸弹匣失败: {e.Message}");
        }
    }

    // 把物品塞进玩家身上的某个容器（目前就是弹挂甲）。成功返回 true
    private static bool TryStoreInCarrier(Body body, Item item, out string name)
    {
        name = null;
        if (body == null || item == null) return false;

        Container cont = null;
        Item carrier = null;
        try
        {
            carrier = FindWearableById(body, "platecarrier");
            if (carrier == null) return false;

            cont = carrier.GetComponent<Container>();
            if (cont == null) return false;

            if (!cont.CanHoldItem(item)) return false;   // 单件上限 / 总承重 / tag 限制
            cont.LoadItem(item);

            // LoadItem 失败时是静默 return → 用父子关系验证
            if (IsIn(item, cont))
            {
                name = carrier.Stats != null ? carrier.fullName : "弹挂甲";
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 弹匣入弹挂甲失败: {e.Message}");
            // 万一异常抛在 SetParent 之后，物品其实已经进去了 —— 再确认一次，
            // 避免外面又 AutoPickUpItem 一次（同一个物品出现在两个地方）
            if (cont != null && IsIn(item, cont))
            {
                name = carrier != null && carrier.Stats != null ? carrier.fullName : "弹挂甲";
                return true;
            }
        }
        return false;
    }

    private static bool IsIn(Item item, Container cont)
    {
        try
        {
            return item != null && cont != null && item.transform != null &&
                   item.transform.parent == cont.transform;
        }
        catch { return false; }
    }

    // 按 id 找身上穿着的装备（和 FindMagazine 用同一个入口，行为保持一致）
    private static Item FindWearableById(Body body, string id)
    {
        try
        {
            var all = body.GetAllItemsThorough();
            if (all == null) return null;
            foreach (var it in all)
                if (it != null && it.id == id) return it;
        }
        catch { }
        return null;
    }

    // 只在**我们加的弹挂甲容器**里找弹匣（平衡设计：弹匣必须放在弹挂里）
    // 容器内容 = Container 的 transform 子物体（Container.get_itemCount = transform.childCount）
    private static Item FindMagazine(Body body, Item gunItem, GunScript gun)
    {
        try
        {
            Item carrier = null;
            var all = body.GetAllItemsThorough();
            if (all != null)
                foreach (var it in all)
                {
                    if (it == null || it.id != "platecarrier") continue;
                    carrier = it;
                    break;
                }

            if (carrier == null)
            {
                Plugin.Log.LogInfo("[STEMFishDLC] 未穿弹挂甲 → 不能自动换弹（改为拉栓）");
                return null;
            }

            var cont = carrier.GetComponent<Container>();
            if (cont == null)
            {
                Plugin.Log.LogInfo("[STEMFishDLC] 弹挂甲没有容器组件 → 不能自动换弹");
                return null;
            }

            Item best = null;
            int bestRounds = -1;
            foreach (Transform child in cont.transform)
            {
                var it = child != null ? child.GetComponent<Item>() : null;
                if (it == null || it == gunItem) continue;

                var ammo = it.GetComponent<AmmoScript>();
                if (ammo == null) ammo = it.GetComponentInChildren<AmmoScript>(true);
                if (ammo == null) continue;
                if ((int)ammo.itemType != 1) continue;          // 1 = 弹匣
                if (ammo.ammoType != gun.ammoType) continue;
                if (ammo.rounds <= 0) continue;

                if (ammo.rounds > bestRounds) { bestRounds = ammo.rounds; best = it; }
            }

            if (best == null)
                Plugin.Log.LogInfo($"[STEMFishDLC] 弹挂甲里没有匹配弹匣（口径 {gun.ammoType}）→ 拉栓");
            return best;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 找弹匣失败: {e.Message}");
        }
        return null;
    }

    // 一次"完整枪机动作"：闭锁→拉开→(0.22s)→推回上膛；已拉开则直接推回
    private static void RackCycle(GunScript gun)
    {
        try
        {
            if (gun.racked)
            {
                gun.TryRack();                       // 已开 → 推回（Update 里完成上膛）
                Plugin.Log.LogInfo($"[STEMFishDLC] 推回枪机（膛内={gun.roundInChamber}）");
            }
            else
            {
                gun.TryRack();                       // 闭锁 → 拉开（抛壳）
                _pendingRack = gun;
                _pendingRackAt = Time.unscaledTime + 0.22f;
                Plugin.Log.LogInfo("[STEMFishDLC] 拉开枪机…");
            }
        }
        catch { }
    }

    private static void TickPendingRack()
    {
        if (_pendingRack == null) return;
        if (Time.unscaledTime < _pendingRackAt) return;

        var g = _pendingRack;
        _pendingRack = null;
        try
        {
            g.TryRack();                             // 推回 → 若膛内为 None 且有弹则上膛
            Plugin.Log.LogInfo($"[STEMFishDLC] 推回枪机（膛内={g.roundInChamber}, racked={g.racked}, 弹匣={g.roundsInMag}）");
        }
        catch { }
    }
}
