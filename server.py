# -*- coding: utf-8 -*-
"""
FGO Local Server v3 — подмена сюжетных сценариев (JP) с реальной расшифровкой бандлов.

Эндпоинты:
  GET /status          состояние сервера (для приложения)
  GET|POST /patch      подменяет сценарии из translations/scenarios; если эмулятор доступен по adb —
                       сам забирает текущий файл, собирает перевод и ставит его на устройство.
                       Копия результата лежит в fgo_local_mods/
  GET|POST /restore    возвращает оригиналы на устройство (из fgo_backups/)
  GET /<путь>          отдача файла игре (оригинал или пропатченный сценарий)
"""
import hashlib
import json
import logging
import os
import re
import threading
import zlib
from pathlib import Path

import requests
from flask import Flask, Response, make_response, request, send_file, jsonify

from core import script_patcher as sp
from core import wrap_ru
from core.device import Device, DeviceError
from core.manifest import Manifest
from core.export_ja import export_bundle

# --- КОНФИГУРАЦИЯ ---
BASE_DIR = Path(__file__).parent
RAW_CACHE_DIR = BASE_DIR / "raw_game_cache"
MODS_OUT_DIR = BASE_DIR / "fgo_local_mods"
TRANSLATIONS_DIR = Path(os.environ["FGO_TRANSLATIONS"]) if os.environ.get("FGO_TRANSLATIONS") else BASE_DIR / "translations" / "scenarios"   # можно перенести папку: $env:FGO_TRANSLATIONS = "D:\\...\\scenarios"
CUSTOM_FONT_PATH = BASE_DIR / "assets" / "font.ttf"
EXTERNAL_DIR = BASE_DIR / "external_decryptor"
ASSET_STORAGE = EXTERNAL_DIR / "Android" / "AssetStorage_dec.txt"   # создаёт пункт 4/5 FGOAssetsModifyTool
KEYS_JSON = EXTERNAL_DIR / "Android" / "gamedata" / "unpack_assetbundle" / "assetbundleKey.json"
EXTRA_JSON = EXTERNAL_DIR / "AssetListWithExtraKeyType.json"
INDEX_FILE = BASE_DIR / "cache" / "script_index.json"
BACKUP_DIR = BASE_DIR / "fgo_backups"
WRAPPED_DIR = BASE_DIR / "translations_wrapped"          # сюда кладутся переводы с расставленными переносами [r]
WRAP_ENABLED = os.environ.get("FGO_WRAP", "1") != "0"
WRAP_WIDTH = float(os.environ.get("FGO_WRAP_WIDTH", "34"))   # ширина строки в «условных буквах» (окно вмещает ~34)
WRAP_LINES = int(os.environ.get("FGO_WRAP_LINES", "2"))      # строк в окне диалога
WRAP_TAG = os.environ.get("FGO_WRAP_TAG", "")                # опыт: оборачивать реплики в NGUI-тег (sup/sub), пусто = не оборачивать

for d in (RAW_CACHE_DIR, MODS_OUT_DIR, TRANSLATIONS_DIR, CUSTOM_FONT_PATH.parent):
    d.mkdir(parents=True, exist_ok=True)

FGO_JP_BASE = "https://game.fate-go.jp"


# --- ЛОГИРОВАНИЕ ---
class ColorFormatter(logging.Formatter):
    colors = {'green': '\033[92m', 'yellow': '\033[93m', 'blue': '\033[94m', 'red': '\033[91m', 'reset': '\033[0m'}

    def format(self, record):
        color = ''
        if '[СКАЧИВАНИЕ]' in record.msg: color = self.colors['blue']
        elif '[ОРИГИНАЛ]' in record.msg: color = self.colors['green']
        elif '[ПАТЧ]' in record.msg or '[СЮЖЕТ ЗАМЕНЕН]' in record.msg: color = self.colors['yellow']
        elif '[ОШИБКА]' in record.msg: color = self.colors['red']
        return f"{color}{super().format(record)}{self.colors['reset']}"


logger = logging.getLogger(__name__)
logger.setLevel(logging.INFO)
_h = logging.StreamHandler()
_h.setFormatter(ColorFormatter('%(message)s'))
logger.addHandler(_h)

app = Flask(__name__)
_lock = threading.Lock()
_ctx = {}
# adb: FGO_ADB (путь), FGO_ADB_SERIAL (по умолчанию эмулятор 127.0.0.1:16384).
# У каждого устройства свои резервные копии оригиналов: у телефона другая версия глав, смешивать их нельзя.
_SERIAL = os.environ.get("FGO_ADB_SERIAL", "127.0.0.1:16384")
if _SERIAL == "127.0.0.1:16384":
    _BDIR = BACKUP_DIR
