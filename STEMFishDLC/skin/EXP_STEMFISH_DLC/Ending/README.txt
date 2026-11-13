结局 / 过场插画替换（Ending）
==============================


规则和 Loading/ 完全一样
------------------------

**PNG 文件名 = 游戏里的 sprite 名**，放在本目录（Ending/）就会生效；
放在 _reference/ 里只是参考图，不生效（想改哪张就从 _reference/ 复制到上一层再画）。

程序挂的是 `Image.sprite` 和 `SpriteRenderer.sprite` 两个赋值钩子：
结局过场是全屏 UGUI Image，实验室里的科学家是世界里的 SpriteRenderer，
两条路都会被当场换成你的图，不会和原版逐帧动画抢。

尺寸：原图是 **320×180 的画布**（结局过场）/ **54×114、36×70**（科学家）。
保持画布尺寸和比例，画多大都行（等比放大即可），程序按原版几何重建 sprite，
所以角色在画布里的位置 = 游戏里的位置，照 _reference/ 描就行。


────────────────────────────────────────────────────────
正式版 1.0 的结局有哪几种（反汇编 Assembly-CSharp 得到）
────────────────────────────────────────────────────────

所有结局过场图都在 `WorldGeneration.cutsceneImages[13]` 这个数组里，
下表就是数组的真实顺序（用存档文件的 PPtr 逐个对出来的）：

  索引  sprite 名          用在哪
  ────  ─────────────────  ──────────────────────────────────────────────
   0    char1endcargo      ★ 没被任何结局引用（货舱差分，v1.0 遗留素材）
   1    char2endcargo      ★ 同上
   2    char3endcargo      ★ 同上
   3    char1end           ✅ 逃脱结局 / 地表突破：主角 SAW-03 "Milky" 站在地表
   4    char2end           ✅ 同上：SAW-12 "Dune" 的差分
   5    char3end           ✅ 同上：第三个角色（v1.0 还没有角色数据，画了也看不到）
   6    surface            ✅ 地表突破过场（SurfaceBreachCutscene）的底图
   7    lab                ✅ 生物舱结局（BiopodEnding / Alt / Alt2）—— 被按回培养舱
   8    earth              ✅ 逃脱结局第一幕（地表全景）
   9    surfacemerc        ✅ 佣兵结局第一幕（MercEnding）
  10    mercend            ★ 没被引用（v1.0 遗留）
  11    merccombined       ✅ 佣兵结局第二幕（佣兵 + 你）
  12    earthend           ✅ 逃脱结局定格（"逃出生天"那张结算图）

对应的代码位置（方便以后游戏更新了重新核对）：

  WorldGeneration/<SurfaceBreachCutscene>  → cutsceneUnder = [6]，cutsceneOver = [charType+3]
  WorldGeneration/<EscapeEnding>          → cutsceneUnder = [8]，再换 [12]，over = [charType+3]
  WorldGeneration/<MercEnding>            → cutsceneUnder = [9]，再换 [11]
  WorldGeneration/<BiopodEnding*>(×3)     → cutsceneUnder = [7]
  PlayerCamera/<EndSequence>              → 结算画面直接把 cutsceneUnder 的图拿来当背景

`charType` = 你在主菜单选的角色（0=Milky / 1=Dune / 2=第三个），
所以 **char1end / char2end / char3end 三张是"同一幕、不同角色"的差分**，
画的时候只画自己用的那张也行，其余留空就是原版。


────────────────────────────────────────────────────────
实验室结局里的科学家（世界里的 SpriteRenderer，不走 UGUI）
────────────────────────────────────────────────────────

  scientist      54×114   实验室里那位科学家（EndLabScript.scientist 的初始图）
  endsc1         36×70    ├ 说话/待机 第 1 帧（scientistSprites[0]）
  endsc2         36×70    ├ 说话/待机 第 2 帧（scientistSprites[1]）
  endsc3         36×70    ├ 惊恐 第 1 帧（scientistSprites[2]）
  endsc4         36×70    └ 惊恐 第 2 帧（scientistSprites[3]）
  endscfall      60×70    科学家被击中倒下的那一下
  endsccorpse    16×62    科学家的尸体

  ⚠ endsc1~4 是 1.5 次/秒的两帧循环（EndLabScript.Update 里 floor(time*1.5)%2），
    四张必须画布一致，只改表情/姿势，否则会跳。


────────────────────────────────────────────────────────
现状（0.18.12）
────────────────────────────────────────────────────────

**结局有关的美术资源替换还没完工** —— 这里目前只有原版导出的参考图
（_reference/），一层都没有能生效的新图。等你把画好的 PNG 放到 Ending/ 下，
下一次启动就会自动接管，不用改代码、不用重编译。

画完一张就能先放一张，没放的那一幕继续用原版图，不会互相影响。
