#!/usr/bin/env bash
# ============================================================================
#  STEM Fish 全家桶 —— 构建 + 部署到**正式版**
#  ---------------------------------------------------------------------------
#  和 STEMFishDLC/deploy.sh（试用版）的区别：这里同时构建三个东西
#    · STEMFishDLC        → BepInEx/plugins/         （剧情 DLC 本体）
#    · NoSelfDestruct     → BepInEx/plugins/         （退出不自毁）
#    · STEMFishDLC.Patcher→ BepInEx/patchers/        （隔离其它 MOD，可选）
#
#  用法：
#     bash deploy_full.sh                       # 默认正式版目录
#     GAME_DIR="D:/Steam/.../Casualties Unknown Demo" bash deploy_full.sh
# ============================================================================
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
GAME="${GAME_DIR:-D:/Steam/steamapps/common/Casualties Unknown}"
DOTNET="/c/Program Files/dotnet/dotnet.exe"
DST="$GAME/BepInEx"
PLUGIN="$DST/plugins/STEMFishDLC"

if tasklist 2>/dev/null | grep -qi "CasualtiesUnknown.exe"; then
  echo "!! 游戏正在运行，请先退出（dll 会被占用）"
  exit 1
fi

if [ ! -f "$DST/core/BepInEx.dll" ]; then
  echo "!! $GAME 里没有 BepInEx，先装 BepInEx 5.4.23.x"
  exit 1
fi

echo "== 目标游戏：$GAME =="

echo "== 构建 STEMFishDLC =="
cd "$ROOT/STEMFishDLC"
"$DOTNET" build -c Release -p:GameDir="$GAME" | grep -E "error|已成功生成" || true

echo "== 构建 NoSelfDestruct =="
cd "$ROOT/NoSelfDestruct"
"$DOTNET" build -c Release -p:GameDir="$GAME" | grep -E "error|已成功生成" || true

echo "== 构建 Patcher =="
cd "$ROOT/patcher"
"$DOTNET" build -c Release -p:GameDir="$GAME" | grep -E "error|已成功生成" || true

DLL="$ROOT/STEMFishDLC/bin/Release/netstandard2.1/STEMFishDLC.dll"
NSD="$ROOT/NoSelfDestruct/bin/Release/No Self-Destruct on Quit.dll"
PAT="$ROOT/patcher/bin/Release/STEMFishDLC.Patcher.dll"

for f in "$DLL" "$NSD" "$PAT"; do
  [ -f "$f" ] || { echo "!! 构建产物不存在：$f"; exit 1; }
done

echo "== 部署 =="
cp "$DLL" "$DST/plugins/STEMFishDLC.dll"
cp "$NSD" "$DST/plugins/"
mkdir -p "$DST/patchers"
cp "$PAT" "$DST/patchers/"

mkdir -p "$PLUGIN/textures"
cp "$ROOT"/STEMFishDLC/textures/*.png "$PLUGIN/textures/"
# 所有语种一起带过去（只认 xx-XX.json 命名，避免把「副本」之类的临时文件也复制进去）
for f in "$ROOT"/STEMFishDLC/*.zh-CN.json "$ROOT"/STEMFishDLC/*.en-US.json \
         "$ROOT"/STEMFishDLC/*.ru-RU.json; do
  [ -f "$f" ] && cp "$f" "$PLUGIN/"
done

if [ -d "$ROOT/STEMFishDLC/sfx" ]; then
  mkdir -p "$PLUGIN/sfx"
  cp "$ROOT"/STEMFishDLC/sfx/*.wav "$PLUGIN/sfx/" 2>/dev/null || true
fi

if [ -d "$ROOT/STEMFishDLC/lore_img" ]; then
  mkdir -p "$PLUGIN/lore_img"
  cp "$ROOT"/STEMFishDLC/lore_img/*.png "$PLUGIN/lore_img/" 2>/dev/null || true
fi

if [ -d "$ROOT/STEMFishDLC/skin" ]; then
  echo "== 皮肤 =="
  for pack in "$ROOT"/STEMFishDLC/skin/*/; do
    [ -d "$pack" ] || continue
    name="$(basename "$pack")"
    # ⚠ 不做删除：本机的批量删除保护会把子目录移进回收站后失败，
    #   一旦某一步失败，后面的 cp 不会执行，皮肤包就会被清空一半。
    #   改成覆盖式拷贝；如果源码里删掉了某张贴图，部署目录里可能留旧文件，
    #   需要时手动删（这类情况很少）。
    mkdir -p "$DST/plugins/CustomSprites/$name"
    cp -rf "$pack". "$DST/plugins/CustomSprites/$name/"
    echo "  同步皮肤包: $name"
  done
fi

echo "== 完成 =="
ls -la "$DST/plugins/STEMFishDLC.dll" "$DST/plugins/No Self-Destruct on Quit.dll" "$DST/patchers/STEMFishDLC.Patcher.dll"
