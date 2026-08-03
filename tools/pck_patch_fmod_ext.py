#!/usr/bin/env python3
"""就地修复 Godot pck 里 FMOD GDExtension 在 iOS 上加载失败(无声音)的问题。

根因(Godot 4.5 core/extension/gdextension_library_loader.cpp):
    is_static_library = library_path.ends_with(".a") || library_path.ends_with(".xcframework");
    open_dynamic_library(is_static_library ? String() : abs_path, ...);
fmod.gdextension 的 ios.debug/ios.release 指向 ".xcframework" → Godot 判定为"静态库",
传空路径走 RTLD_SELF 分支,dlsym(RTLD_SELF, "fmod_library_init") 在主二进制里找符号 →
找不到(FMOD 是独立动态 framework,没静态进主二进制)→ 扩展加载失败 → 全部 FMOD 音频(背景
音乐 + 战斗音效)静默。spine 用 ".framework" 走动态加载分支,所以正常。

修法: 把 addons/fmod/fmod.gdextension 里两处 ".xcframework" 改成 ".framework",
Godot 即走动态加载,dlopen 到 app/Frameworks/ 下由 deploy-slim 注入的
libGodotFmod.ios.template_release.framework(其内 dlsym fmod_library_init 成功)。

实现: 同长度就地覆盖(改后补尾部换行到原长度)→ 其余文件偏移不变,只重写该文件内容 +
更新目录里它这一项的 md5。不重建 1.8G pck。

用法: pck_patch_fmod_ext.py <pck>   (就地修改)
"""
import struct, sys, os, hashlib

TARGET = "addons/fmod/fmod.gdextension"

def patch(pck):
    f = open(pck, "r+b")
    assert f.read(4) == b"GDPC", "bad magic"
    fmt = struct.unpack("<I", f.read(4))[0]
    assert fmt >= 3, f"only format v3+ supported, got {fmt}"
    f.read(12)                                    # version major/minor/patch
    flags = struct.unpack("<I", f.read(4))[0]
    assert not (flags & 1), "directory encrypted"
    files_base = struct.unpack("<Q", f.read(8))[0]
    dir_offset = struct.unpack("<Q", f.read(8))[0]
    rel = bool(flags & 2)

    f.seek(dir_offset)
    count = struct.unpack("<I", f.read(4))[0]
    entries = []  # [dir_pos_of_md5, plen, pathbytes, off_stored, size]
    for _ in range(count):
        plen = struct.unpack("<I", f.read(4))[0]
        pathb = f.read(plen)
        off = struct.unpack("<Q", f.read(8))[0]
        size = struct.unpack("<Q", f.read(8))[0]
        md5_pos = f.tell()
        f.read(16)                                # md5
        f.read(4)                                 # entry flags
        entries.append([md5_pos, plen, pathb, off, size])

    def abs_off(o): return o + files_base if rel else o
    def pstr(pb): return pb.rstrip(b"\x00").decode("utf-8")

    target = next((e for e in entries if pstr(e[2]) == TARGET), None)
    assert target is not None, f"{TARGET} not found in pck"
    md5_pos, _, _, off_stored, size = target
    o = abs_off(off_stored)

    f.seek(o); content = f.read(size)
    n = content.count(b".xcframework")
    assert n == 2, f"expected exactly 2 '.xcframework' in {TARGET}, found {n}"
    new = content.replace(b".xcframework", b".framework")
    assert len(new) < size, "replacement did not shrink content as expected"
    new += b"\n" * (size - len(new))              # 补尾部换行到原长度(ConfigFile 忽略)
    assert len(new) == size, "padding failed to restore original size"
    assert b".xcframework" not in new and new.count(b"libGodotFmod.ios.template_release.framework") >= 1

    f.seek(o); f.write(new)                        # 同长度原地覆盖
    f.seek(md5_pos); f.write(hashlib.md5(new).digest())   # 更新该文件目录项 md5
    f.close()
    print(f"OK: {TARGET} 的 ios .xcframework → .framework (同长度就地, md5 已更新)")

if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: pck_patch_fmod_ext.py <pck>", file=sys.stderr); sys.exit(2)
    patch(sys.argv[1])
