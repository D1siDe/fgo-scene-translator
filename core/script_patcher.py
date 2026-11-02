# -*- coding: utf-8 -*-
"""
Подмена сценариев FGO (JP): поиск бандлов со сценариями, расшифровка, замена TextAsset, обратное шифрование.

Бандлы сценариев — это файлы 'ScriptActionEncrypt/<глава>' из AssetStorage; в кэше игры они лежат под
хэшированными именами (.bin). Находим их по размеру и CRC32 из AssetStorage_dec.txt.
"""
import io
import json
import re
import zlib
from pathlib import Path

import zlib
from .fgo_crypto import mouse_game4, cat_game4, cat_game4_forged, mouse_game3, cat_game3


class KeyMaps:
    """Ключи шифрования бандлов с 'extra key' (если файла нет в списке — используется ключ по умолчанию)."""
    def __init__(self, keys_json=None, extra_json=None):
        self.keys, self.extra = {}, {}
        if keys_json and Path(keys_json).exists():
            self.keys = {k["id"]: k["decryptKey"] for k in json.loads(Path(keys_json).read_text(encoding="utf-8"))}
        if extra_json and Path(extra_json).exists():
            self.extra = {x["FileName"]: x["AssetName"] for x in json.loads(Path(extra_json).read_text(encoding="utf-8"))}

    def key_for(self, file_name):
        kid = self.extra.get(file_name)
        return self.keys.get(kid) if kid else None


def parse_script_entries(storage_txt):
    """[(size, crc32, asset_name)] для ScriptActionEncrypt/* из AssetStorage_dec.txt."""
    out = []
    for line in Path(storage_txt).read_text(encoding="utf-8", errors="ignore").splitlines():
        p = line.strip().split(",", 4)
        if len(p) == 5 and p[2].isdigit() and p[3].isdigit() and "ScriptActionEncrypt" in p[4]:
            out.append((int(p[2]), int(p[3]), p[4].split(",")[0]))
    return out


def locate_script_bundles(storage_txt, cache_dir):
    """{asset_name: Path} — оригинальные файлы бандлов в кэше (проверка размера и CRC32)."""
    by_size = {}
    for e in parse_script_entries(storage_txt):
        by_size.setdefault(e[0], []).append(e)
    found = {}
    for f in Path(cache_dir).rglob("*"):
        if not f.is_file():
            continue
        cands = by_size.get(f.stat().st_size)
        if not cands:
            continue
        crc = zlib.crc32(f.read_bytes()) & 0xFFFFFFFF
        for size, want, name in cands:
            if crc == want and name not in found:
                found[name] = f
    return found


def _name_key(name):
    return re.sub(r"\D", "", str(name)) or str(name)


def load_translations(translations_dir):
    """{цифры_имени: Path}: 0100000010.txt и 01-00-00-00-1-0.txt дают один ключ."""
    idx = {}
    for p in Path(translations_dir).iterdir():
        if p.is_file() and p.suffix.lower() == ".txt":
            idx.setdefault(_name_key(p.stem), p)
    return idx


def _decrypt_bundle(path, keymaps):
    data = mouse_game4(Path(path).read_bytes(), keymaps.key_for(Path(path).name))
    if not data.startswith(b"UnityFS"):
        raise ValueError(f"{Path(path).name}: после расшифровки нет сигнатуры UnityFS")
    return data


def _script_objects(env):
    import UnityPy  # noqa: F401  (проверка наличия)
    for obj in env.objects:
        if obj.type.name == "TextAsset":
            d = obj.read()
            yield d, (getattr(d, "m_Name", None) or getattr(d, "name", ""))


def _script_text(d):
    s = d.m_Script
    if isinstance(s, str):
        s = s.encode("utf-8", "surrogateescape")
    return bytes(s).decode("ascii").strip()


def list_scripts(path, keymaps):
    """Имена сценариев внутри бандла."""
    import UnityPy
    env = UnityPy.load(io.BytesIO(_decrypt_bundle(path, keymaps)))
    return [name for _, name in _script_objects(env)]