else:
    import re as _re
    _BDIR = BASE_DIR / ("fgo_backups_" + _re.sub(r"[^0-9A-Za-z._-]+", "_", _SERIAL))
device = Device(_BDIR)


def translations_dir():
    """Папка с переводами для сборки: исходные (translations/scenarios) или с автопереносами строк."""
    if not WRAP_ENABLED:
        return TRANSLATIONS_DIR, None
    st = wrap_ru.prepare(TRANSLATIONS_DIR, WRAPPED_DIR, WRAP_WIDTH, WRAP_LINES, WRAP_TAG)
    if st["rebuilt"]:
        logger.info(f"[ИНФО] Переносы строк: обновлено {st['rebuilt']} из {st['files']} файлов "
                    f"(ширина {WRAP_WIDTH}, строк {WRAP_LINES}, добавлено страниц {st['extra_pages']})")
    return WRAPPED_DIR, st


def get_ctx(refresh=False):
    """Бандлы сценариев + ключи. Если эмулятор доступен по adb, всё берётся из ЖИВОГО списка игры на устройстве
    (версия данных, размеры и CRC32), поэтому после обновления игры ничего вручную обновлять не надо.
    Без устройства используется старый способ: AssetStorage_dec.txt + raw_game_cache."""
    with _lock:
        if refresh or "bundles" not in _ctx:
            keymaps = sp.KeyMaps(KEYS_JSON, EXTRA_JSON)
            if device.ready():
                scan = device.scan_scripts(log=logger.info)
                logger.info(f"[ИНФО] Версия данных игры: {scan['version']}; глав сценариев на устройстве: {len(scan['bundles'])}")
                for pr in scan["problems"]:
                    logger.info(f"[ОШИБКА] {pr}")
                entries = {a: {"size": b["size"], "crc": b["crc"]} for a, b in scan["bundles"].items()}
                _ctx.update(keymaps=keymaps, live=True, version=scan["version"], problems=scan["problems"],
                            device_files={a: b["file"] for a, b in scan["bundles"].items()}, entries=entries,
                            bundles={a: None for a in entries}, by_file={b["file"]: a for a, b in scan["bundles"].items()},
                            found={})
            else:
                if not ASSET_STORAGE.exists():
                    raise FileNotFoundError(f"Устройство по adb недоступно и нет {ASSET_STORAGE}")
                bundles = sp.locate_script_bundles(ASSET_STORAGE, RAW_CACHE_DIR)
                logger.info(f"[ИНФО] Бандлов сценариев найдено в кэше: {len(bundles)}")
                _ctx.update(keymaps=keymaps, live=False, version="?", problems=[], bundles=bundles,
                            by_file={p.name: n for n, p in bundles.items()}, found={})
        return _ctx


def bundle_path(ctx, asset):
    """Локальный путь к оригиналу бандла (в живом режиме - проверенная копия из fgo_backups)."""
    p = ctx["bundles"].get(asset)
    if p is None and ctx.get("live"):
        p = device.original_path(ctx["device_files"][asset], ctx["entries"][asset])
        ctx["bundles"][asset] = p
    return p


def bundle_for(key, ctx):
    """В каком бандле лежит сценарий с ключом key (цифры имени). Сначала бандл главы (первые 2 цифры), затем остальные.
    Содержимое каждого бандла читается один раз и кэшируется в INDEX_FILE."""
    with _lock:
        if key in ctx["found"]:
            return ctx["found"][key]
        names = list(ctx["bundles"])
        pref = [n for n in names if n.rstrip("/").split("/")[-1] == key[:2]]
        for n in pref + [n for n in names if n not in pref]:
            try:
                idx = sp.build_index({n: bundle_path(ctx, n)}, ctx["keymaps"], INDEX_FILE, log=logger.info)
            except DeviceError as e:
                logger.info(f"[ОШИБКА] {e}")
                continue
            if key in idx:
                ctx["found"][key] = n
                return n
        ctx["found"][key] = None
        return None


