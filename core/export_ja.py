# -*- coding: utf-8 -*-
"""Расшифровка одного бандла сценариев в отдельные японские тексты сцен (используется server.py и export_scripts.py)."""
import io
import shutil
import tempfile
from pathlib import Path

from . import script_patcher as sp
from .fgo_crypto import mouse_game3


def export_bundle(src, device_file, asset, out_root, keymaps, translated=()):
    """Расшифровывает бандл src (ключ определяется по имени файла на устройстве) в out_root/<asset>/<сцена>.txt.
    Возвращает (сцен, из них переведено)."""
    import UnityPy
    tmp = Path(tempfile.mkdtemp(prefix="fgo_"))
    try:
        p = tmp / device_file
        shutil.copyfile(src, p)
        env = UnityPy.load(io.BytesIO(sp._decrypt_bundle(p, keymaps)))
        folder = Path(out_root) / asset.replace("/", "_")
        folder.mkdir(parents=True, exist_ok=True)
        n = t = 0
        for d, name in sp._script_objects(env):
            try:
                text = mouse_game3(sp._script_text(d))
            except Exception:
                continue
            (folder / (str(name).replace("/", "_") + ".txt")).write_bytes(text.encode("utf-8"))
            n += 1
            t += sp._name_key(name) in translated
        return n, t
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
