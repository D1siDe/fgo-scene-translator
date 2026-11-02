# -*- coding: utf-8 -*-
"""
Точечная замена данных объектов в UnityFS-бандле БЕЗ пересборки через UnityPy.

Почему: UnityPy при env.file.save() пересобирает SerializedFile (для Unity 6 теряет ~2 КБ служебных данных),
и игра на таком бандле зависает. Здесь содержимое CAB остаётся байт-в-байт тем же, меняются только:
  - данные заменяемых объектов (дописываются в конец CAB, либо на месте, если влезают),
  - запись объекта в таблице (byte_start/byte_size),
  - поле file_size в заголовке SerializedFile.
Дальше блок сжимается заново, собирается заголовок UnityFS, делается подгонка размера/CRC32 под оригинал.
"""
import io
import lzma
import struct
import zlib
from pathlib import Path

from .fgo_crypto import mouse_game4, cat_game4, cat_game4_forged, script_format

try:
    import lz4.block as _lz4
except ImportError:                                  # lz4 ставится вместе с UnityPy
    _lz4 = None

LAST_FIT = []


def _a(x, n):
    return (x + n - 1) // n * n


def _cstr(b, p):
    e = b.index(b"\0", p)
    return b[p:e], e + 1


# ---------------------------------------------------------------- UnityFS
def _lz4_dec(d, n):
    return _lz4.decompress(d, uncompressed_size=n)


def _lz4_hc(d):
    return _lz4.compress(d, mode="high_compression", store_size=False)