def patch_script_bundle(file_path):
    """(bytes|None, [имена]) — патчит файл, если это бандл сценариев и для него есть переводы."""
    try:
        ctx = get_ctx()
        asset = ctx["by_file"].get(Path(file_path).name)
        if not asset:
            return None, []
        translations = sp.load_translations(translations_dir()[0])
        mine = {k: v for k, v in translations.items() if bundle_for(k, ctx) == asset}
        if not mine:
            return None, []
        data, done = sp.patch_bundle(file_path, mine, ctx["keymaps"])
        for n in done:
            logger.info(f"[СЮЖЕТ ЗАМЕНЕН] {n} ({asset})")
        return data, done
    except Exception as e:
        logger.info(f"[ОШИБКА] {Path(file_path).name}: {e}")
        return None, []


def make_builder(trs, keymaps, skipped):
    """Сборка бандла. Размер файла свободен (игра проверяет только CRC32, он подгоняется под оригинал);
    ветка «не помещается» осталась как страховка на случай, если подгонка не удалась."""
    def build(base):
        cur = dict(trs)
        while cur:
            try:
                return sp.patch_bundle(base, cur, keymaps)
            except ValueError as e:
                if "не помещается" not in str(e):
                    raise
                k = max(cur, key=lambda x: cur[x].stat().st_size)
                skipped.append(cur.pop(k).name)
                logger.info(f"[ПАТЧ] не хватает места ({e}); пропускаю {skipped[-1]}")
        return None, []
    return build


@app.route('/', methods=['GET'])
@app.route('/status', methods=['GET'])
def server_status():
    scenarios_count = len(list(TRANSLATIONS_DIR.glob("*.txt")))
    cached_bundles = [p for p in RAW_CACHE_DIR.rglob("*") if p.is_file()]
    return jsonify({
        "status": "online",
        "font_ready": CUSTOM_FONT_PATH.exists(),
        "scenarios_count": scenarios_count,
        "cached_bundles": len(cached_bundles),
        "device_ready": device.ready(),
        "adb": device.adb_path
    }), 200


@app.route('/scan', methods=['GET'])
def scan_game():
    """Диагностика: версия данных игры и найденные на устройстве главы сценариев."""
    try:
        ctx = get_ctx(refresh=True)
    except Exception as e:
        return jsonify({"status": "error", "error": str(e)}), 500
    return jsonify({"status": "ok", "live": ctx.get("live"), "version": ctx.get("version"),
                    "bundles": ctx.get("device_files", {}), "problems": ctx.get("problems", [])}), 200


@app.route('/patch', methods=['POST', 'GET'])
def apply_patch_batch():
    try:
        ctx = get_ctx(refresh=True)
    except Exception as e:
        logger.info(f"[ОШИБКА] {e}")
        return jsonify({"status": "error", "error": str(e), "total_scanned": 0, "patched_count": 0}), 500

    tdir, wrap_stat = translations_dir()
    translations = sp.load_translations(tdir)
    by_bundle, missing = {}, []
    for key, tr in translations.items():
        asset = bundle_for(key, ctx)
        if asset:
            by_bundle.setdefault(asset, {})[key] = tr
        else:
            missing.append(tr.name)

    use_device = bool(ctx.get("live"))
    logger.info(f"[ИНФО] Режим: {'установка на устройство (adb)' if use_device else 'только файлы в fgo_local_mods (устройство по adb недоступно)'}")
    patched_names, errors, scripts_done, skipped, sizes = [], [], [], [], {}
    for asset, trs in by_bundle.items():
        try:
            if ctx.get("live"):
                fname = ctx["device_files"][asset]
                rel = Path(asset)
                out = MODS_OUT_DIR / rel.parent / (rel.name + ".bin")
            else:
                src = ctx["bundles"][asset]
                rel = src.relative_to(RAW_CACHE_DIR)
                out = MODS_OUT_DIR / rel
            if use_device and ctx.get("live"):
                done, md5 = device.install(fname, make_builder(trs, ctx["keymaps"], skipped), out, entry=ctx["entries"][asset])
                if out.exists():
                    sizes[str(rel)] = out.stat().st_size
                logger.info(f"[ПАТЧ] {asset}: заменено сценариев {len(done)}, файл {sizes.get(str(rel), '?')} Б, "
                            f"установлено на устройство (md5 {md5})")
            elif use_device:
                done, md5 = device.install(src.name, make_builder(trs, ctx["keymaps"], skipped), out)
                if out.exists():
                    sizes[str(rel)] = out.stat().st_size
                logger.info(f"[ПАТЧ] {asset}: заменено сценариев {len(done)}, файл {sizes.get(str(rel), '?')} Б, "
                            f"установлено на устройство (md5 {md5})")
            else:
                data, done = make_builder(trs, ctx["keymaps"], skipped)(src if not ctx.get("live") else bundle_path(ctx, asset))
                if not data:
                    continue
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_bytes(data)
                sizes[str(rel)] = len(data)
                logger.info(f"[ПАТЧ] {asset}: заменено сценариев {len(done)} -> {out}")
        except Exception as e:
            logger.info(f"[ОШИБКА] {asset}: {e}")
            errors.append(f"{asset}: {e}")
            continue
        patched_names.append(str(rel))
        scripts_done += done

    return jsonify({
        "status": "completed",
        "mode": "device" if use_device else "files",
        "total_scanned": len(ctx["bundles"]),
        "game_version": ctx.get("version"),
        "problems": ctx.get("problems", []),
        "patched_count": len(patched_names),
        "patched_files": patched_names,
        "scripts_replaced": scripts_done,
        "translations_without_bundle": missing,
        "skipped_no_space": skipped,
        "bundle_sizes": sizes,
        "wrap": wrap_stat or {"enabled": False},
        "errors": errors
    }), 200


