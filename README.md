# FGO Scene Translator

**English** · [Русский](README.ru.md)

An unofficial tool for installing translated story scenes into Fate/Grand Order (JP) on Android **without repacking the APK** and without logging in again. Any target language works as long as the game's font supports it (the translations and examples in this project happen to be Russian). The app interface is available in English and Russian.

> This project is not affiliated with Aniplex, Sony Music Entertainment (Japan), Lasengle or TYPE-MOON. The repository contains no game files, no encryption keys and no third-party software: you have to obtain those yourself, with the game installed. Use at your own risk: modifying game files may violate the terms of service.

## How it works

A PC server (Python) decrypts the story files, inserts your translation and adjusts the CRC32 checksum to match the original. The phone app (.NET MAUI) uses [Shizuku](https://shizuku.rikka.app/) to read and write files in the game's `Android/data` folder and talks to the server over Wi-Fi.

```
game → "Send chapters to PC" → PC: decryption → you translate scenes → PC: build → "Install translation" → game
```

Chapter files are patched on the fly. The translation survives game updates: after an update, press "Install translation" again. "Check for updates" tells you when a chapter changed or a new translation is available.

## Repository layout

| Path | Purpose |
| --- | --- |
| `server.py`, `core/` | Server: decryption, translation build, CRC32 fix-up, line wrapping |
| `scene_tool.py` | Working files for translators (JA/target text), rebuild with tag validation |
| `export_scripts.py` | Decrypts all chapters sent from the phone into `scripts_ja/` |
| `build_pack.py`, `install_pack.py` | Batch install over adb (emulator) |
| `FGOPatcher.Maui.csproj`, `MainPage*`, `L.cs`, `PhoneInstaller.cs`, `ShizukuShell.cs`, `Platforms/`, `Resources/`, `libs/` | Phone app |

## What you need to provide yourself

- Python 3.11: `python -m pip install flask requests UnityPy lz4`
- .NET 8 SDK with the `maui-android` workload, Android platform-tools (`adb`)
- Shizuku on the phone (Android 11+)
- The FGO bundle encryption keys (`assetbundleKey.json`, `AssetListWithExtraKeyType.json`) in `external_decryptor/`. See the top of `server.py` (`KEYS_JSON`, `EXTRA_JSON`) for the expected paths and structure. The keys are not part of this repository.
- Shizuku libraries (`libs/shizuku-*.jar`) from Maven (version 13.1.5), see `FGOPatcher.Maui.csproj`.

## Quick start

```powershell
chcp 65001; $env:PYTHONUTF8 = 1
python scene_tool.py selftest

$env:FGO_HOST = "0.0.0.0"
$env:FGO_ADB_SERIAL = "<phone serial from adb devices>"
python server.py

dotnet build -t:Run -f net8.0-android -c Debug -p:AdbTarget="-s <serial>"
```

Typical workflow in the app:

1. **Check server** and **Check Shizuku**.
2. Open a chapter in the game so it downloads, then press **Send chapters to PC**. The server stores and decrypts it.
3. Translate the scenes (see below).
4. Press **Install translation**.
5. After game updates or new translations, press **Check for updates**, then **Install translation** again.
6. **Restore originals** puts the original files back.

The collapsed "Emulator mode" section contains the same install/restore actions performed by the PC over adb. It is meant for emulators only.

## Environment variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `FGO_HOST` | `127.0.0.1` | Address the server listens on (`0.0.0.0` for Wi-Fi) |
| `FGO_ADB_SERIAL` | `127.0.0.1:16384` | Device for adb mode |
| `FGO_TRANSLATIONS` | `translations/scenarios` | Folder with translations |
| `FGO_DATA_DIR` | next to the scripts | Data folder: `collected/` (files from the phone), `scripts_ja/` (decrypted scenes), `work/` (translation working files) |
| `FGO_WRAP`, `FGO_WRAP_WIDTH`, `FGO_WRAP_LINES` | `1`, `34`, `2` | Automatic line wrapping |

## Translation format

Working files `work/<scene>.txt` contain `JA:` / translation blocks without scene commands. Tags inside lines (`[r]`, `[%1]`, `[&a:b]`) must be carried over into the translated text; `scene_tool.py build` verifies that they match.

Line wrapping is tuned for Latin/Cyrillic text (default width 34 characters, 2 lines per window). Adjust `FGO_WRAP_WIDTH` and `FGO_WRAP_LINES` for other languages, and make sure the game's font contains the glyphs you need.

## License

The code is released under the MIT License, see [LICENSE](LICENSE). Shizuku libraries (`libs/shizuku-*.jar`) are © RikkaApps, Apache-2.0. Game texts and resources are not part of this repository and belong to their respective rights holders.