def _lzma_dec(block, usize):
    pr = block[0]
    dic = int.from_bytes(block[1:5], "little")
    dec = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[
        {"id": lzma.FILTER_LZMA1, "dict_size": dic, "lc": pr % 9, "lp": (pr // 9) % 5, "pb": pr // 45}])
    return dec.decompress(block[5:], max_length=usize)


def _lzma_enc(cab, props, enough=None):
    """Сжимает LZMA1 (raw): перебирает размер словаря и lc/lp/pb, берёт лучшее. Возвращает props(5)+поток."""
    dic0 = int.from_bytes(props[1:5], "little")
    base = props[0]
    first = (base % 9, (base // 9) % 5, base // 45)
    grid = [first] + [(lc, lp, pb) for lc in (0, 1, 2, 3, 4) for lp in (0, 1) for pb in (0, 1, 2) if lc + lp <= 4]
    grid = list(dict.fromkeys(grid))
    dicts = list(dict.fromkeys([1 << 22, dic0, 1 << 20, 1 << 23, 1 << 24]))
    # сначала все словари с исходными lc/lp/pb, потом остальные lc/lp/pb со словарём 4 МБ
    cands = [(d,) + first for d in dicts] + [(dicts[0],) + g for g in grid[1:]]
    best = None
    for dic, lc, lp, pb in cands:
        if best is not None and enough is not None and len(best[0]) <= enough:
            break                       # уже влезает - дальше не ищем (перебор долгий)
        c = lzma.LZMACompressor(format=lzma.FORMAT_RAW, filters=[
            {"id": lzma.FILTER_LZMA1, "preset": 9 | lzma.PRESET_EXTREME, "dict_size": dic,
             "lc": lc, "lp": lp, "pb": pb, "nice_len": 273, "mf": lzma.MF_BT4, "depth": 1000}])
        out = c.compress(cab) + c.flush()
        if best is None or len(out) < len(best[0]):
            best = (out, dic, lc, lp, pb)
    out, dic, lc, lp, pb = best
    LZMA_CHOICE[:] = [f"lc={lc} lp={lp} pb={pb} dict={dic}"]
    return bytes([(pb * 5 + lp) * 9 + lc]) + dic.to_bytes(4, "little") + out


LZMA_CHOICE = []


def _unpack(block, mode, usize):
    if mode == 0:
        return block
    if mode == 1:
        return _lzma_dec(block, usize)
    if mode in (2, 3):
        return _lz4_dec(block, usize)
    raise ValueError(f"неизвестное сжатие {mode}")


def parse_unityfs(dec):
    if dec[:7] != b"UnityFS":
        raise ValueError("нет сигнатуры UnityFS")
    p = 8
    ver = struct.unpack(">I", dec[p:p + 4])[0]
    p += 4
    s1, p = _cstr(dec, p)
    s2, p = _cstr(dec, p)
    size, csz, usz, fl = struct.unpack(">qIII", dec[p:p + 20])
    p += 20
    if fl & 0x80:
        raise ValueError("block info в конце файла: не поддерживается")
    bi_pos = _a(p, 16) if ver >= 7 else p
    bi_c = dec[bi_pos:bi_pos + csz]
    bi = _unpack(bi_c, fl & 0x3F, usz)
    q = 16
    n = struct.unpack(">i", bi[q:q + 4])[0]
    q += 4
    blocks = []
    for _ in range(n):
        blocks.append(struct.unpack(">IIH", bi[q:q + 10]))
        q += 10
    m = struct.unpack(">i", bi[q:q + 4])[0]
    q += 4
    nodes = []
    for _ in range(m):
        off, sz, nf = struct.unpack(">qqI", bi[q:q + 20])
        q += 20
        nm, q = _cstr(bi, q)
        nodes.append((off, sz, nf, nm))
    data_pos = _a(bi_pos + csz, 16) if ver >= 7 else bi_pos + csz
    pos = data_pos
    cab = bytearray()
    for u, c, f in blocks:
        cab += _unpack(dec[pos:pos + c], f & 0x3F, u)
        pos += c
    P = dict(ver=ver, s1=s1, s2=s2, fl=fl, hash=bi[:16], blocks=blocks, nodes=nodes,
             first_block=dec[data_pos:data_pos + blocks[0][1]], data_end=pos)
    return P, bytes(cab)


def _blockinfo(P, ucab, comp):
    bf = P["blocks"][0][2]
    bi = P["hash"] + struct.pack(">i", 1) + struct.pack(">IIH", ucab, comp, bf) + struct.pack(">i", len(P["nodes"]))
    for off, sz, nf, nm in P["nodes"]:
        bi += struct.pack(">qqI", off, ucab if off == 0 else sz, nf) + nm + b"\0"
    return bi


def build_unityfs(P, cab, limit=None):
    """Собирает UnityFS из CAB: один блок того же типа сжатия, что и первый блок оригинала."""
    if len(P["nodes"]) != 1:
        raise ValueError("поддерживается один узел CAB")
    bf = P["blocks"][0][2]
    mode = bf & 0x3F
    if mode == 1:
        block = _lzma_enc(cab, P["first_block"][:5], None if limit is None else limit - 400)
    elif mode in (2, 3):
        block = _lz4_hc(cab)
    else:
        block = cab
    if _unpack(block, mode, len(cab)) != cab:
        raise ValueError("самопроверка сжатия не прошла")
    bi = _blockinfo(P, len(cab), len(block))
    bi_c = _lz4_hc(bi) if (P["fl"] & 0x3F) in (2, 3) else bi
    hdr = b"UnityFS\0" + struct.pack(">I", P["ver"]) + P["s1"] + b"\0" + P["s2"] + b"\0"
    pos = _a(len(hdr) + 20, 16)
    data_pos = _a(pos + len(bi_c), 16)
    end = data_pos + len(block)
    out = bytearray(hdr + struct.pack(">qIII", end, len(bi_c), len(bi), P["fl"]))
    out += b"\0" * (pos - len(out)) + bi_c
    out += b"\0" * (data_pos - len(out)) + block
    return bytes(out)


# ---------------------------------------------------------------- SerializedFile
def replace_objects(cab, repl):
    """
    repl: {path_id: (byte_start_как_в_UnityPy, старый_размер, новые_байты)}.
    Возвращает новый CAB. Поддерживается формат SerializedFile v22 (Unity 6).
    """
    cab = bytearray(cab)
    ver = struct.unpack(">I", cab[8:12])[0]
    if ver < 22:
        raise ValueError(f"SerializedFile версии {ver}: поддерживается только >= 22")
    fmt = "<" if cab[16] == 0 else ">"
    file_size = struct.unpack(">q", cab[24:32])[0]
    data_off = struct.unpack(">q", cab[32:40])[0]
    if file_size != len(cab):
        raise ValueError(f"file_size в заголовке ({file_size}) != длина CAB ({len(cab)})")
    for pid, (bstart, osize, new) in repl.items():
        entry = None
        for rel in (bstart - data_off, bstart):
            pat = struct.pack(fmt + "qqI", pid, rel, osize)
            if cab.count(pat, 0, data_off) == 1:
                entry = (cab.index(pat, 0, data_off), rel)
                break
        if entry is None:
            raise ValueError(f"запись объекта {pid} в таблице не найдена однозначно")
        at, rel = entry
        if len(new) <= osize:
            start = rel
            cab[data_off + start:data_off + start + osize] = new + b"\0" * (osize - len(new))
        else:
            cab[data_off + rel:data_off + rel + osize] = b"\0" * osize   # старые данные больше не нужны (нули почти не занимают места)
            start = _a(len(cab) - data_off, 8)
            cab += b"\0" * (data_off + start - len(cab)) + new
        cab[at + 8:at + 16] = struct.pack(fmt + "q", start)
        cab[at + 16:at + 20] = struct.pack(fmt + "I", len(new))
    cab[24:32] = struct.pack(">q", len(cab))
    return bytes(cab)


def textasset_raw(raw, new_script):
    """Сырые данные TextAsset с заменой m_Script (bytes). Формат: [len][name][align4][len][script][align4][хвост]."""
    n = struct.unpack("<I", raw[:4])[0]
    p = _a(4 + n, 4)
    s = struct.unpack("<I", raw[p:p + 4])[0]
    end = _a(p + 4 + s, 4)
    return raw[:p] + struct.pack("<I", len(new_script)) + new_script + b"\0" * (-len(new_script) % 4) + raw[end:]


def textasset_script(raw):
    n = struct.unpack("<I", raw[:4])[0]
    p = _a(4 + n, 4)
    s = struct.unpack("<I", raw[p:p + 4])[0]
    return raw[p + 4:p + 4 + s]


# ---------------------------------------------------------------- верхний уровень
def patch_scripts(path, translations, keymaps, name_key, mouse3, cat3, fit=True, forge=True, pad=0, size=None):
    """
    Подменяет сценарии. translations: {ключ_имени: Path}. mouse3/cat3 — расшифровка/шифрование текста сценария.
    Возвращает (зашифрованные_байты_размера_оригинала | None, [имена]).
    """
    import UnityPy
    path = Path(path)
    raw = path.read_bytes()
    target, crc = len(raw), zlib.crc32(raw) & 0xFFFFFFFF
    key = keymaps.key_for(path.name)
    dec = mouse_game4(raw, key)
    P, cab = parse_unityfs(dec)
    env = UnityPy.load(io.BytesIO(dec))
    repl, done, info = {}, [], []
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        d = obj.read()
        name = getattr(d, "m_Name", None) or getattr(d, "name", "")
        tr = translations.get(name_key(name))
        if not tr:
            continue
        old = bytes(obj.get_raw_data())
        old_script = textasset_script(old)
        if textasset_raw(old, old_script) != old:
            raise ValueError(f"{name}: не удалось разобрать TextAsset")
        fmt0 = script_format(old_script.decode("ascii").strip())
        original = mouse3(old_script.decode("ascii").strip())
        eol = "\r\n" if "\r\n" in original else "\n"
        text = tr.read_text(encoding="utf-8-sig").replace("\r\n", "\n").replace("\n", eol)
        enc = cat3(text, method="best").encode("ascii")
        info.append(f"{name}: было {len(old_script)} Б ({fmt0}), стало {len(enc)} Б ({script_format(enc.decode())})")
        repl[obj.path_id] = (obj.byte_start, len(old), textasset_raw(old, enc))
        done.append(name)
    if not done:
        return None, []
    new_cab = replace_objects(cab, repl)
    if not fit:
        # размер не важен: игре будут даны новые размер и CRC32 через локальный список ресурсов
        bundle = build_unityfs(P, new_cab)
        LAST_FIT.clear()
        LAST_FIT.extend(info)
        LAST_FIT.append(f"бандл {len(bundle)} байт (без подгонки под размер оригинала {target})")
        return cat_game4(bundle, key), done
    # Игра проверяет только CRC32 файла, размер ей не важен (проверено на эмуляторе: от -1 КБ до +300 КБ).
    # Поэтому файл подгоняется под CRC оригинала, а размер получается естественным (или заданным через size).
    if forge:
        tsize = (int(size) + 31) // 32 * 32 if size else None
        lim = (tsize - 96) if tsize else target - 96     # lim лишь сообщает LZMA, когда можно прекращать перебор
    else:
        tsize, lim = None, target
    bundle = build_unityfs(P, new_cab, limit=lim)
    LAST_FIT.clear()
    LAST_FIT.extend(info)
    if forge and tsize is None:
        tsize = (len(bundle) + 96 + 31) // 32 * 32      # 3 служебных блока для подгонки CRC + выравнивание
    LAST_FIT.append(f"бандл {len(bundle)} байт, файл {tsize or '-'} (оригинал {target}), LZMA {LZMA_CHOICE}")
    if forge and len(bundle) > tsize - 96:
        raise ValueError(f"бандл не помещается: {len(bundle)} > {tsize - 96}")
    if not forge:
        # без подделки CRC: обычный зашифрованный файл; pad добавляет безвредный хвост за концом бандла
        tail = bytes((i * 131 + 7) & 255 for i in range(pad)) if pad else b""
        return cat_game4(bundle + tail, key), done
    return cat_game4_forged(bundle, key, tsize, crc, patch_size=False), done
