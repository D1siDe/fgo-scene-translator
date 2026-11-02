# FGO RU Patcher

Неофициальный инструмент для установки русского перевода сюжетных сцен Fate/Grand Order (JP) на Android **без перепаковки APK** и без повторного входа в аккаунт.

> Проект не связан с Aniplex, Sony Music Entertainment (Japan), Lasengle и TYPE-MOON. В репозитории нет файлов игры, ключей шифрования и стороннего ПО: всё это нужно получить самостоятельно, имея установленную игру. Используйте на свой риск: изменение файлов игры может нарушать условия сервиса.

## Как это работает

Сервер на ПК (Python) расшифровывает файлы сюжета, подставляет перевод и подгоняет контрольную сумму CRC32 под оригинал. Приложение на телефоне (.NET MAUI) через [Shizuku](https://shizuku.rikka.app/) читает и записывает файлы в `Android/data` игры и общается с сервером по Wi-Fi.

```
игра → (кнопка 7) → ПК: расшифровка → вы переводите сцены → ПК: сборка перевода → (кнопка 5) → игра
```

Файлы глав подменяются на лету, перевод переживает обновления игры (после обновления достаточно нажать «5»).

## Состав

| Путь | Назначение |
| --- | --- |
| `server.py`, `core/` | Сервер: расшифровка, сборка перевода, подгонка CRC32, перенос строк |
| `scene_tool.py` | Рабочие файлы для перевода (JA/RU), сборка обратно с проверкой тегов |
| `export_scripts.py` | Расшифровка всех переданных глав в `scripts_ja/` |
| `build_pack.py`, `install_pack.py` | Пакетная установка по adb (эмулятор) |
| `FGOPatcher.Maui.csproj`, `MainPage*`, `PhoneInstaller.cs`, `ShizukuShell.cs`, `Platforms/`, `Resources/`, `libs/` | Приложение для телефона |

## Что нужно подготовить самостоятельно

- Python 3.11: `python -m pip install flask requests UnityPy lz4`
- .NET 8 SDK с нагрузкой `maui-android`, Android platform-tools (`adb`)
- Shizuku на телефоне (Android 11+)
- Ключи шифрования бандлов FGO (`assetbundleKey.json`, `AssetListWithExtraKeyType.json`) в папке `external_decryptor/`. Путь и структуру см. в начале `server.py` (`KEYS_JSON`, `EXTRA_JSON`). Ключи в репозиторий не входят.
- Библиотеки Shizuku (`libs/shizuku-*.jar`) из Maven (версия 13.1.5), см. `FGOPatcher.Maui.csproj`.

## Быстрый старт

```powershell
chcp 65001; $env:PYTHONUTF8 = 1
python scene_tool.py selftest

$env:FGO_HOST = "0.0.0.0"
$env:FGO_ADB_SERIAL = "<серийник телефона из adb devices>"
python server.py

dotnet build -t:Run -f net8.0-android -c Debug -p:AdbTarget="-s <серийник>"
```


## Переменные окружения

| Переменная | По умолчанию | Назначение |
| --- | --- | --- |
| `FGO_HOST` | `127.0.0.1` | Адрес, на котором слушает сервер (`0.0.0.0` для Wi-Fi) |
| `FGO_ADB_SERIAL` | `127.0.0.1:16384` | Устройство для режима adb |
| `FGO_TRANSLATIONS` | `translations/scenarios` | Папка с переводами |
| `FGO_DATA_DIR` | рядом со скриптами | Папка данных: `collected/` (файлы с телефона), `scripts_ja/` (расшифрованные сцены), `work/` (рабочие файлы перевода) |
| `FGO_WRAP`, `FGO_WRAP_WIDTH`, `FGO_WRAP_LINES` | `1`, `34`, `2` | Автоматический перенос строк |

## Формат перевода

Рабочие файлы `work/<сцена>.txt` содержат блоки `JA:`/`RU:` без команд сцены; теги внутри реплик (`[r]`, `[%1]`, `[&а:б]`) переносятся в русский текст, `scene_tool.py build` проверяет их совпадение.

## Лицензия

Код распространяется по лицензии MIT, см. [LICENSE](LICENSE). Библиотеки Shizuku (`libs/shizuku-*.jar`) © RikkaApps, лицензия Apache-2.0. Тексты и ресурсы игры в репозиторий не входят и принадлежат правообладателям.


