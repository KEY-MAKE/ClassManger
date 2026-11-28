#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""把 credentials.json 加密成 credentials.enc。

用法（在仓库根目录跑）：
    python tools/encrypt-credentials.py

为什么要有这一步：
    credentials.json 里是明文 API Key。它一旦进了 git（哪怕是私有仓库），
    就等于把所有 key 摊在别人面前 —— 之前那次泄露就是公开仓库里的明文文件
    被爬虫扫走，四天烧空了 6 把 key。

    现在改成：明文只留在本地（.gitignore 排除），进仓库的是 AES 加密后的密文。
    门槛从「strings 一搜就有」提高到「得逆程序、找到三段拼起来的密钥、再解密」。

    再次强调：这提高的是成本，不是绝对安全。要绝对安全只能不内置 key、
    让每个用户自己填（设置页已经支持）。

加密格式：Base64( IV(16 字节) + AES-256-CBC 密文 )
密钥派生：SHA256(PASSPHRASE)
"""

import base64
import io
import json
import os
import sys

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.hashes import SHA256
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

# !! 改动这里必须同步改 C# 侧 AiCredentialVault.cs 的三段常量 !!
_PASSPHRASE_PARTS = ["cm", "-ai-", "vault", "-2026", "-K#9", "xQ7"]


def _passphrase() -> bytes:
    return "".join(_PASSPHRASE_PARTS).encode("utf-8")


def encrypt(plain: bytes) -> str:
    key = HKDF(
        algorithm=SHA256(),
        length=32,
        salt=b"classmanager-ai-vault",
        info=b"credentials",
    ).derive(_passphrase())

    iv = os.urandom(16)

    padder = padding.PKCS7(128).padder()
    padded = padder.update(plain) + padder.finalize()

    cipher = Cipher(algorithms.AES(key), modes.CBC(iv))
    encryptor = cipher.encryptor()
    body = encryptor.update(padded) + encryptor.finalize()

    return base64.b64encode(iv + body).decode("ascii")


def main() -> int:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    src = os.path.join(root, "ClassManager.App", "Assets", "ai", "credentials.json")
    dst = os.path.join(root, "ClassManager.App", "Assets", "ai", "credentials.enc")

    if not os.path.exists(src):
        print("[错误] 找不到明文凭据：{}".format(src))
        print("       先把 credentials.json 放回这个位置（它不进 git，只在你本机）")
        return 1

    with io.open(src, "r", encoding="utf-8") as f:
        text = f.read()

    # 校验一下是不是合法 JSON，避免加密一堆垃圾
    try:
        data = json.loads(text)
    except Exception as exc:
        print("[错误] credentials.json 不是合法 JSON：{}".format(exc))
        return 1

    keys = data.get("keys") or []
    if not keys:
        print("[警告] keys 是空的，加密出来的密文没有意义")

    token = encrypt(text.encode("utf-8"))

    with io.open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(token)

    print("[完成] {}  ({} 个 key, {:.1f} KB)".format(
        os.path.relpath(dst, root), len(keys), os.path.getsize(dst) / 1024))

    return 0


if __name__ == "__main__":
    sys.exit(main())
