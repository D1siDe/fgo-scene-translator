# -*- coding: utf-8 -*-
"""
Установка пропатченных бандлов прямо на устройство/эмулятор через adb.

Схема для каждого бандла сценариев:
  1. найти файл на устройстве (…/files/data/<папка>/<имя>.bin) и скачать его;
  2. если на устройстве уже наша сборка (md5 совпал с записанным в state.json) — строить из резервной копии
     оригинала (fgo_backups/), иначе текущий файл считается оригиналом и становится резервной копией;
  3. собрать перевод (размер и CRC32 — как у оригинала, поэтому игра принимает файл);
  4. остановить игру, положить файл на устройство, сверить md5.
Откат: restore_all() кладёт резервные копии обратно.
"""
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import tempfile
import zlib
from pathlib import Path

from .manifest import Manifest

GAME_PKG = "com.aniplex.fategrandorder"
DEVICE_DATA = f"/storage/emulated/0/Android/data/{GAME_PKG}/files/data"
LIST_NAME = "cfb1d36393fd67385e046b084b7cf7ed"     # локальный список ресурсов игры (его же правит Rayshift)
SCRIPT_PREFIX = "ScriptActionEncrypt/"
_LS_RE = re.compile(r"^\S+\s+\d+\s+\S+\s+\S+\s+(\d+)\s+\d{4}-\d\d-\d\d\s+\d\d:\d\d\s+(.+?)\s*$")


class DeviceError(Exception):
    pass


def _find_adb():
    cands = [os.environ.get("FGO_ADB"), shutil.which("adb"), r"C:\platform-tools\adb.exe",
             r"C:\Program Files\Netease\MuMuPlayer\nx_device\15.0\shell\adb.exe",
             r"C:\Program Files\Netease\MuMuPlayer-12.0\shell\adb.exe"]
    for c in cands:
        if c and Path(c).exists():
            return str(c)
    return None


