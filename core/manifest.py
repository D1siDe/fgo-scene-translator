# -*- coding: utf-8 -*-
"""
Правка локального списка ресурсов игры (files/data/d713/<hash>, «AssetStorage»).

Формат (после расшифровки MouseGame3/8):
    ~<CRC32 всего текста после первой строки, десятично>
    @FgoDataVersion...
    <флаг>,<группа>,<размер>,<crc32>,<имя ресурса>
Файл на устройстве: base64-текст (ASCII) -> Rijndael (stage keys) -> gzip/bz2 -> инверсия байт.
Если подставить в строку ресурса размер и CRC32 нового файла и пересчитать первую строку, игра примет файл любого размера.
"""
import zlib
from .fgo_crypto import mouse_game3, cat_game3, script_format


class Manifest:
    def __init__(self, raw: bytes):
        self.bom = raw[:3] == b"\xef\xbb\xbf"
        self.fmt = script_format(raw.decode("utf-8-sig").strip())          # 'gzip' | 'bz2'
        text = mouse_game3(raw.decode("utf-8-sig").strip())
        self.crlf = "\r\n" in text
        self.lines = text.replace("\r\n", "\n").split("\n")
        # хвостовой пустой элемент (текст оканчивается переводом строки) сохраняем как есть

    @staticmethod
    def looks_like_manifest(raw: bytes) -> bool:
        """Быстрая проверка по содержимому: после расшифровки первая строка '~число', вторая '@FgoDataVersion'."""
        try:
            t = mouse_game3(raw.decode("utf-8-sig").strip())
        except Exception:
            return False
        a = t.split("\n", 2)
        return len(a) > 1 and a[0].startswith("~") and a[1].lstrip("\r").startswith("@FgoDataVersion")

    def find(self, name):
        """Индекс строки ресурса по точному имени (последнее поле)."""
        for i, l in enumerate(self.lines):
            p = l.split(",", 4)
            if len(p) == 5 and p[4].rstrip("\r") == name:
                return i
        return None

    def get(self, name):
        i = self.find(name)
        if i is None:
            return None
        p = self.lines[i].split(",", 4)
        return int(p[2]), int(p[3])

    def set(self, name, size, crc):
        i = self.find(name)
        if i is None:
            raise KeyError(name)
        p = self.lines[i].split(",", 4)
        p[2], p[3] = str(int(size)), str(int(crc) & 0xFFFFFFFF)
        self.lines[i] = ",".join(p)

    def header_ok(self):
        body = "\n".join(self.lines[1:])
        return self.lines[0] == f"~{zlib.crc32(body.encode('utf-8')) & 0xFFFFFFFF}"

    def text(self):
        body = "\n".join(self.lines[1:])
        head = f"~{zlib.crc32(body.encode('utf-8')) & 0xFFFFFFFF}"
        t = head + "\n" + body
        return t.replace("\n", "\r\n") if self.crlf else t

    def dump(self, method=None):
        """Зашифрованные байты (base64 ASCII), тем же сжатием, что у исходного файла, если method не задан."""
        data = cat_game3(self.text(), method=method or self.fmt).encode("ascii")
        return (b"\xef\xbb\xbf" + data) if self.bom else data
