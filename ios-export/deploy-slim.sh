#!/usr/bin/env bash
# 增量装机: 把新织入的 sts2.dylib 换进已构建的 .app → 剥离 pck(资源走文档区) →
# 注入 --main-pack user://StS2.pck → 签名(含大内存权限) → USB 装机(保留文档区 pck+存档)。
# pck 已常驻手机 Documents, 同 bundle id 重装不会清; 故本脚本不重推 pck。
# 前置: 已用 build-ios.sh + Xcode GUI 完整构建过一次(DerivedData 里有 .app)。
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$SCRIPT_DIR/config.sh" ] || { echo "❌ 缺少 ios-export/config.sh，先 cp config.example.sh config.sh 并填写" >&2; exit 1; }
# shellcheck disable=SC1091
source "$SCRIPT_DIR/config.sh"
: "${STS2_DEVICE_UDID:?config.sh 未设置 STS2_DEVICE_UDID}"
: "${STS2_SIGN_IDENTITY:?config.sh 未设置 STS2_SIGN_IDENTITY}"
: "${STS2_BUNDLE_ID:?config.sh 未设置 STS2_BUNDLE_ID}"

DEV="$STS2_DEVICE_UDID"
IDENT="$STS2_SIGN_IDENTITY"
# 已构建的 .app（DerivedData 里的哈希目录不固定；Xcode Run 默认 Debug，也可能 Release，两者都收，取最新）
APP=$(find "$HOME/Library/Developer/Xcode/DerivedData" -maxdepth 6 -type d -path "*-iphoneos/StS2.app" ! -path "*Index.noindex*" 2>/dev/null \
      | xargs -I{} stat -f '%m %N' {} 2>/dev/null | sort -rn | head -1 | cut -d' ' -f2-)
PUB="$SCRIPT_DIR/.godot/mono/temp/bin/ExportRelease/ios-arm64/publish/sts2.dylib"
STG="$(mktemp -d /tmp/sts2slim.XXXXXX)/StS2.app"
ENT="$SCRIPT_DIR/.work/ent_mem.plist"
fail(){ echo "❌ $1"; exit 1; }

[ -f "$PUB" ] || fail "新 dylib 不存在: $PUB（先跑 build-ios.sh）"
[ -n "$APP" ] && [ -d "$APP" ] || fail "DerivedData 里找不到已构建的 StS2.app（先用 Xcode GUI 完整构建一次）"
[ -f "$ENT" ] || fail "entitlements 不存在: $ENT（先跑 build-ios.sh 生成）"
# 1) 换新织入 dylib 进 .app
cp "$PUB" "$APP/Frameworks/sts2.framework/sts2" || fail "换 dylib 失败"
echo "✅ 新 dylib 已换入 ($(shasum "$PUB" | cut -c1-12))"
# 2) 造瘦身包
mkdir -p "$(dirname "$STG")"; cp -R "$APP" "$STG"
rm -f "$STG/StS2.pck"
plutil -remove godot_cmdline "$STG/Info.plist" 2>/dev/null
plutil -insert godot_cmdline -json '["--main-pack", "user://StS2.pck"]' "$STG/Info.plist" || fail "注入 cmdline 失败"
echo "✅ 瘦身包 $(du -sh "$STG" | cut -f1), 已注入 --main-pack user://StS2.pck"
# 2.5) 注入 spine GDExtension iOS framework 到 app/Frameworks(治本:官方引擎运行时
#   OS_AppleEmbedded::open_dynamic_library 从 app/Frameworks/{name}.framework/{name} dlopen
#   GDExtension 库。Godot 命令行导出不会自动嵌入(需扩展的 macOS 库才触发嵌入),否则
#   SpineSprite 类型未注册→spine 节点退化成 Node→进游戏 NBossMapPoint._material null→NRE)。
#   游戏 pck 里的 spine_godot_extension.gdextension 的 ios.release 路径运行时解析到这里。
SPINE_FW="$SCRIPT_DIR/addons/spine/ios/libspine_godot.ios.template_release.framework"
[ -d "$SPINE_FW" ] || fail "缺 spine iOS framework: $SPINE_FW（先跑 build-ios.sh step3 放置）"
rm -rf "$STG/Frameworks/$(basename "$SPINE_FW")"
cp -R "$SPINE_FW" "$STG/Frameworks/" || fail "拷 spine framework 失败"
echo "✅ spine framework 已注入 app/Frameworks（SpineSprite 类型注册所需）"
# 2.6) 注入 FMOD GDExtension（同理，否则音频类未注册=游戏没声音）。FMOD 的 iOS 产物是
#   xcframework 里的裸 dylib（已静态含 FMOD 引擎，只 undefined 主二进制的 load_all_fmod_plugins，
#   dummy.cpp 已提供）。包装成 .framework 注入，运行时 open_dynamic_library fallback 到此加载。
FMOD_DYLIB="$SCRIPT_DIR/addons/fmod/libs/ios/libGodotFmod.ios.template_release.xcframework/ios-arm64/libGodotFmod.ios.template_release.universal.dylib"
[ -f "$FMOD_DYLIB" ] || fail "缺 FMOD dylib: $FMOD_DYLIB（先跑 build-ios.sh step3 放置）"
FMOD_FW="$STG/Frameworks/libGodotFmod.ios.template_release.framework"
rm -rf "$FMOD_FW"; mkdir -p "$FMOD_FW"
cp "$FMOD_DYLIB" "$FMOD_FW/libGodotFmod.ios.template_release" || fail "拷 FMOD dylib 失败"
# install_name 归一到 @rpath（原 dylib 的 id 是打包时的相对路径，改成标准 framework 形态）
install_name_tool -id "@rpath/libGodotFmod.ios.template_release.framework/libGodotFmod.ios.template_release" \
  "$FMOD_FW/libGodotFmod.ios.template_release" 2>/dev/null
