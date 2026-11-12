using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  FreezeOnSpawn - 生成后先冻几秒，等区块碰撞体就绪再放开
//  ---------------------------------------------------------------------------
//  「地图加载时就把物资摆好」这件事有个物理陷阱：
//    我们按**方块数组**算出了正确落点（方块数据一生成就有），但那个区块的
//    TilemapCollider2D 可能还没实例化 —— 这时候生成的物品会直接穿过地板掉下去，
//    甚至一路掉出世界。原版自己的实体是在 `WorldPlaceEntities` 里生成的，
//    天然赶在碰撞体就绪之后，我们没这个时机，只能自己兜。
//
//  做法：`rb.simulated = false` 停几秒（物品原地悬着、不下落），到点再打开。
//  如果碰撞体本来就是好的，这几秒完全无感 —— 物品本来就该待在原地。
// ============================================================================

public class FreezeOnSpawn : MonoBehaviour
{
    private float _t;
    private Rigidbody2D _rb;

    public static void Attach(GameObject go, float seconds)
    {
        if (go == null) return;
        try
        {
            var f = go.AddComponent<FreezeOnSpawn>();
            if (f != null) f.Setup(seconds);
        }
        catch { }
    }

    private void Setup(float seconds)
    {
        _t = Mathf.Clamp(seconds, 0.1f, 20f);
        try
        {
            _rb = GetComponent<Rigidbody2D>();
            if (_rb != null)
            {
                _rb.velocity = Vector2.zero;
                _rb.angularVelocity = 0f;
                _rb.simulated = false;
            }
        }
        catch { }
    }

    private void Update()
    {
        _t -= Time.deltaTime;
        if (_t > 0f) return;

        try
        {
            if (_rb != null)
            {
                _rb.simulated = true;
                _rb.velocity = Vector2.zero;
                _rb.angularVelocity = 0f;
            }
        }
        catch { }

        Destroy(this);
    }
}