@app.route('/restore', methods=['POST', 'GET'])
def restore_originals():
    if not device.ready():
        return jsonify({"status": "error", "error": "устройство по adb недоступно"}), 500
    try:
        restored = device.restore_all()
    except DeviceError as e:
        logger.info(f"[ОШИБКА] {e}")
        return jsonify({"status": "error", "error": str(e)}), 500
    logger.info(f"[ИНФО] Возвращены оригиналы: {restored}")
    return jsonify({"status": "restored", "restored": restored}), 200


BUILD_CACHE = BASE_DIR / "cache" / "build"
_build_lock = threading.Lock()
_keymaps_cache = {}


DATA_DIR = Path(os.environ["FGO_DATA_DIR"]) if os.environ.get("FGO_DATA_DIR") else BASE_DIR   # куда складывать collected\, scripts_ja\, work\
SCRIPTS_JA_DIR = DATA_DIR / "scripts_ja"
COLLECT_DIR = DATA_DIR / "collected"
COLLECT_INDEX = COLLECT_DIR / "index.json"
_collect_lock = threading.Lock()


def _collect_load():
    try:
        return json.loads(COLLECT_INDEX.read_text(encoding="utf-8"))
    except Exception:
        return {"files": {}, "latest": {}, "version": "?"}


@app.route('/sync/list', methods=['POST'])
def sync_list():
    """Телефон присылает локальный список ресурсов игры; в ответ - какие скачанные главы сценариев ещё не собраны на ПК."""
    data = request.get_data()
    try:
        m = Manifest(data)
        entries = device.script_entries(m)
    except Exception as e:
        return jsonify({"status": "error", "error": f"не удалось разобрать список: {e}"}), 400
    ver = m.lines[1].split(",")[-1].strip() if len(m.lines) > 1 else "?"
    with _collect_lock:
        idx = _collect_load()
        idx["version"] = ver
        COLLECT_DIR.mkdir(parents=True, exist_ok=True)
        COLLECT_INDEX.write_text(json.dumps(idx, ensure_ascii=False, indent=1), encoding="utf-8")
        need, have, notdl = [], 0, 0
        for asset, e in sorted(entries.items()):
            if e["flag"] != "1":
                notdl += 1
                continue
            if f"{asset}|{e['crc']}" in idx["files"]:
                have += 1
            else:
                need.append({"asset": asset, "size": e["size"], "crc": e["crc"]})
    logger.info(f"[СИНХР] версия {ver}: глав в списке {len(entries)}, уже на ПК {have}, не скачано в игре {notdl}, нужно получить {len(need)}")
    return jsonify({"status": "ok", "version": ver, "have": have, "not_downloaded": notdl, "need": need}), 200