def _md5(path_or_bytes):
    h = hashlib.md5()
    if isinstance(path_or_bytes, (bytes, bytearray)):
        h.update(path_or_bytes)
    else:
        with open(path_or_bytes, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
    return h.hexdigest()


class Device:
    def __init__(self, backup_dir, serial=None, adb=None):
        self.adb_path = adb or _find_adb()
        self.serial = serial or os.environ.get("FGO_ADB_SERIAL", "127.0.0.1:16384")
        self.backup_dir = Path(backup_dir)
        self.backup_dir.mkdir(parents=True, exist_ok=True)
        self.state_file = self.backup_dir / "state.json"

    # ---- adb ----
    def adb(self, *args, timeout=180):
        if not self.adb_path:
            raise DeviceError("adb не найден (задайте переменную FGO_ADB или положите adb в PATH)")
        r = subprocess.run([self.adb_path, "-s", self.serial, *args], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=timeout)
        return r.returncode, (r.stdout or "") + (r.stderr or "")

    def ready(self):
        """True, если adb есть и устройство отвечает (до 3 попыток; причина последней неудачи в self.last_error)."""
        import time
        self.last_error = ""
        if not self.adb_path:
            self.last_error = "adb не найден"
            return False
        for attempt in range(3):
            try:
                if ":" in self.serial:                       # сетевое устройство (эмулятор, Wi-Fi отладка)
                    subprocess.run([self.adb_path, "connect", self.serial], capture_output=True, timeout=15)
                rc, out = self.adb("get-state", timeout=15)
                if rc == 0 and "device" in out and "offline" not in out:
                    return True
                self.last_error = out.strip() or f"код {rc}"
            except Exception as e:
                self.last_error = f"{type(e).__name__}: {e}"
            time.sleep(1)
        return False

    def device_path(self, name):
        rc, out = self.adb("shell", f"ls {DEVICE_DATA}/*/{name}")
        for line in out.splitlines():
            line = line.strip()
            if line.startswith("/") and line.endswith(name):
                return line
        raise DeviceError(f"{name}: файла нет на устройстве (игра ещё не скачала этот ресурс?)")


    # ---- актуальное состояние игры (без AssetStorage_dec.txt) ----
    def data_dir(self):
        """Папка с данными игры (там, где лежит локальный список), например .../files/data/d713."""
        rc, out = self.adb("shell", f"ls {DEVICE_DATA}/*/{LIST_NAME}")
        for line in out.splitlines():
            line = line.strip()
            if line.startswith("/") and line.endswith(LIST_NAME):
                return line[: -len(LIST_NAME) - 1]
        raise DeviceError("локальный список игры не найден на устройстве (запустите игру хотя бы раз)")

    def read_list(self):
        """Локальный список ресурсов игры -> (Manifest, версия данных)."""
        tmp = Path(tempfile.mkdtemp(prefix="fgo_"))
        try:
            dst = tmp / LIST_NAME
            rc, out = self.adb("pull", f"{self.data_dir()}/{LIST_NAME}", str(dst))
            if rc != 0 or not dst.exists():
                raise DeviceError(f"не удалось скачать список ресурсов: {out.strip()}")
            m = Manifest(dst.read_bytes())
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        ver = m.lines[1].split(",")[-1].strip() if len(m.lines) > 1 else "?"
        return m, ver

    def script_entries(self, manifest):
        """{asset: {'flag','size','crc'}} для ScriptActionEncrypt/* из списка."""
        res = {}
        for line in manifest.lines:
            p = line.split(",", 4)
            if len(p) == 5 and p[4].rstrip("\r").startswith(SCRIPT_PREFIX) and p[2].isdigit() and p[3].isdigit():
                res[p[4].rstrip("\r")] = {"flag": p[0], "size": int(p[2]), "crc": int(p[3])}
        return res

    def listing(self):
        """{имя_файла: размер} для папки данных игры."""
        rc, out = self.adb("shell", f"ls -l {self.data_dir()}/", timeout=120)
        files = {}
        for line in out.splitlines():
            m = _LS_RE.match(line.strip())
            if m:
                files[m.group(2)] = int(m.group(1))
        return files

    def remote_crcs(self, names):
        """{имя: crc32} — считает на устройстве (toybox crc32), недостающее докачивает и считает здесь."""
        base, res = self.data_dir(), {}
        for i in range(0, len(names), 40):
            chunk = names[i:i + 40]
            rc, out = self.adb("shell", "crc32 " + " ".join(shlex.quote(f"{base}/{n}") for n in chunk), timeout=900)
            for line in out.splitlines():
                m = re.match(r"^([0-9a-fA-F]{8})\s+(.+?)\s*$", line.strip())
                if m:
                    res[m.group(2).rsplit("/", 1)[-1]] = int(m.group(1), 16)
        for n in names:
            if n not in res:
                tmp = Path(tempfile.mkdtemp(prefix="fgo_"))
                try:
                    rc, _ = self.adb("pull", f"{base}/{n}", str(tmp / n))
                    if rc == 0 and (tmp / n).exists():
                        res[n] = zlib.crc32((tmp / n).read_bytes()) & 0xFFFFFFFF
                finally:
                    shutil.rmtree(tmp, ignore_errors=True)
        return res

    def _map(self):
        try:
            return json.loads((self.backup_dir / "map.json").read_text(encoding="utf-8"))
        except Exception:
            return {}

    def find_file(self, asset, size, crc, files=None, log=print):
        """Имя файла на устройстве для ресурса asset: файл, CRC32 которого равен записи списка
        (размер у нашей сборки другой, поэтому сначала проверяется запомненное имя, затем файлы нужного размера,
        затем - как крайняя мера - все крупные файлы)."""
        files = files if files is not None else self.listing()
        mp = self._map()
        order = []
        if mp.get(asset) in files:
            order.append(mp[asset])
        order += [n for n, sz in files.items() if sz == size and n not in order]
        for stage in (order, None):
            if stage is None:
                tried = set(order)
                stage = [n for n, sz in files.items() if n not in tried and 50_000 <= sz <= 40_000_000]
                if stage:
                    log(f"[ИНФО] {asset}: ищу файл по CRC32 среди {len(stage)} файлов (один раз)")
            if not stage:
                continue
            got = self.remote_crcs(stage)
            for n in stage:
                if got.get(n) == crc:
                    mp[asset] = n
                    (self.backup_dir / "map.json").write_text(json.dumps(mp, indent=1), encoding="utf-8")
                    return n
        return None

    def scan_scripts(self, log=print):
        """Что сейчас у игры: версия данных, главы сценариев (файл, размер, CRC32 из списка) и проблемы."""
        m, ver = self.read_list()
        files = self.listing()
        found, problems = {}, []
        for asset, e in self.script_entries(m).items():
            if e["flag"] != "1":
                continue                                   # глава ещё не скачана игрой - патчить нечего
            name = self.find_file(asset, e["size"], e["crc"], files, log)
            if name:
                found[asset] = {"file": name, "size": e["size"], "crc": e["crc"], "device_size": files.get(name)}
            else:
                problems.append(f"{asset}: нет файла с CRC32 из списка игры (игра ещё докачивает обновление?)")
        return {"version": ver, "bundles": found, "problems": problems}

    def original_path(self, name, entry):
        """Путь к ОРИГИНАЛУ файла (локальная копия в fgo_backups). Оригиналом считается файл с размером и CRC32
        из списка игры; бэкап, не совпадающий со списком (устарел после обновления или испорчен), заменяется."""
        backup = self.backup_dir / name
        if backup.exists() and self._is_original(backup, entry):
            return backup
        dpath = self.device_path(name)
        st = self._state()
        tmp = Path(tempfile.mkdtemp(prefix="fgo_"))
        try:
            cur = tmp / name
            rc, out = self.adb("pull", dpath, str(cur))
            if rc != 0 or not cur.exists():
                raise DeviceError(f"{name}: adb pull не удался: {out.strip()}")
            if self._is_original(cur, entry) and _md5(cur) != st.get(name, {}).get("pushed_md5"):
                shutil.copyfile(cur, backup)
                return backup
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        raise DeviceError(
            f"{name}: на устройстве изменённый файл, а оригинала (размер {entry['size']}, CRC32 {entry['crc']}) нет в "
            f"fgo_backups. Нажмите «Вернуть оригиналы» либо удалите файл на устройстве - игра скачает его заново.")

    @staticmethod
    def _is_original(path, entry):
        p = Path(path)
        return p.stat().st_size == entry["size"] and (zlib.crc32(p.read_bytes()) & 0xFFFFFFFF) == entry["crc"]

    # ---- состояние ----
    def _state(self):
        try:
            return json.loads(self.state_file.read_text(encoding="utf-8"))
        except Exception:
            return {}

    def _save(self, st):
        self.state_file.write_text(json.dumps(st, indent=1), encoding="utf-8")

    # ---- основное ----
    def install(self, name, build, out_path, entry=None):
        """
        build(base_path: Path) -> (bytes|None, [имена])  — собирает пропатченный файл из base_path.
        Результат записывается в out_path и устанавливается на устройство.
        Возвращает (список_имён, md5).
        """
        dpath = self.device_path(name)
        st = self._state()
        tmp = Path(tempfile.mkdtemp(prefix="fgo_"))
        try:
            if entry is not None:
                backup = self.original_path(name, entry)   # оригинал проверен по размеру и CRC32 из списка игры
            else:
                cur = tmp / name
                rc, out = self.adb("pull", dpath, str(cur))
                if rc != 0 or not cur.exists():
                    raise DeviceError(f"{name}: adb pull не удался: {out.strip()}")
                cur_md5 = _md5(cur)
                backup = self.backup_dir / name
                if not (backup.exists() and st.get(name, {}).get("pushed_md5") == cur_md5):
                    shutil.copyfile(cur, backup)       # без списка: текущий файл считается оригиналом
            data, done = build(backup)
            if not data:
                raise DeviceError(f"{name}: нечего патчить")
            out_path = Path(out_path)
            out_path.parent.mkdir(parents=True, exist_ok=True)
            out_path.write_bytes(data)
            self.adb("shell", f"am force-stop {GAME_PKG}")
            rc, out = self.adb("push", str(out_path), dpath)
            if rc != 0:
                raise DeviceError(f"{name}: adb push не удался: {out.strip()}")
            want = _md5(data)
            rc, out = self.adb("shell", f"md5sum {dpath}")
            got = out.split()[0].lower() if out.split() else ""
            if got != want:
                raise DeviceError(f"{name}: после push хэш не совпал ({got} != {want})")
            st[name] = {"pushed_md5": want, "scripts": done}
            self._save(st)
            return done, want
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def restore_all(self):
        """Возвращает оригиналы. Возвращает список имён.
        Файл трогается только если на устройстве ещё НАША сборка (md5 как в state.json): если игра после обновления
        уже заменила его свежим оригиналом, старая резервная копия поверх него не кладётся.
        Нет резервной копии - файл удаляется, игра скачает оригинал заново."""
        st = self._state()
        restored = []
        for name in list(st):
            backup = self.backup_dir / name
            try:
                dpath = self.device_path(name)
            except DeviceError:
                st.pop(name)                      # файла на устройстве уже нет (игра сама удалила/переименовала)
                continue
            rc, out = self.adb("shell", f"md5sum {dpath}")
            cur = out.split()[0].lower() if rc == 0 and out.split() else ""
            if cur != st[name].get("pushed_md5"):
                st.pop(name)                      # не наша сборка: игра обновила файл, откатывать нечего
                continue
            self.adb("shell", f"am force-stop {GAME_PKG}")
            if backup.exists():
                rc, out = self.adb("push", str(backup), dpath)
                if rc != 0:
                    raise DeviceError(f"{name}: adb push не удался: {out.strip()}")
                rc, out = self.adb("shell", f"md5sum {dpath}")
                if not out.split() or out.split()[0].lower() != _md5(backup):
                    raise DeviceError(f"{name}: после отката хэш не совпал")
            else:
                self.adb("shell", f"rm {dpath}")  # оригинала нет: игра скачает файл заново при входе в сцену
            st.pop(name)
            restored.append(name)
        self._save(st)
        return restored
