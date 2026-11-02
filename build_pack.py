# -*- coding: utf-8 -*-
"""
Собирает ПАК ПЕРЕВОДА для телефона: готовые пропатченные бандлы + manifest.json.
Оригиналы берутся с устройства по adb (эмулятор по умолчанию или телефон: --serial).
Пак подходит любому устройству с той же версией глав: приложение/скрипт сверяет CRC32 оригинала с записью в паке.

  python build_pack.py [--serial 127.0.0.1:16384] [--out pack]
Результат: pack\\manifest.json, pack\\*.bin и translate_pack.zip
"""
import argparse
import hashlib
import json
import os
import sys
import zipfile
from datetime import datetime
from pathlib import Path

ap = argparse.ArgumentParser()
ap.add_argument("--serial", help="устройство, с которого берутся оригиналы (по умолчанию FGO_ADB_SERIAL / эмулятор)")
ap.add_argument("--out", default="pack")
a = ap.parse_args()
if a.serial:
    os.environ["FGO_ADB_SERIAL"] = a.serial

import server as S                    # noqa: E402  (используем те же функции, что и /patch)
from core import script_patcher as sp  # noqa: E402

out = Path(a.out)
out.mkdir(parents=True, exist_ok=True)
for old in out.glob("*.bin"):
    old.unlink()

if not S.device.ready():
    sys.exit(f"Устройство {S.device.serial} по adb недоступно ({S.device.last_error}): проверьте adb devices и --serial")
ctx = S.get_ctx(refresh=True)
if not ctx.get("live"):
    sys.exit(f"Не удалось прочитать список игры с устройства {S.device.serial}: соединение с adb сорвалось "
             f"({S.device.last_error or 'причина неизвестна'}). Повторите команду; если повторяется - adb kill-server и adb devices")

tdir, wrap = S.translations_dir()
translations = sp.load_translations(tdir)
by_bundle = {}
for key, tr in translations.items():
    asset = S.bundle_for(key, ctx)
    if asset:
        by_bundle.setdefault(asset, {})[key] = tr
    else:
        print(f"[ПРЕДУПРЕЖДЕНИЕ] для перевода {tr.name} нет бандла")

manifest = {"format": 1, "game_version": ctx["version"], "built": datetime.now().isoformat(timespec="seconds"),
            "wrap": wrap, "bundles": []}
for asset, trs in by_bundle.items():
    entry = ctx["entries"][asset]
    base = S.bundle_path(ctx, asset)                       # проверенный оригинал (размер и CRC32 из списка игры)
    skipped = []
    data, done = S.make_builder(trs, ctx["keymaps"], skipped)(base)
    if not data:
        print(f"[ОШИБКА] {asset}: не собрано (пропущено: {skipped})")
        continue
    fname = asset.replace("/", "_") + ".bin"
    (out / fname).write_bytes(data)
    manifest["bundles"].append({
        "asset": asset, "orig_size": entry["size"], "orig_crc": entry["crc"],
        "device_file": ctx["device_files"][asset], "file": fname, "size": len(data),
        "md5": hashlib.md5(data).hexdigest(), "scenes": sorted(done)})
    print(f"[ПАК] {asset}: сцен {len(done)}, файл {len(data)} Б (оригинал {entry['size']} Б, CRC32 {entry['crc']})")

(out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
zp = Path("translate_pack.zip")
with zipfile.ZipFile(zp, "w", zipfile.ZIP_STORED) as z:
    z.write(out / "manifest.json", "manifest.json")
    for b in manifest["bundles"]:
        z.write(out / b["file"], b["file"])
print(f"\nГотово: {zp} ({zp.stat().st_size / 1e6:.2f} МБ), версия данных {manifest['game_version']}, бандлов {len(manifest['bundles'])}")