@app.route('/sync/file', methods=['POST'])
def sync_file():
    """Телефон присылает оригинал главы. Заголовки: X-Asset, X-Device-File, X-CRC (десятичный CRC32 из списка игры)."""
    data = request.get_data()
    asset = request.headers.get("X-Asset", "")
    dev = request.headers.get("X-Device-File", "")
    try:
        crc = int(request.headers.get("X-CRC", ""))
    except ValueError:
        return jsonify({"status": "error", "error": "нет X-CRC"}), 400
    if not asset.startswith("ScriptActionEncrypt/") or not re.fullmatch(r"[0-9A-Za-z._-]+", dev):
        return jsonify({"status": "error", "error": "неверные заголовки"}), 400
    if (zlib.crc32(data) & 0xFFFFFFFF) != crc:
        return jsonify({"status": "error", "error": "CRC32 присланного файла не совпал со списком"}), 422
    name = f"{crc:08x}_{asset.replace('/', '_')}.bin"
    with _collect_lock:
        COLLECT_DIR.mkdir(parents=True, exist_ok=True)
        (COLLECT_DIR / name).write_bytes(data)
        idx = _collect_load()
        idx["files"][f"{asset}|{crc}"] = {"file": name, "device_file": dev, "size": len(data)}
        idx["latest"][asset] = {"device_file": dev, "crc": crc, "size": len(data), "file": name}
        COLLECT_INDEX.write_text(json.dumps(idx, ensure_ascii=False, indent=1), encoding="utf-8")
    logger.info(f"[СИНХР] получено {asset}: {len(data) // 1024} КБ -> collected/{name}")
    try:
        km = _keymaps_cache.get("km") or _keymaps_cache.setdefault("km", sp.KeyMaps(KEYS_JSON, EXTRA_JSON))
        n, t = export_bundle(COLLECT_DIR / name, dev, asset, SCRIPTS_JA_DIR, km, set(sp.load_translations(translations_dir()[0])))
        logger.info(f"[СИНХР] {asset}: расшифровано сцен {n}, переведено {t} -> scripts_ja/")
    except Exception as e:
        logger.info(f"[ОШИБКА] расшифровка {asset}: {e}")
    return jsonify({"status": "ok"}), 200


@app.route('/sync/status', methods=['GET'])
def sync_status():
    idx = _collect_load()
    return jsonify({"version": idx.get("version"), "assets": sorted(idx["latest"]), "files": len(idx["files"])}), 200


def _stamp(keys, translations):
    """Отпечаток переводов для набора сцен: меняется, когда меняется любой из файлов переводов этих сцен."""
    sig = "|".join(f"{k}:{translations[k].stat().st_size}:{int(translations[k].stat().st_mtime)}" for k in sorted(keys) if k in translations)
    return hashlib.sha1(sig.encode()).hexdigest()[:12] if sig else ""


@app.route('/sync/check', methods=['POST'])
def sync_check():
    """Телефон присылает список игры; в ответ - главы, для которых есть переводы: текущие размер/CRC32 из списка, файл на устройстве, отпечаток переводов."""
    try:
        m = Manifest(request.get_data())
        entries = device.script_entries(m)
    except Exception as e:
        return jsonify({"status": "error", "error": f"не удалось разобрать список: {e}"}), 400
    translations = sp.load_translations(translations_dir()[0])
    idx = _collect_load()
    res = []
    for asset, e in sorted(entries.items()):
        folder = SCRIPTS_JA_DIR / asset.replace("/", "_")
        if e["flag"] != "1" or not folder.exists():
            continue
        keys = {sp._name_key(p.stem) for p in folder.glob("*.txt")}
        mine = keys & set(translations)
        if not mine:
            continue
        lat = idx["latest"].get(asset)
        res.append({"asset": asset, "size": e["size"], "crc": e["crc"], "device_file": lat["device_file"] if lat else "",
                    "translated": len(mine), "stamp": _stamp(mine, translations)})
    return jsonify({"status": "ok", "bundles": res}), 200


@app.route('/pack_info', methods=['GET'])
def pack_info():
    """Какие главы переводятся и как называются их файлы на устройстве (из последнего build_pack.py)."""
    idx = _collect_load()
    if idx["latest"]:
        return jsonify({"game_version": idx.get("version"),
                        "bundles": [{"asset": a, "device_file": v["device_file"], "size": v["size"], "crc": v["crc"]} for a, v in sorted(idx["latest"].items())]}), 200
    mf = BASE_DIR / "pack" / "manifest.json"
    if not mf.exists():
        return jsonify({"status": "error", "error": "pack/manifest.json нет: выполните python build_pack.py"}), 404
    m = json.loads(mf.read_text(encoding="utf-8"))
    return jsonify({"game_version": m.get("game_version"),
                    "bundles": [{"asset": b["asset"], "device_file": b["device_file"]} for b in m["bundles"]]}), 200


