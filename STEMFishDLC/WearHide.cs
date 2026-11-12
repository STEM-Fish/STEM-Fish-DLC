using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  WearHide - 穿戴后隐藏自身贴图（耳塞这种"看不见"的小件专用）
//  ---------------------------------------------------------------------------
//  为什么需要它（反汇编 Body.WearWearable 确认）：
//    穿上一件 wearable 时，游戏只是把**物品自己的 SpriteRenderer**
//      SetParent(肢体) + localPosition=0 + localRotation=identity + localScale=1
//      sortingOrder = 肢体.sortingOrder + wearableVisualOffset
//   也就是说"物品图标"会直接变成"穿在身上的贴图"——头盔、护甲都是这么做的。
//    对耳塞来说这就有问题：它会以头部中心为原点画在脸上。
//    wearableVisualOffset 设负值能压到头后面，但头部 sprite 是圆的，
//    四角透明，小图标会从边上漏出来。
//
//  → 干脆在穿戴状态下把 renderer 关掉：物品栏/地上照常可见，戴身上完全不可见。
//
//  ★ 判断"穿着"而不是"手持"：
//    手上拿着的物品同样挂在 hand 肢体下（parent 也是 Limb），
//    所以必须用 Body.HoldingItem(item) 排除掉，否则拿在手里就隐形了。
//  ★ 容器里不管：Container.LoadItem 会把 sr.enabled 设成 itemsVisible，
//    那时 parent 是 Container 不是 Limb，本脚本不会插手。
// ============================================================================
public class WearHide : MonoBehaviour
{
    private SpriteRenderer _sr;
    private Item _item;
    private Body _body;
    private Transform _lastParent;
    private bool _inited;
    private bool _hidden;

    private void Awake()
    {
        _sr = GetComponent<SpriteRenderer>();
        _item = GetComponent<Item>();
    }

    private void LateUpdate()
    {
        if (_sr == null) return;

        var p = transform.parent;
        if (_inited && p == _lastParent) return;     // 父级没变 → 状态不会变，省掉每帧判断
        _inited = true;
        _lastParent = p;

        bool hide = false;
        try
        {
            var limb = p != null ? p.GetComponent<Limb>() : null;
            if (limb != null)
            {
                if (_body == null) _body = GetComponentInParent<Body>();
                bool held = _body != null && _item != null && _body.HoldingItem(_item);
                hide = !held;                       // 挂在肢体上又不是手持 → 穿着
            }
        }
        catch { hide = false; }

        if (hide == _hidden) return;
        _hidden = hide;
        try { _sr.enabled = !hide; } catch { }
    }
}
