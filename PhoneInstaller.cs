using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace FGOPatcher.Maui;

/// <summary>
/// Установка пака перевода на ЭТОТ телефон через Shizuku (то же, что делает install_pack.py с ПК):
/// найти файл главы -> сверить CRC32 с паком -> сохранить оригинал -> записать файл из пака -> проверить md5.
/// Пак (translate_pack.zip) берётся с ПК (server.py, /pack.zip) или из Download/translate_pack.zip на телефоне.
/// </summary>
public sealed class PhoneInstaller
{
    const string Pkg = "com.aniplex.fategrandorder";
    const string DataRoot = "/storage/emulated/0/Android/data/" + Pkg + "/files/data";
    const string BackupDir = "/storage/emulated/0/Download/FGO_RU_backup";
    const string LocalPack = "/storage/emulated/0/Download/translate_pack.zip";

    readonly Action<string> _log;
    public PhoneInstaller(Action<string> log) => _log = log;

    static ShizukuShell.ShellResult Sh(string cmd, Stream? stdin = null) => ShizukuShell.Run(cmd, stdin);
    static string Q(string s) => ShizukuShell.Quote(s);

    // ---------------------------------------------------------------- Shizuku
    public async Task<bool> EnsureShizukuAsync()
    {
        if (!ShizukuShell.ClassPresent()) { _log("[ERROR] Классы Shizuku не найдены в приложении."); return false; }
        if (!ShizukuShell.PingBinder())
        {
            _log("[WARN] Shizuku не запущен (или приложение не получило связь). Запустите Shizuku, затем полностью закройте и откройте патчер.");
            return false;
        }
        if (ShizukuShell.CheckPermission() != 0)
        {
            _log("[INFO] Разрешите доступ в окне Shizuku (30 секунд)...");
            ShizukuShell.RequestPermission(1000);
            for (int i = 0; i < 30 && ShizukuShell.CheckPermission() != 0; i++) await Task.Delay(1000);
            if (ShizukuShell.CheckPermission() != 0) { _log("[WARN] Разрешение не выдано. Нажмите кнопку ещё раз."); return false; }
        }
        return true;
    }