@app.route('/build', methods=['POST'])
def build_from_original():
    """Телефон присылает ОРИГИНАЛ файла главы (любой версии), сервер возвращает его с подставленным переводом
    (размер свободный, CRC32 подогнан под оригинал). Заголовок X-Device-File - имя файла на устройстве."""
    data = request.get_data()
    fname = request.headers.get("X-Device-File", "").strip()
    if not data or not fname or "/" in fname or "\\" in fname or ".." in fname:
        return jsonify({"status": "error", "error": "нужны тело запроса и заголовок X-Device-File"}), 400
    tdir, _ = translations_dir()
    translations = sp.load_translations(tdir)
    sig = "|".join(f"{k}:{v.stat().st_size}:{int(v.stat().st_mtime)}" for k, v in sorted(translations.items()))
    key = hashlib.sha1(b"v2" + hashlib.md5(data).digest() + sig.encode() + fname.encode()).hexdigest()
    BUILD_CACHE.mkdir(parents=True, exist_ok=True)
    out_f, meta_f = BUILD_CACHE / f"{key}.bin", BUILD_CACHE / f"{key}.json"
    with _build_lock:
        if out_f.exists() and meta_f.exists():
            meta = json.loads(meta_f.read_text(encoding="utf-8"))
            body = out_f.read_bytes()
            logger.info(f"[ПАТЧ] {fname}: из кэша, сцен {meta['scenes']}")
        else:
            work = BUILD_CACHE / "in"
            work.mkdir(exist_ok=True)
            src = work / fname
            src.write_bytes(data)
            try:
                km = _keymaps_cache.get("km") or _keymaps_cache.setdefault("km", sp.KeyMaps(KEYS_JSON, EXTRA_JSON))
                keys = {sp._name_key(n) for n in sp.list_scripts(src, km)}
                mine = {k: v for k, v in translations.items() if k in keys}
                if not mine:
                    return Response(status=204)
                logger.info(f"[ПАТЧ] {fname}: собираю перевод для телефона ({len(mine)} сцен)...")
                body, done = sp.patch_bundle(src, mine, km)
                stamp = _stamp(mine.keys(), translations)
            except Exception as e:
                logger.info(f"[ОШИБКА] {fname}: {e}")
                return jsonify({"status": "error", "error": str(e)}), 500
            if not body:
                return Response(status=204)
            out_f.write_bytes(body)
            meta = {"scenes": len(done), "stamp": stamp}
            meta_f.write_text(json.dumps(meta), encoding="utf-8")
            logger.info(f"[ПАТЧ] {fname}: готово, {len(done)} сцен, {len(body)} Б")
    return Response(body, mimetype="application/octet-stream",
                    headers={"X-Scenes": str(meta["scenes"]), "X-Stamp": meta.get("stamp", ""), "X-MD5": hashlib.md5(body).hexdigest()})


@app.route('/pack.zip', methods=['GET'])
def download_pack():
    """Пак перевода для приложения на телефоне (собирается командой python build_pack.py)."""
    p = BASE_DIR / "translate_pack.zip"
    if not p.exists():
        return jsonify({"status": "error", "error": "translate_pack.zip нет: выполните python build_pack.py"}), 404
    return send_file(p, mimetype="application/zip", as_attachment=False)


@app.route('/<path:asset_path>', methods=['GET'])
def handle_asset(asset_path):
    local_file_path = RAW_CACHE_DIR / asset_path
    if not local_file_path.exists():
        logger.info(f"[СКАЧИВАНИЕ] Загрузка оригинала: {asset_path}")
        try:
            resp = requests.get(f"{FGO_JP_BASE}/{asset_path}", stream=True,
                                headers={"User-Agent": "UnityPlayer/2022.3.x (UnityWebRequest)"})
            if resp.status_code != 200:
                return "Not Found", 404
            local_file_path.parent.mkdir(parents=True, exist_ok=True)
            with open(local_file_path, 'wb') as f:
                for chunk in resp.iter_content(8192):
                    f.write(chunk)
        except Exception as e:
            logger.info(f"[ОШИБКА] {e}")
            return "Server Error", 500

    data, done = patch_script_bundle(local_file_path)
    if data:
        response = make_response(data)
        response.headers['Content-Type'] = 'application/octet-stream'
        return response
    logger.info(f"[ОРИГИНАЛ] {asset_path}")
    return send_file(local_file_path)


if __name__ == '__main__':
    print("=" * 60)
    print("FGO Local Server v3 запущен на http://127.0.0.1:5000")
    print("=" * 60)
    # FGO_HOST=0.0.0.0 - чтобы телефон достучался до ПК по Wi-Fi (по умолчанию только этот компьютер / adb reverse)
    app.run(host=os.environ.get("FGO_HOST", "127.0.0.1"), port=5000, debug=False, use_reloader=False)