def build_index(bundles, keymaps, index_file=None, log=print):
    """{ключ_имени: asset_name бандла}. Результат кэшируется в index_file (проверка по размеру файла)."""
    cache = {}
    if index_file and Path(index_file).exists():
        try:
            cache = json.loads(Path(index_file).read_text(encoding="utf-8"))
        except Exception:
            cache = {}
    result = {}
    for asset_name, path in bundles.items():
        size = Path(path).stat().st_size
        fp = zlib.crc32(Path(path).read_bytes()) & 0xFFFFFFFF      # после обновления игры размер может совпасть, содержимое - нет
        ent = cache.get(asset_name)
        if not ent or ent.get("size") != size or ent.get("crc") != fp:
            log(f"[ИНДЕКС] читаю {asset_name} ...")
            ent = {"size": size, "crc": fp, "scripts": list_scripts(path, keymaps)}
            cache[asset_name] = ent
        for n in ent["scripts"]:
            result[_name_key(n)] = asset_name
    if index_file:
        Path(index_file).parent.mkdir(parents=True, exist_ok=True)
        Path(index_file).write_text(json.dumps(cache, ensure_ascii=False), encoding="utf-8")
    return result


LAST_FIT = []


def _fit_bundle(env, target):
    """Сохраняет бандл так, чтобы после шифрования файл был ровно target байт (игра сверяет размер).
    Пробует упаковщики от самого близкого к оригиналу к самому плотному."""
    tried = LAST_FIT
    tried.clear()
    for packer in ("original", "lz4", "lzma"):
        try:
            data = env.file.save(packer=packer)
        except Exception as e:
            tried.append(f"{packer}: ошибка {e}")
            continue
        tried.append(f"{packer}: {len(data)}")
        if len(data) <= target - 96:       # 3 блока в хвосте под подгонку CRC32
            tried[-1] += " <- выбран"
            return data
    raise ValueError(f"бандл не помещается в {target} байт ({'; '.join(tried)})")


def pad_for_size(plain, target):
    """Дополняет открытый текст нулями, чтобы длина шифртекста (CBC, блок 32, PKCS7) стала ровно target."""
    if target % 32:
        raise ValueError("размер оригинала не кратен 32")
    need = target - 1 - len(plain)
    if need < 0:
        raise ValueError("данные больше целевого размера")
    return plain + b"\0" * need


def patch_bundle(path, translations, keymaps, keep_size=True):
    """
    Подменяет сценарии в бандле. translations: {ключ_имени: Path к .txt}.
    keep_size=True: результат (после шифрования) ровно такого же размера, как исходный файл.
    Возвращает (зашифрованные_байты | None, [имена заменённых сценариев]).
    """
    # точечная замена данных объекта без пересборки через UnityPy (иначе игра зависает)
    from . import bundle_surgery as bs
    out, done = bs.patch_scripts(path, translations, keymaps, _name_key, mouse_game3, cat_game3, fit=keep_size)
    LAST_FIT[:] = bs.LAST_FIT
    return out, done
    import UnityPy
    key = keymaps.key_for(Path(path).name)
    env = UnityPy.load(io.BytesIO(_decrypt_bundle(path, keymaps)))
    done = []
    for d, name in _script_objects(env):
        tr = translations.get(_name_key(name))
        if not tr:
            continue
        original = mouse_game3(_script_text(d))
        eol = "\r\n" if "\r\n" in original else "\n"
        text = tr.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\n", eol)
        d.m_Script = cat_game3(text)
        d.save()
        done.append(name)
    if not done:
        return None, []
    if keep_size:
        raw = Path(path).read_bytes()
        target, crc = len(raw), zlib.crc32(raw) & 0xFFFFFFFF
        # файл с тем же размером и CRC32, что у исходного (игра сверяет их с AssetStorage)
        out = cat_game4_forged(_fit_bundle(env, target), key, target, crc)
    else:
        out = cat_game4(env.file.save(), key)
    return out, done
