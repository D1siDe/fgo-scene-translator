using System;
using System.Globalization;
using Microsoft.Maui.Storage;

namespace FGOPatcher.Maui;

/// <summary>Язык интерфейса (RU/EN). T(ru, en) - подпись интерфейса; Loc(текст) - перевод строк журнала по таблице фраз.</summary>
public static class L
{
    public static bool En { get; private set; }

    static L()
    {
        string v = "";
        try { v = Preferences.Get("lang", ""); } catch { }
        if (v == "")
            v = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        En = v == "en";
    }

    public static void Set(bool en)
    {
        En = en;
        try { Preferences.Set("lang", en ? "en" : "ru"); } catch { }
    }

    public static string T(string ru, string en) => En ? en : ru;

    /// <summary>Английская версия строки журнала: заменяет известные русские фразы (длинные первыми), числа и имена файлов остаются.</summary>
    public static string Loc(string s)
    {
        if (!En || string.IsNullOrEmpty(s)) return s;
        foreach (var (ru, en) in Table) s = s.Replace(ru, en);
        return s;
    }

    // Отсортировано по убыванию длины русской фразы (генерируется скриптом, править не нужно)
    private static readonly (string, string)[] Table =
    {
        (": файл не соответствует списку игры (игра обновляет или докачивает данные). Откройте сцену в игре, затем «Передать главы на ПК» и «Установить перевод».", ": the file does not match the game's list (the game is updating or still downloading data). Open a scene in the game, then press “Send chapters to PC” and “Install translation”."),
        ("[WARN] Shizuku не запущен (или приложение ещё не получило связь). Запустите приложение Shizuku, затем полностью закройте и откройте этот патчер.", "[WARN] Shizuku is not running (or the app has not connected yet). Start the Shizuku app, then fully close and reopen this app."),
        ("[ERROR] Пака нет: запустите server.py на ПК (adb reverse tcp:5000 tcp:5000) или положите translate_pack.zip в папку Download на телефоне.", "[ERROR] No pack: start server.py on the PC (adb reverse tcp:5000 tcp:5000) or put translate_pack.zip into the Download folder on the phone."),
        ("[WARN] Shizuku не запущен (или приложение не получило связь). Запустите Shizuku, затем полностью закройте и откройте патчер.", "[WARN] Shizuku is not running (or the app has not connected yet). Start Shizuku, then fully close and reopen this app."),
        (": на телефоне не оригинал и нет резервной копии. Удалите файл в игре (очистка кэша) или верните оригинал и повторите.", ": the phone file is not the original and there is no backup. Delete the file in the game (clear cache) or restore the original and retry."),
        ("[INFO] Нечего возвращать: на устройстве нет нашей сборки (либо перевод не ставился, либо игра уже обновила файлы).", "[INFO] Nothing to restore: the device has no patched files (the translation was not installed, or the game has already updated the files)."),
        ("[WARN] Эмулятор недоступен по adb: файлы лежат в fgo_local_mods на ПК, в игру ничего не поставлено.", "[WARN] The emulator is unreachable via adb: files are in fgo_local_mods on the PC, nothing was installed into the game."),
        (", но глава ещё не передавалась на ПК. Нажмите «Передать главы на ПК», затем «Установить перевод».", ", but the chapter has not been sent to the PC yet. Press “Send chapters to PC”, then “Install translation”."),
        ("[ERROR] Классы Shizuku не найдены в приложении (положите libs\\shizuku-*.jar и пересоберите).", "[ERROR] Shizuku classes not found in the app (put libs\\shizuku-*.jar and rebuild)."),
        (": на телефоне наша сборка, а оригинала этой версии нет. Нажмите «Вернуть оригиналы» или ", ": the phone has our patched file and no original of this version. Press “Restore originals” or "),
        ("удалите файл через очистку данных игры и откройте сцену, чтобы игра скачала его заново.", "delete the file by clearing the game's data and open a scene so the game downloads it again."),
        ("Версия главы другая: соберите пак на ПК под этот телефон (build_pack.py --serial ...).", "The chapter version is different: build a pack on the PC for this phone (build_pack.py --serial ...)."),
        (": файла нет на телефоне. Откройте любую сцену этой главы в игре, чтобы она скачалась.", ": the file is not on the phone. Open any scene of this chapter in the game so it gets downloaded."),
        (": сервер собирает перевод под эту версию главы (первый раз может занять минуту)...", ": the server is building the translation for this chapter version (the first time may take a minute)..."),
        ("[WARN] Разрешение не выдано. Нажмите кнопку ещё раз и подтвердите в окне Shizuku.", "[WARN] Permission not granted. Press the button again and confirm in the Shizuku dialog."),
        (": файла нет на телефоне (игра его не скачала). Откройте сцену этой главы в игре.", ": the file is not on the phone (the game has not downloaded it). Open a scene of this chapter in the game."),
        ("[HINT] Запустите server.py на ПК и выполните: adb reverse tcp:5000 tcp:5000", "[HINT] Start server.py on the PC and run: adb reverse tcp:5000 tcp:5000"),
        ("[HINT] Запущен ли server.py и выполнен ли: adb reverse tcp:5000 tcp:5000", "[HINT] Is server.py running, and did you run: adb reverse tcp:5000 tcp:5000"),
        ("[PATCH] Сервер собирает перевод и ставит его на устройство через adb ...", "[PATCH] The server is building the translation and installing it on the device via adb ..."),
        ("[SUCCESS] Перевод установлен. Игра была закрыта, запустите FGO заново.", "[SUCCESS] Translation installed. The game was closed, start FGO again."),
        ("Статус: сборка и установка перевода (может занять несколько минут)...", "Status: building and installing translation (may take several minutes)..."),
        ("[ОШИБКА] Список ресурсов игры не найден. Запустите игру хотя бы раз.", "[ERROR] Game resource list not found. Start the game at least once."),
        ("[INFO] Резервных копий нет: перевод с этого телефона не ставился.", "[INFO] No backups: no translation was installed from this phone."),
        ("[INIT] FGO Scene Translator готов. Сначала проверьте сервер.", "[INIT] FGO Scene Translator is ready. Check the server first."),
        ("  → запустите игру, дождитесь обновления данных и повторите", "  → start the game, wait for the data update and try again"),
        (" сцен). Нажмите «Установить перевод», чтобы переустановить.", " scenes). Press “Install translation” to reinstall."),
        (" КБ (ограничения по размеру нет, проверяется только CRC32)", " KB (no size limit, only CRC32 is checked)"),
        ("[WARN] Нечего ставить: проверьте переводы и ошибки выше.", "[WARN] Nothing to install: check the translations and the errors above."),
        ("[WARN] Для перевода нет бандла (проверьте имя файла): ", "[WARN] No bundle for this translation (check the file name): "),
        (". Глава не скачана целиком или заменена нашей сборкой.", ". The chapter is not fully downloaded or was replaced by our patched file."),
        ("[INFO] Разрешите доступ в окне Shizuku (30 секунд)...", "[INFO] Allow access in the Shizuku dialog (30 seconds)..."),
        (" сцен). Нажмите «Установить перевод», чтобы обновить.", " scenes). Press “Install translation” to update."),
        ("[INFO] Нет скачанных глав, для которых есть переводы.", "[INFO] No downloaded chapters that have translations."),
        ("[WARN] Разрешение не выдано. Нажмите кнопку ещё раз.", "[WARN] Permission not granted. Press the button again."),
        ("[INFO] Сервер на ПК отвечает (пак для версии данных ", "[INFO] The PC server is responding (pack for data version "),
        ("[WARN] Сервер на ПК недоступен или нет списка глав (", "[WARN] The PC server is unavailable or has no chapter list ("),
        ("[OK] Пак прочитан из Download/translate_pack.zip: ", "[OK] Pack read from Download/translate_pack.zip: "),
        (": игра заменила файл оригиналом, перевод слетел (", ": the game replaced the file with the original, the translation was lost ("),
        ("[ERROR] Классы Shizuku не найдены в приложении.", "[ERROR] Shizuku classes not found in the app."),
        ("[ОШИБКА] Не удалось прочитать список ресурсов.", "[ERROR] Could not read the resource list."),
        ("). Ищу пак в Download/translate_pack.zip ...", "). Looking for the pack in Download/translate_pack.zip ..."),
        (": оригинал сохранён в Download/FGO_RU_backup", ": original saved to Download/FGO_RU_backup"),
        ("НЕ доступен (перевод не будет установлен)", "NOT available (translation will not be installed)"),
        ("Статус: перевод собран, но НЕ установлен", "Status: translation built but NOT installed"),
        ("[ERROR] Сервер не ответил за 20 минут.", "[ERROR] The server did not respond within 20 minutes."),
        ("[OK] Классы Shizuku в приложении есть.", "[OK] Shizuku classes are present in the app."),
        ("[WARN] Не удалось собрать, пропущено: ", "[WARN] Could not build, skipped: "),
        (" КБ) сохранён в Download/FGO_RU_backup", " KB) saved to Download/FGO_RU_backup"),
        (": не удалось сохранить резервную копию", ": could not save a backup"),
        ("Статус: установка на этот телефон...", "Status: installing on this phone..."),
        ("[INFO] Пак собран для версии данных ", "[INFO] Pack built for data version "),
        (": на телефоне оригинал, переводов ", ": the phone has the original, translations: "),
        (": перевод установлен и актуален (", ": translation installed and up to date ("),
        ("Статус: превышено время ожидания", "Status: timed out"),
        ("[OK] Бандлов сценариев найдено: ", "[OK] Script bundles found: "),
        ("[WARN] Сервер на ПК недоступен (", "[WARN] The PC server is unavailable ("),
        ("[OK] Разрешение Shizuku выдано.", "[OK] Shizuku permission granted."),
        (". Нажмите «Установить перевод».", ". Press “Install translation”."),
        ("[OK] shell завершился с кодом ", "[OK] shell exited with code "),
        (" нет. Файлов того же размера: ", " not found. Files of the same size: "),
        (": для этой главы нет переводов", ": no translations for this chapter"),
        (": уже установлено, актуально (", ": already installed, up to date ("),
        ("Статус: возврат оригиналов...", "Status: restoring originals..."),
        ("Статус: оригиналы возвращены", "Status: originals restored"),
        (" • Файлов в raw_game_cache: ", " • Files in raw_game_cache: "),
        ("[OK] Переносы строк: ширина ", "[OK] Line wrapping: width "),
        (": нет файла в игре или копии", ": no file in the game or backup"),
        (": пак повреждён (md5/размер)", ": pack is corrupted (md5/size)"),
        ("[ОШИБКА] Сервер недоступен: ", "[ERROR] Server unavailable: "),
        ("Статус: готово (см. журнал)", "Status: done (see log)"),
        ("Статус: проверка сервера...", "Status: checking server..."),
        ("[INFO] Версия данных игры: ", "[INFO] Game data version: "),
        (": запись не прошла проверку", ": write failed verification"),
        (": не удалось прочитать файл", ": could not read the file"),
        ("Статус: ничего не изменено", "Status: nothing changed"),
        ("Статус: перевод установлен", "Status: translation installed"),
        ("[ИТОГО] Требуют внимания: ", "[SUMMARY] Need attention: "),
        ("Статус: нечего возвращать", "Status: nothing to restore"),
        ("Статус: сервер недоступен", "Status: server unavailable"),
        ("[OK] Заменено сценариев: ", "[OK] Scenes replaced: "),
        ("Shizuku не вернул процесс", "Shizuku did not return a process"),
        ("[OK] Возвращено файлов: ", "[OK] Files restored: "),
        ("). Пробую готовый пак...", "). Trying the prebuilt pack..."),
        (": на ПК новые переводы (", ": new translations on the PC ("),
        ("[OK] Возвращён оригинал ", "[OK] Original restored: "),
        ("в паке нет manifest.json", "manifest.json is missing from the pack"),
        ("Статус: ошибка возврата", "Status: restore error"),
        (": не удалось прочитать ", ": could not read "),
        ("Статус: ошибка сервера", "Status: server error"),
        ("[OK] Сервер подключен.", "[OK] Server connected."),
        ("[WARN] Ответ сервера: ", "[WARN] Server response: "),
        ("[OK] Пак скачан с ПК: ", "[OK] Pack downloaded from the PC: "),
        ("[ИТОГО] Всё актуально.", "[SUMMARY] Everything is up to date."),
        ("[ИТОГО] Всё уже на ПК.", "[SUMMARY] Everything is already on the PC."),
        ("Статус: сервер онлайн", "Status: server online"),
        (" • Шрифт (font.ttf): ", " • Font (font.ttf): "),
        ("[OK] Shizuku запущен.", "[OK] Shizuku is running."),
        ("[INFO] Версия данных ", "[INFO] Data version "),
        ("близких по размеру: [", "close in size: ["),
        (" • Файлов перевода: ", " • Translation files: "),
        (" • Эмулятор по adb: ", " • Emulator via adb: "),
        (", добавлено страниц ", ", pages added "),
        ("[ИТОГО] установлено ", "[SUMMARY] installed "),
        ("[ВЕРСИЯ ИЗМЕНИЛАСЬ]", "[VERSION CHANGED]"),
        ("[ПЕРЕВОД ИЗМЕНИЛСЯ]", "[TRANSLATION CHANGED]"),
        (", изменено файлов: ", ", files changed: "),
        (": файла с размером ", ": file with size "),
        ("[ИТОГО] возвращено ", "[SUMMARY] restored "),
        (". Запустите игру.", ". Start the game."),
        (": сервер ответил ", ": server replied "),
        (", нужно передать ", ", to send: "),
        ("[ИТОГО] передано ", "[SUMMARY] sent "),
        ("[ОШИБКА] Сервер: ", "[ERROR] Server: "),
        ("не скачано игрой ", "not downloaded by the game: "),
        ("[НЕ УСТАНОВЛЕНО]", "[NOT INSTALLED]"),
        ("Проверьте сервер", "Check the server"),
        (", уже актуально ", ", already up to date "),
        (", строк в окне ", ", lines per window "),
        (": у игры CRC32 ", ": the game has CRC32 "),
        ("Статус: ошибка", "Status: error"),
        ("[УСТАНОВЛЕНО]", "[INSTALLED]"),
        ("[НЕ ПОДХОДИТ]", "[MISMATCH]"),
        ("Статус: HTTP ", "Status: HTTP "),
        (": в паке нет ", ": not in the pack: "),
        (" Б), в паке ", " B), pack has "),
        (", пропущено ", ", skipped "),
        (": оригинал (", ": original ("),
        (": переводов ", ": translations: "),
        (": на ПК уже ", ": the PC already has "),
        ("отсутствует", "missing"),
        (": передано ", ": sent "),
        ("[ПРОПУСК]", "[SKIP]"),
        (" и CRC32 ", " and CRC32 "),
        (", в паке ", ", pack has "),
        ("[ОШИБКА]", "[ERROR]"),
        ("доступен", "available"),
        ("[ИТОГО]", "[SUMMARY]"),
        ("Запрос ", "Request "),
        (" сцен).", " scenes)."),
        (": сцен ", ": scenes "),
        ("найден", "found"),
        (" сцен)", " scenes)"),
        ("[НЕТ]", "[MISSING]"),
        (" Б). ", " B). "),
        (" КБ)", " KB)"),
        (" из ", " of "),
        (" КБ", " KB")
    };
}