    // ---------------------------------------------------------------- файлы на устройстве
    static string? FindPath(string deviceFile)
    {
        var r = Sh($"ls {DataRoot}/*/{deviceFile} 2>/dev/null");
        foreach (var l in r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (l.Trim().EndsWith(deviceFile)) return l.Trim();
        return null;
    }

    static byte[]? ReadFile(string path)
    {
        var r = Sh($"cat {Q(path)}");
        return r.Exit == 0 ? r.Out : null;
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    static string Md5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    /// <summary>Пишет файл через временный файл в той же папке и переименование; проверяет результат по md5.</summary>
    static bool WriteFile(string dest, byte[] data)
    {
        var dir = dest[..dest.LastIndexOf('/')];
        var tmp = dir + "/.fgoru_tmp";
        var w = Sh($"cat > {Q(tmp)}", new MemoryStream(data));
        if (w.Exit != 0) return false;
        Sh($"chmod 666 {Q(tmp)}; mv -f {Q(tmp)} {Q(dest)}");
        var back = ReadFile(dest);
        return back != null && Md5(back) == Md5(data);
    }

    // ---------------------------------------------------------------- пак
    async Task<byte[]?> LoadPackAsync(string serverUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            var b = await http.GetByteArrayAsync(serverUrl.TrimEnd('/') + "/pack.zip");
            _log($"[OK] Пак скачан с ПК: {b.Length / 1024} КБ");
            return b;
        }
        catch (Exception ex)
        {
            _log($"[WARN] Сервер на ПК недоступен ({ex.Message}). Ищу пак в Download/translate_pack.zip ...");
        }
        var r = await Task.Run(() => Sh($"cat {Q(LocalPack)}"));
        if (r.Exit == 0 && r.Out.Length > 100)
        {
            _log($"[OK] Пак прочитан из Download/translate_pack.zip: {r.Out.Length / 1024} КБ");
            return r.Out;
        }
        _log("[ERROR] Пака нет: запустите server.py на ПК (adb reverse tcp:5000 tcp:5000) или положите translate_pack.zip в папку Download на телефоне.");
        return null;
    }

    // ---------------------------------------------------------------- установка
    sealed record Prep(string Path, byte[] Original, byte[] Current);

    static string OwnerOf(string path)
    {
        // ls -l: -rw-rw-rw- 1 shell ext_data_rw ...  (файлы, записанные нами, принадлежат shell; скачанные игрой - uid игры)
        var parts = Sh($"ls -l {Q(path)}").Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 ? parts[2] : "";
    }

    /// <summary>Находит файл главы и оригинал для сборки (сохраняет его в резервную копию). null - ставить нельзя.</summary>
    Prep? PrepareOriginal(string asset, string dev, long size = 0, long crc = 0)
    {
        var path = FindPath(dev);
        if (path == null)
        {
            _log($"[ПРОПУСК] {asset}: файла нет на телефоне. Откройте любую сцену этой главы в игре, чтобы она скачалась.");
            return null;
        }
        var cur = ReadFile(path);
        if (cur == null) { _log($"[ОШИБКА] {asset}: не удалось прочитать {dev}"); return null; }
        var backup = $"{BackupDir}/{dev}";
        // оригинал по списку игры: размер и CRC32 совпали (наша сборка имеет другой размер), владельцу файла не верим
        bool listOriginal = size > 0 && cur.Length == size && Crc32(cur) == (uint)crc;
        if (listOriginal || OwnerOf(path) != "shell")
        {
            // файл скачан игрой: это оригинал текущей версии
            if (!WriteFile(backup, cur)) { _log($"[ОШИБКА] {asset}: не удалось сохранить резервную копию"); return null; }
            _log($"[OK] {asset}: оригинал ({cur.Length / 1024} КБ) сохранён в Download/FGO_RU_backup");
            return new Prep(path, cur, cur);
        }
        // файл записан нами: оригинал берём из резервной копии, она должна быть той же версии (CRC32 совпадает)
        var b = ReadFile(backup);
        if (b == null || Crc32(b) != Crc32(cur))
        {
            _log($"[ОШИБКА] {asset}: на телефоне наша сборка, а оригинала этой версии нет. Нажмите «6. Вернуть оригиналы» или " +
                 "удалите файл через очистку данных игры и откройте сцену, чтобы игра скачала его заново.");
            return null;
        }
        return new Prep(path, b, cur);
    }

    public async Task InstallAsync(string serverUrl)
    {
        if (!await EnsureShizukuAsync()) return;
        var baseUrl = serverUrl.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        List<(string asset, string dev, long size, long crc)> list;
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(baseUrl + "/pack_info"));
            _log($"[INFO] Сервер на ПК отвечает (пак для версии данных {doc.RootElement.GetProperty("game_version").GetString()}).");
            list = doc.RootElement.GetProperty("bundles").EnumerateArray()
                .Select(b => (b.GetProperty("asset").GetString()!, b.GetProperty("device_file").GetString()!,
                    b.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0L, b.TryGetProperty("crc", out var cr) ? cr.GetInt64() : 0L)).ToList();
        }
        catch (Exception ex)
        {
            _log($"[WARN] Сервер на ПК недоступен или нет списка глав ({ex.Message}). Пробую готовый пак...");
            var pack = await LoadPackAsync(serverUrl);
            if (pack != null) await Task.Run(() => InstallPack(pack));
            return;
        }

