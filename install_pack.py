# -*- coding: utf-8 -*-
"""
Ставит пак перевода на устройство (телефон) по adb. Это ЭТАЛОН того, что будет делать приложение на телефоне:
  1. читает локальный список игры, находит файл главы на устройстве (по CRC32 из списка);
  2. сверяет CRC32 оригинала с записью в паке (разные версии данных -> пак не подходит, об этом сообщается);
  3. сохраняет оригинал в fgo_backups_<serial>\\, записывает файл из пака, проверяет md5.

  python install_pack.py --serial <серийник|ip:порт> [--pack translate_pack.zip | --pack pack] [--check] [--restore]
  --check    только проверить совместимость, ничего не записывая
  --restore  вернуть оригиналы
"""
import argparse
import json
import re
import sys
import zipfile
from pathlib import Path

from core.device import Device, DeviceError

ap = argparse.ArgumentParser()
ap.add_argument("--serial", required=True)
ap.add_argument("--pack", default="translate_pack.zip")
ap.add_argument("--check", action="store_true")
ap.add_argument("--restore", action="store_true")
a = ap.parse_args()

safe = re.sub(r"[^0-9A-Za-z._-]+", "_", a.serial)
dev = Device(Path(f"fgo_backups_{safe}"), serial=a.serial)
if not dev.ready():
    sys.exit(f"Устройство {a.serial} не отвечает по adb (adb devices)")

if a.restore:
    done = dev.restore_all()
    print("Возвращено: " + (", ".join(done) if done else "ничего (нашей сборки на устройстве нет)"))
    sys.exit(0)

pack = Path(a.pack)
if pack.is_file():
    z = zipfile.ZipFile(pack)
    manifest = json.loads(z.read("manifest.json").decode("utf-8"))
    read = z.read
else:
    manifest = json.loads((pack / "manifest.json").read_text(encoding="utf-8"))
    read = lambda n: (pack / n).read_bytes()

m, ver = dev.read_list()
entries = dev.script_entries(m)
print(f"Версия данных на устройстве: {ver}; пак собран для: {manifest['game_version']}")
files = dev.listing()
ok = bad = 0
for b in manifest["bundles"]:
    asset = b["asset"]
    e = entries.get(asset)
    if not e:
        print(f"[ПРОПУСК] {asset}: нет в списке игры"); bad += 1; continue
    if e["flag"] != "1":
        print(f"[ПРОПУСК] {asset}: глава ещё не скачана игрой (откройте любую сцену этой главы)"); bad += 1; continue
    if (e["size"], e["crc"]) != (b["orig_size"], b["orig_crc"]):
        print(f"[НЕ ПОДХОДИТ] {asset}: у игры на устройстве CRC32 {e['crc']} (размер {e['size']}), "
              f"в паке {b['orig_crc']} ({b['orig_size']}). Версия главы другая - соберите пак с этого устройства: "
              f"python build_pack.py --serial {a.serial}"); bad += 1; continue
    name = dev.find_file(asset, e["size"], e["crc"], files, log=print)
    if not name:
        print(f"[ПРОПУСК] {asset}: на устройстве нет файла с нужным CRC32 (игра ещё докачивает данные?)"); bad += 1; continue
    if a.check:
        print(f"[ОК] {asset}: подходит, файл {name}"); ok += 1; continue
    data = read(b["file"])
    done, md5 = dev.install(name, lambda base, d=data, s=b["scenes"]: (d, s), Path("fgo_local_mods") / ("phone_" + b["file"]), entry=e)
    print(f"[УСТАНОВЛЕНО] {asset}: сцен {len(done)}, {len(data)} Б, md5 {md5}"); ok += 1
print(f"\nИтого: подходящих/установленных {ok}, пропущено {bad}")
