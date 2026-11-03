# FGO Scene Translator

**English** · [Русский](README.ru.md)

An unofficial tool for installing translated story scenes into Fate/Grand Order (JP) on Android **without repacking the APK** and without logging in again. Any target language works as long as the game's font supports it (the translations and examples in this project happen to be Russian). The app interface is available in English and Russian.

> This project is not affiliated with Aniplex, Sony Music Entertainment (Japan), Lasengle or TYPE-MOON. The repository contains no game files, no encryption keys and no third-party software: you have to obtain those yourself, with the game installed. Use at your own risk: modifying game files may violate the terms of service.

## How it works

A PC server (Python) decrypts the story files, inserts your translation and adjusts the CRC32 checksum to match the original. The phone app uses [Shizuku](https://shizuku.rikka.app/) to read and write files in the game's `Android/data` folder and talks to the server over Wi-Fi or USB.

```
game → "Send chapters to PC" → PC: decrypted scenes → you translate → "Install translation" → game
```

Chapter files are patched on the fly. The translation survives game updates: after an update, press "Install translation" again. "Check for updates" tells you when a chapter changed or a new translation is available.

## Quick start (ready-made APK)

**On the phone**

1. Download `FGO-Scene-Translator-*.apk` from [Releases](../../releases) (ARM64) and install it.
2. Install and start [Shizuku](https://shizuku.rikka.app/) (Android 11+, wireless debugging).
3. Open any chapter in the game so it downloads its script.

**On the PC** (Windows, PowerShell)

1. Install Python 3.11 and the dependencies, download this repository:

   ```powershell
   python -m pip install flask requests UnityPy lz4
   git clone https://github.com/D1siDe/fgo-scene-translator.git
   cd fgo-scene-translator
   ```

2. Put the FGO bundle encryption keys (`assetbundleKey.json`, `AssetListWithExtraKeyType.json`) into `external_decryptor/`. See the top of `server.py` (`KEYS_JSON`, `EXTRA_JSON`). The keys are not part of this repository.
3. Start the server (for Wi-Fi it must listen on all interfaces, and the firewall must allow port 5000):

   ```powershell
   chcp 65001; $env:PYTHONUTF8 = 1
   $env:FGO_HOST = "0.0.0.0"
   New-NetFirewallRule -DisplayName "FGO Scene Translator" -Direction Inbound -Protocol TCP -LocalPort 5000 -Action Allow   # once, as administrator
   python server.py
   ```

   Alternative over USB: leave `FGO_HOST` unset, run `adb reverse tcp:5000 tcp:5000` and use `http://127.0.0.1:5000` in the app.

**In the app**

1. Enter the server address (`http://<PC IP>:5000`) and press **Check server**, then **Check Shizuku**.
2. Press **Send chapters to PC**. The server stores and decrypts the chapters you have downloaded.
3. Translate the scenes (see below).
4. Press **Install translation**, then start the game.
5. After game updates or new translations: **Check for updates**, then **Install translation** again.
6. **Restore originals** puts the original files back.

The collapsed "Emulator mode" section contains the same install/restore actions performed by the PC over adb. It is meant for emulators only.

## Translation format

A translation is the **original scene file with the text replaced**. Nothing is merged from two files.

1. After **Send chapters to PC**, the decrypted scenes are in `scripts_ja/`, in a subfolder per chapter bundle (for example `scripts_ja/ScriptActionEncrypt_02/0200020010.txt`).
2. Copy the scene you want to translate into the translations folder (`translations/scenarios/`, configurable with `FGO_TRANSLATIONS`) and replace the Japanese text with your translation. Keep the file name.
3. Leave everything else as it is: scene commands, speaker lines (`＠name`) and the tags inside lines (`[r]`, `[%1]`, `[&a:b]`). Choice lines have the form `？N：text`; translate the text after the colon.
4. Line breaks are inserted automatically: the server wraps the text to fit the dialogue window (width 34, 2 lines by default).

The server picks up new or changed files, builds the chapter bundle on the fly and gives it to the phone. A scene without a translation file stays in Japanese.

`scene_tool.py` is an optional helper: it exports scenes into convenient working files, checks that no tags were lost and builds the translation file for you. You do not need it if you edit the scene files directly.

Wrapping is tuned for Latin/Cyrillic text. Adjust `FGO_WRAP_WIDTH` and `FGO_WRAP_LINES` for other languages, and make sure the game's font contains the glyphs you need.

## Environment variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `FGO_HOST` | `127.0.0.1` | Address the server listens on (`0.0.0.0` for Wi-Fi) |
| `FGO_ADB_SERIAL` | `127.0.0.1:16384` | Device for adb mode |
| `FGO_TRANSLATIONS` | `translations/scenarios` | Folder with translations |
| `FGO_DATA_DIR` | next to the scripts | Data folder: `collected/` (files from the phone), `scripts_ja/` (decrypted scenes), `work/` (working files of `scene_tool.py`) |
| `FGO_WRAP`, `FGO_WRAP_WIDTH`, `FGO_WRAP_LINES` | `1`, `34`, `2` | Automatic line wrapping |

## Repository layout

| Path | Purpose |
| --- | --- |
| `server.py`, `core/` | Server: decryption, translation build, CRC32 fix-up, line wrapping |
| `scene_tool.py` | Optional helper for translators: working files, tag validation |
| `export_scripts.py` | Decrypts all chapters sent from the phone into `scripts_ja/` |
| `build_pack.py`, `install_pack.py` | Batch install over adb (emulator) |
| `FGOPatcher.Maui.csproj`, `MainPage*`, `L.cs`, `PhoneInstaller.cs`, `ShizukuShell.cs`, `Platforms/`, `Resources/`, `libs/` | Phone app (.NET MAUI) |

## Building the app from source

Requires the .NET 8 SDK with the `maui-android` workload and the Shizuku libraries (`libs/shizuku-*.jar`, version 13.1.5 from Maven, see `FGOPatcher.Maui.csproj`).

```powershell
dotnet build -t:Run -f net8.0-android -c Debug -p:AdbTarget="-s <serial>"
```

## Credits

- [hexstr/FGOAssetsModifyTool](https://github.com/hexstr/FGOAssetsModifyTool): `core/fgo_crypto.py` is a Python port of the game's bundle/script cipher implementation from this project. The original repository does not state a license.
- [Rayshift](https://rayshift.io/) and its translation tooling for FGO: inspiration for installing translations without repacking the game.
- [Shizuku](https://github.com/RikkaApps/Shizuku-API) by RikkaApps: privileged file access on Android (Apache-2.0).

## License

The code is released under the MIT License, see [LICENSE](LICENSE). Shizuku libraries (`libs/shizuku-*.jar`) are © RikkaApps, Apache-2.0. Game texts and resources are not part of this repository and belong to their respective rights holders.
