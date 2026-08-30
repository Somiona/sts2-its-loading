#!/usr/bin/env python3
"""构建 Godot 4.5 格式(pack_version=3)的 mod 图标 PCK。

格式逆向自 BaseLib.pck(v3, engine 4.5.1,游戏可加载):
  header(112B): magic GDPC / v3 / 引擎版本 / flags=2 / file_base / 表偏移 / 保留零
  data:         文件内容裸放,offset 相对 file_base,16 字节对齐
  table(尾部): count + [plen][path\\0][u64 ofs][u64 size][md5][u32 flags],贴 EOF
注意不能用 Godot 4.7 的 PCKPacker——它写 v4,游戏的 4.5.1 fork 会拒绝。
"""
import hashlib
import struct
import sys

FILE_BASE = 112
ENGINE = (4, 5, 1)
PACK_VERSION = 3


def build(out_path: str, files: list) -> None:
    header = struct.pack('<5I', 0x43504447, PACK_VERSION, *ENGINE)
    header += struct.pack('<I', 2)                # pack_flags(与 BaseLib 一致)
    header += struct.pack('<Q', FILE_BASE)        # file_base
    header += struct.pack('<Q', 0)                # 表偏移占位
    header += b'\0' * (FILE_BASE - len(header))

    data = bytearray()
    entries = []
    for res_path, local in files:
        blob = open(local, 'rb').read()
        entries.append((res_path, len(data), len(blob), hashlib.md5(blob).digest()))
        data += blob
        while len(data) % 16:
            data.append(0)

    table_off = FILE_BASE + len(data)
    table = struct.pack('<I', len(entries))
    for res_path, ofs, size, md5 in entries:
        p = res_path.encode() + b'\0'
        table += (struct.pack('<I', len(p)) + p
                  + struct.pack('<QQ', ofs, size) + md5 + struct.pack('<I', 0))

    blob = header + bytes(data) + table
    blob = blob[:0x20] + struct.pack('<Q', table_off) + blob[0x28:]
    open(out_path, 'wb').write(blob)
    print(f"pck written: {out_path} ({len(blob)} bytes, {len(entries)} file(s))")


if __name__ == "__main__":
    # 用法: build_pck.py <out.pck> <res 路径(无 res:// 前缀)> <本地文件> [更多对...]
    out = sys.argv[1]
    files = [(sys.argv[i], sys.argv[i + 1]) for i in range(2, len(sys.argv), 2)]
    build(out, files)
