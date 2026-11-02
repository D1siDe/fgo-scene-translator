# -*- coding: utf-8 -*-
"""
Расшифровывает все главы из collected\\ (их передаёт телефон кнопкой 7) и складывает японские тексты сцен:
    scripts_ja\\ScriptActionEncrypt_01\\0100000010.txt
Уже расшифрованные главы пропускаются (проверка по CRC32). Показывает, сколько сцен уже переведено.
Запуск:  python export_scripts.py [--all-versions]
"""
import argparse, json, os, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
from core import script_patcher as sp
from core.export_ja import export_bundle
import server as S

ap = argparse.ArgumentParser()
ap.add_argument("--all-versions", action="store_true", help="расшифровать и старые версии глав, а не только последние")
args = ap.parse_args()

base = Path(os.environ["FGO_DATA_DIR"]) if os.environ.get("FGO_DATA_DIR") else Path(__file__).parent   # FGO_DATA_DIR - папка данных
idx_f = base / "collected" / "index.json"
if not idx_f.exists():
    sys.exit("collected\\index.json нет: нажмите на телефоне кнопку «7. Передать главы на ПК».")
idx = json.loads(idx_f.read_text(encoding="utf-8"))
out = base / "scripts_ja"
out.mkdir(parents=True, exist_ok=True)
state_f = out / "_done.json"
done = json.loads(state_f.read_text(encoding="utf-8")) if state_f.exists() else {}
km = sp.KeyMaps(S.KEYS_JSON, S.EXTRA_JSON)
tdir, _ = S.translations_dir()
translated = set(sp.load_translations(tdir))

if args.all_versions:
    items = list(idx["files"].items())
else:
    items = [(f"{a}|{v['crc']}", idx["files"][f"{a}|{v['crc']}"]) for a, v in idx["latest"].items() if f"{a}|{v['crc']}" in idx["files"]]

total = tr = 0
for key, ent in sorted(items):
    asset = key.split("|")[0]
    folder = out / asset.replace("/", "_")
    src = base / "collected" / ent["file"]
    if done.get(key) and folder.exists():
        total += done[key]["scenes"]; tr += done[key]["translated"]
        continue
    try:
        n, t = export_bundle(src, ent["device_file"], asset, out, km, translated)
        done[key] = {"scenes": n, "translated": t}
        total += n; tr += t
        print(f"[OK] {asset}: сцен {n}, переведено {t}")
    except Exception as ex:
        print(f"[ОШИБКА] {asset}: {ex}")
state_f.write_text(json.dumps(done, indent=1), encoding="utf-8")
print(f"\nГотово: глав {len(items)}, сцен {total}, переведено {tr}. Тексты в {out}")