        await Task.Run(() => Sh($"mkdir -p {Q(BackupDir)}"));
        int ok = 0, bad = 0, same = 0;
        foreach (var (asset, dev, lsize, lcrc) in list)
        {
            if (asset.EndsWith("/Common") || asset.EndsWith("/ScriptFileList")) continue;   // служебные файлы, сцен для перевода нет
            var prep = await Task.Run(() => PrepareOriginal(asset, dev, lsize, lcrc));
            if (prep == null) { bad++; continue; }

            _log($"[INFO] {asset}: сервер собирает перевод под эту версию главы (первый раз может занять минуту)...");
            byte[]? patched; int scenes; string stamp = "";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/build") { Content = new ByteArrayContent(prep.Original) };
                req.Headers.Add("X-Device-File", dev);
                using var resp = await http.SendAsync(req);
                if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) { _log($"[ПРОПУСК] {asset}: для этой главы нет переводов"); bad++; continue; }
                if (!resp.IsSuccessStatusCode) { _log($"[ОШИБКА] {asset}: сервер ответил {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}"); bad++; continue; }
                patched = await resp.Content.ReadAsByteArrayAsync();
                stamp = resp.Headers.TryGetValues("X-Stamp", out var sv) ? sv.FirstOrDefault() ?? "" : "";
                scenes = resp.Headers.TryGetValues("X-Scenes", out var v) && int.TryParse(v.FirstOrDefault(), out var n) ? n : 0;
            }
            catch (Exception ex) { _log($"[ОШИБКА] {asset}: {ex.Message}"); bad++; continue; }

            if (Md5(patched) == Md5(prep.Current)) { Preferences.Set("stamp_" + asset, stamp); _log($"[OK] {asset}: уже установлено, актуально ({scenes} сцен)"); same++; continue; }
            bool written = await Task.Run(() =>
            {
                Sh($"am force-stop {Pkg}");
                return WriteFile(prep.Path, patched);
            });
            if (!written) { _log($"[ОШИБКА] {asset}: запись не прошла проверку"); bad++; continue; }
            Preferences.Set("stamp_" + asset, stamp);
            _log($"[УСТАНОВЛЕНО] {asset}: сцен {scenes}, {patched.Length / 1024} КБ");
            ok++;
        }
        _log($"[ИТОГО] установлено {ok}, уже актуально {same}, пропущено {bad}. Запустите игру.");
    }

    // ---------------------------------------------------------------- передача глав на ПК
    const string ListName = "cfb1d36393fd67385e046b084b7cf7ed";

    /// <summary>Отправляет на ПК локальный список игры и все скачанные оригиналы глав, которых там ещё нет.</summary>
    public async Task SyncAsync(string serverUrl)
    {
        if (!await EnsureShizukuAsync()) return;
        var baseUrl = serverUrl.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var listPath = await Task.Run(() => FindPath(ListName));
        if (listPath == null) { _log("[ОШИБКА] Список ресурсов игры не найден. Запустите игру хотя бы раз."); return; }
        var dir = listPath[..listPath.LastIndexOf('/')];
        var list = await Task.Run(() => ReadFile(listPath));
        if (list == null) { _log("[ОШИБКА] Не удалось прочитать список ресурсов."); return; }

        JsonElement need;
        try
        {
            using var resp = await http.PostAsync(baseUrl + "/sync/list", new ByteArrayContent(list));
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { _log($"[ОШИБКА] Сервер: {text}"); return; }
            var root = JsonDocument.Parse(text).RootElement;
            need = root.GetProperty("need").Clone();
            _log($"[INFO] Версия данных {root.GetProperty("version").GetString()}: на ПК уже {root.GetProperty("have").GetInt32()}, " +
                 $"не скачано игрой {root.GetProperty("not_downloaded").GetInt32()}, нужно передать {need.GetArrayLength()}.");
        }
        catch (Exception ex) { _log($"[ОШИБКА] Сервер недоступен: {ex.Message}"); return; }
        if (need.GetArrayLength() == 0) { _log("[ИТОГО] Всё уже на ПК."); return; }

        // файлы папки данных: имя -> (владелец, размер)
        var files = new List<(string name, string owner, long size)>();
        foreach (var l in (await Task.Run(() => Sh($"ls -l {Q(dir)}/"))).Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 8 && long.TryParse(p[4], out var sz)) files.Add((p[^1].Split('/')[^1], p[2], sz));
        }

        // оригиналы, которые мы сохранили при установке перевода (на самом телефоне сейчас наша сборка)
        var backups = new List<(string name, long size)>();
        foreach (var l in (await Task.Run(() => Sh($"ls -l {Q(BackupDir)}/ 2>/dev/null"))).Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 8 && long.TryParse(p[4], out var sz)) backups.Add((p[^1].Split('/')[^1], sz));
        }

        int sent = 0, missing = 0;
        foreach (var n in need.EnumerateArray())
        {
            var asset = n.GetProperty("asset").GetString()!;
            var size = n.GetProperty("size").GetInt64();
            var crc = n.GetProperty("crc").GetInt64();
            // оригинал: размер и CRC32 как в списке игры (наша сборка имеет другой размер; владельца не проверяем - он ненадёжен)
            byte[]? found = null; string? dev = null;
            foreach (var f in files.Where(f => f.size == size))
            {
                var data = await Task.Run(() => ReadFile($"{dir}/{f.name}"));
                if (data != null && Crc32(data) == (uint)crc) { found = data; dev = f.name; break; }
            }
            if (found == null)
                foreach (var f in backups.Where(f => f.size == size))
                {
                    var data = await Task.Run(() => ReadFile($"{BackupDir}/{f.name}"));
                    if (data != null && Crc32(data) == (uint)crc) { found = data; dev = f.name; break; }
                }
            if (found == null)
            {
                var same = files.Where(f => f.size == size).Select(f => $"{f.name[..8]}…({f.owner})").ToList();
                var near = files.Where(f => f.owner != "shell" && Math.Abs(f.size - size) < 4096 && f.size != size).Select(f => $"{f.name[..8]}…={f.size}").ToList();
                _log($"[ПРОПУСК] {asset}: файла с размером {size} и CRC32 {crc} нет. Файлов того же размера: {same.Count} [{string.Join(", ", same)}]; " +
                     $"близких по размеру: [{string.Join(", ", near)}]. Глава не скачана целиком или заменена нашей сборкой.");
                missing++; continue;
            }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/sync/file") { Content = new ByteArrayContent(found) };
                req.Headers.Add("X-Asset", asset);
                req.Headers.Add("X-Device-File", dev!);
                req.Headers.Add("X-CRC", crc.ToString(CultureInfo.InvariantCulture));
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) { _log($"[ОШИБКА] {asset}: {await resp.Content.ReadAsStringAsync()}"); missing++; continue; }
                _log($"[OK] {asset}: передано {found.Length / 1024} КБ");
                sent++;
            }
            catch (Exception ex) { _log($"[ОШИБКА] {asset}: {ex.Message}"); missing++; }
        }
        _log($"[ИТОГО] передано {sent}, пропущено {missing}.");
    }

    // ---------------------------------------------------------------- проверка обновлений
    /// <summary>Сравнивает файлы глав на телефоне со списком игры и с переводами на ПК; ничего не меняет.</summary>
    public async Task CheckUpdatesAsync(string serverUrl)
    {
        if (!await EnsureShizukuAsync()) return;
        var baseUrl = serverUrl.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var listPath = await Task.Run(() => FindPath(ListName));
        if (listPath == null) { _log("[ОШИБКА] Список ресурсов игры не найден. Запустите игру хотя бы раз."); return; }
        var list = await Task.Run(() => ReadFile(listPath));
        if (list == null) { _log("[ОШИБКА] Не удалось прочитать список ресурсов."); return; }
        JsonElement bundles;
        try
        {
            using var resp = await http.PostAsync(baseUrl + "/sync/check", new ByteArrayContent(list));
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { _log($"[ОШИБКА] Сервер: {text}"); return; }
            bundles = JsonDocument.Parse(text).RootElement.GetProperty("bundles").Clone();
        }
        catch (Exception ex) { _log($"[ОШИБКА] Сервер недоступен: {ex.Message}"); return; }
        if (bundles.GetArrayLength() == 0) { _log("[INFO] Нет скачанных глав, для которых есть переводы."); return; }

        int needInstall = 0;
        foreach (var b in bundles.EnumerateArray())
        {
            var asset = b.GetProperty("asset").GetString()!;
            var size = b.GetProperty("size").GetInt64();
            var crc = (uint)b.GetProperty("crc").GetInt64();
            var dev = b.GetProperty("device_file").GetString() ?? "";
            var stamp = b.GetProperty("stamp").GetString() ?? "";
            var n = b.GetProperty("translated").GetInt32();
            if (dev == "") { _log($"[?] {asset}: переводов {n}, но глава ещё не передавалась на ПК. Нажмите «7. Передать главы на ПК», затем «5»."); needInstall++; continue; }
            var path = await Task.Run(() => FindPath(dev));
            if (path == null) { _log($"[НЕТ] {asset}: файла нет на телефоне (игра его не скачала). Откройте сцену этой главы в игре."); continue; }
            var data = await Task.Run(() => ReadFile(path));
            if (data == null) { _log($"[ОШИБКА] {asset}: не удалось прочитать файл"); continue; }
            var fcrc = Crc32(data);
            var saved = Preferences.Get("stamp_" + asset, "");
            if (fcrc != crc)
            {
                _log($"[ВЕРСИЯ ИЗМЕНИЛАСЬ] {asset}: файл не соответствует списку игры (игра обновляет или докачивает данные). Откройте сцену в игре, затем «7» и «5».");
                needInstall++;
            }
            else if (data.Length == size)
            {
                _log(saved != ""
                    ? $"[ВЕРСИЯ ИЗМЕНИЛАСЬ] {asset}: игра заменила файл оригиналом, перевод слетел ({n} сцен). Нажмите «5», чтобы переустановить."
                    : $"[НЕ УСТАНОВЛЕНО] {asset}: на телефоне оригинал, переводов {n}. Нажмите «5».");
                needInstall++;
            }
            else if (saved != stamp)
            {
                _log($"[ПЕРЕВОД ИЗМЕНИЛСЯ] {asset}: на ПК новые переводы ({n} сцен). Нажмите «5», чтобы обновить.");
                needInstall++;
            }
            else _log($"[OK] {asset}: перевод установлен и актуален ({n} сцен).");
        }
        _log(needInstall == 0 ? "[ИТОГО] Всё актуально." : $"[ИТОГО] Требуют внимания: {needInstall}.");
    }

    void InstallPack(byte[] packZip)
    {
        using var zip = new ZipArchive(new MemoryStream(packZip), ZipArchiveMode.Read);
        var me = zip.GetEntry("manifest.json") ?? throw new InvalidOperationException("в паке нет manifest.json");
        string json;
        using (var sr = new StreamReader(me.Open())) json = sr.ReadToEnd();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _log($"[INFO] Пак собран для версии данных {root.GetProperty("game_version").GetString()}");
        Sh($"mkdir -p {Q(BackupDir)}");
        int ok = 0, bad = 0;
        foreach (var b in root.GetProperty("bundles").EnumerateArray())
        {
            string asset = b.GetProperty("asset").GetString()!;
            string dev = b.GetProperty("device_file").GetString()!;
            string fileInPack = b.GetProperty("file").GetString()!;
            long origSize = b.GetProperty("orig_size").GetInt64();
            long origCrc = b.GetProperty("orig_crc").GetInt64();
            long newSize = b.GetProperty("size").GetInt64();
            string md5 = b.GetProperty("md5").GetString()!;
            int scenes = b.GetProperty("scenes").GetArrayLength();

            var path = FindPath(dev);
            if (path == null)
            {
                _log($"[ПРОПУСК] {asset}: файла нет на телефоне. Откройте любую сцену этой главы в игре, чтобы она скачалась.");
                bad++; continue;
            }
            var cur = ReadFile(path);
            if (cur == null) { _log($"[ОШИБКА] {asset}: не удалось прочитать {dev}"); bad++; continue; }
            long crc = Crc32(cur);
            if (crc != origCrc)
            {
                _log($"[НЕ ПОДХОДИТ] {asset}: у игры CRC32 {crc} ({cur.Length} Б), в паке {origCrc} ({origSize} Б). " +
                     "Версия главы другая: соберите пак на ПК под этот телефон (build_pack.py --serial ...).");
                bad++; continue;
            }
            var backup = $"{BackupDir}/{dev}";
            if (cur.Length == origSize)
            {
                // на телефоне оригинал: сохраняем его
                if (!WriteFile(backup, cur)) { _log($"[ОШИБКА] {asset}: не удалось сохранить резервную копию"); bad++; continue; }
                _log($"[OK] {asset}: оригинал сохранён в Download/FGO_RU_backup");
            }
            else if (ReadFile(backup) == null)
            {
                _log($"[ОШИБКА] {asset}: на телефоне не оригинал и нет резервной копии. Удалите файл в игре (очистка кэша) или верните оригинал и повторите.");
                bad++; continue;
            }
            var entry = zip.GetEntry(fileInPack);
            if (entry == null) { _log($"[ОШИБКА] {asset}: в паке нет {fileInPack}"); bad++; continue; }
            byte[] data;
            using (var ms = new MemoryStream()) { using var es = entry.Open(); es.CopyTo(ms); data = ms.ToArray(); }
            if (Md5(data) != md5 || data.Length != newSize) { _log($"[ОШИБКА] {asset}: пак повреждён (md5/размер)"); bad++; continue; }
            Sh($"am force-stop {Pkg}");
            if (!WriteFile(path, data)) { _log($"[ОШИБКА] {asset}: запись не прошла проверку"); bad++; continue; }
            _log($"[УСТАНОВЛЕНО] {asset}: сцен {scenes}, {data.Length / 1024} КБ");
            ok++;
        }
        _log($"[ИТОГО] установлено {ok}, пропущено {bad}. Запустите игру.");
    }

    // ---------------------------------------------------------------- откат
    public async Task RestoreAsync()
    {
        if (!await EnsureShizukuAsync()) return;
        await Task.Run(() =>
        {
            var names = Sh($"ls {Q(BackupDir)} 2>/dev/null").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (names.Count == 0) { _log("[INFO] Резервных копий нет: перевод с этого телефона не ставился."); return; }
            Sh($"am force-stop {Pkg}");
            int ok = 0;
            foreach (var n in names)
            {
                var path = FindPath(n);
                var data = ReadFile($"{BackupDir}/{n}");
                if (path == null || data == null) { _log($"[ПРОПУСК] {n}: нет файла в игре или копии"); continue; }
                if (WriteFile(path, data)) { _log($"[OK] Возвращён оригинал {n} ({data.Length / 1024} КБ)"); ok++; }
                else _log($"[ОШИБКА] {n}: запись не прошла проверку");
            }
            _log($"[ИТОГО] возвращено {ok} из {names.Count}.");
        });
    }
}