cat > "$FMOD_FW/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>libGodotFmod.ios.template_release</string>
<key>CFBundleIdentifier</key><string>re.utopia.godot-fmod</string>
<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
<key>CFBundleName</key><string>GodotFmod</string>
<key>CFBundlePackageType</key><string>FMWK</string>
<key>CFBundleShortVersionString</key><string>6.1.0</string>
<key>CFBundleVersion</key><string>6.1.0</string>
<key>DTPlatformName</key><string>iphoneos</string>
<key>MinimumOSVersion</key><string>12.0</string>
</dict></plist>
PLIST
echo "✅ FMOD framework 已注入 app/Frameworks（音频类注册所需）"
# 2.7) 换 App 图标为游戏图标（Godot 导出的是占位灰图；产物由 tools/make-appicon.sh 预生成）。
#   必须在签名前替换，签名会覆盖这些文件的封印。产物不存在则跳过（保持占位，不阻断部署）。
ICON_DIR="$SCRIPT_DIR/appicon"
if [ -f "$ICON_DIR/Assets.car" ]; then
  cp "$ICON_DIR/Assets.car" "$STG/Assets.car"
  cp "$ICON_DIR/AppIcon60x60@2x.png" "$STG/AppIcon60x60@2x.png" 2>/dev/null
  cp "$ICON_DIR/AppIcon76x76@2x~ipad.png" "$STG/AppIcon76x76@2x~ipad.png" 2>/dev/null
  echo "✅ App 图标已换为游戏图标"
else
  echo "ℹ️ 未找到预生成图标（$ICON_DIR），保持占位图标。可跑 tools/make-appicon.sh 生成"
fi
# 3) 签名
for fw in "$STG/Frameworks/"*.framework; do
  codesign -f -s "$IDENT" --generate-entitlement-der "$fw" >/dev/null 2>&1 || fail "签框架失败 $fw"
done
codesign -f -s "$IDENT" --generate-entitlement-der --entitlements "$ENT" "$STG" >/dev/null 2>&1 || fail "签 .app 失败"
codesign -d --entitlements - "$STG" 2>/dev/null | grep -q increased-memory && echo "✅ 大内存权限已嵌入" || fail "大内存权限缺失"
codesign -vv "$STG" 2>&1 | grep -q "satisfies its Designated Requirement" && echo "✅ 签名有效"
# 4) 装机(保留文档区)。⚠️ 必须查真实退出码, 曾有装机失败仍报成功的教训。
echo "▶ USB 装机(保留 Documents/StS2.pck + 存档)..."
INSTALL_OUT=$(xcrun devicectl device install app --device "$DEV" "$STG" 2>&1)
RC=$?
echo "$INSTALL_OUT" | grep -iE "installationURL|error" | head -3
[ "$RC" = "0" ] || fail "装机失败(退出码 $RC)"
echo "$INSTALL_OUT" | grep -q "installationURL" || fail "装机输出异常(无 installationURL)"
# 装完核验文档区 pck 是否仍在(升级安装应保留; 若被当新装会清空)
if ! xcrun devicectl device info files --device "$DEV" \
     --domain-type appDataContainer --domain-identifier "$STS2_BUNDLE_ID" 2>/dev/null \
     | grep -q "Documents/StS2.pck"; then
  echo "⚠️ 警告: 容器里没有 Documents/StS2.pck(可能被当新装清空), 需重推 pck!"
fi
echo "✅ 装机完成"
