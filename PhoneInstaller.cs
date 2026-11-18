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
    // копии собранных патчей: <device_file> (готовый файл главы) + <device_file>.json (метаданные). Нужны для установки без сервера
    const string PatchDir = "/storage/emulated/0/Download/FGO_RU_patch";

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

    /// <summary>
    /// Игра сверяет только CRC32 файла главы со своим списком, а сборка патча подгоняет CRC32 под оригинал.
    /// Значит, CRC32 патча обязан быть равен CRC32 оригинала; иначе игра отвергнет файл и перевода не будет видно.
    /// </summary>
    bool CrcMatchesOriginal(string asset, byte[] patched, uint origCrc, string what)
    {
        var pc = Crc32(patched);
        if (pc == origCrc) return true;
        _log($"[ОШИБКА] {asset}: {what} ({pc}) не равен CRC32 оригинала ({origCrc}). " +
             "Игра отвергнет такой файл, запись отменена. Пересоберите патч на ПК (core/bundle_surgery.py, forge=True).");
        return false;
    }

    /// <summary>Читает файл главы из игры после записи и сверяет CRC32 (то, что проверяет сама игра). При расхождении возвращает прежний файл.</summary>
    bool VerifyInGame(string asset, string path, uint expectedCrc, byte[] previous)
    {
        var back = ReadFile(path);
        var c = back == null ? 0u : Crc32(back);
        if (back != null && c == expectedCrc)
        {
            _log($"[OK] {asset}: CRC32 в игре {c} совпадает с оригиналом");
            return true;
        }
        _log($"[ОШИБКА] {asset}: после записи CRC32 в игре {c}, должно быть {expectedCrc}. Перевод не был бы виден, возвращаю прежний файл.");
        if (WriteFile(path, previous)) _log($"[INFO] {asset}: прежний файл возвращён");
        return false;
    }

    // ---------------------------------------------------------------- пак
    async Task<byte[]?> LoadPackAsync(string serverUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            var b = await http.GetByteArrayAsync(serverUrl.TrimEnd('/') + "/pack.zip");
            _log($"[OK] Пак скачан с ПК: {b.Length / 1024} КБ");
            if (await Task.Run(() => WriteFile(LocalPack, b))) _log("[OK] Копия пака сохранена на телефоне: Download/translate_pack.zip");
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
        // оригинал по списку игры: размер и CRC32 совпали (наша сборка имеет другой размер, но тот же CRC32), владельцу файла не верим
        var curCrc = Crc32(cur);
        bool haveList = size > 0 && crc > 0;
        bool listOriginal = haveList && cur.Length == size && curCrc == (uint)crc;
        if (haveList && curCrc != (uint)crc)
        {
            // ни оригинал, ни наша сборка: игра не докачала главу, файл обновлён или испорчен. Не трогаем, в резервную копию не кладём
            _log($"[ПРОПУСК] {asset}: файл главы не совпадает со списком игры (размер {cur.Length} Б, CRC32 {curCrc}; в списке {size} Б, CRC32 {crc}). " +
                 "Игра ещё не докачала главу или версия изменилась. Откройте сцену этой главы в игре, затем «Передать главы на ПК» и «Установить перевод».");
            return null;
        }
        if (listOriginal || (!haveList && OwnerOf(path) != "shell"))
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
            _log($"[ОШИБКА] {asset}: на телефоне наша сборка, а оригинала этой версии нет. Нажмите «Вернуть оригиналы» или " +
                 "удалите файл через очистку данных игры и откройте сцену, чтобы игра скачала его заново.");
            return null;
        }
        return new Prep(path, b, cur);
    }

    /// <summary>Сохраняет собранный патч на телефоне, чтобы его можно было поставить без сервера.</summary>
    void SaveToCache(string asset, string dev, byte[] original, byte[] patched, int scenes, string stamp)
    {
        try
        {
            Sh($"mkdir -p {Q(PatchDir)}");
            var meta = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["asset"] = asset,
                ["device_file"] = dev,
                ["orig_size"] = original.Length,
                ["orig_md5"] = Md5(original),
                ["patched_md5"] = Md5(patched),
                ["scenes"] = scenes,
                ["stamp"] = stamp,
                ["built"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            });
            if (WriteFile($"{PatchDir}/{dev}", patched) && WriteFile($"{PatchDir}/{dev}.json", meta))
                _log($"[OK] {asset}: копия патча сохранена на телефоне (Download/FGO_RU_patch)");
            else
                _log($"[WARN] {asset}: не удалось сохранить копию патча на телефоне");
        }
        catch (Exception ex) { _log($"[WARN] {asset}: не удалось сохранить копию патча на телефоне ({ex.Message})"); }
    }

    /// <summary>
    /// Установка из копий патча на телефоне (сервер на ПК недоступен). Копия подходит, только если файл главы на телефоне -
    /// ровно тот оригинал (md5), из которого патч был собран, либо уже наша сборка. Иначе игра обновила главу: нужен сервер.
    /// Возвращает false, если копий нет вообще.
    /// </summary>
    bool InstallFromCache()
    {
        var ls = Sh($"ls {Q(PatchDir)} 2>/dev/null");
        var names = ls.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.EndsWith(".bin.json")).ToList();
        if (names.Count == 0) return false;
        _log($"[INFO] Использую копии патча на телефоне: {names.Count} шт.");
        Sh($"mkdir -p {Q(BackupDir)}");
        int ok = 0, same = 0, bad = 0;
        foreach (var name in names)
        {
            string asset = name, dev = name[..^5], origMd5, patchedMd5, stamp;
            try
            {
                var mb = ReadFile($"{PatchDir}/{name}");
                if (mb == null) throw new InvalidOperationException("meta");
                using var doc = JsonDocument.Parse(mb);
                var m = doc.RootElement;
                asset = m.GetProperty("asset").GetString()!;
                dev = m.GetProperty("device_file").GetString()!;
                origMd5 = m.GetProperty("orig_md5").GetString()!;
                patchedMd5 = m.GetProperty("patched_md5").GetString()!;
                stamp = m.TryGetProperty("stamp", out var st) ? st.GetString() ?? "" : "";
            }
            catch (Exception) { _log($"[WARN] Копия патча повреждена или не читается: {name}"); bad++; continue; }

            var patched = ReadFile($"{PatchDir}/{dev}");
            if (patched == null || Md5(patched) != patchedMd5) { _log($"[ОШИБКА] {asset}: копия патча повреждена, нужен сервер на ПК"); bad++; continue; }
            var path = FindPath(dev);
            if (path == null) { _log($"[ПРОПУСК] {asset}: файла нет на телефоне. Откройте любую сцену этой главы в игре, чтобы она скачалась."); bad++; continue; }
            var cur = ReadFile(path);
            if (cur == null) { _log($"[ОШИБКА] {asset}: не удалось прочитать {dev}"); bad++; continue; }
            var curMd5 = Md5(cur);
            if (curMd5 == patchedMd5) { Preferences.Set("stamp_" + asset, stamp); _log($"[OK] {asset}: уже актуально (копия на телефоне)"); same++; continue; }
            if (curMd5 != origMd5)
            {
                _log($"[ОШИБКА] {asset}: версия файла на телефоне изменилась, копия патча не подходит. Нужен сервер на ПК.");
                bad++; continue;
            }
            var origCrc = Crc32(cur);   // файл на телефоне - ровно тот оригинал (md5 совпал), его CRC32 и должен остаться у патча
            if (!CrcMatchesOriginal(asset, patched, origCrc, "CRC32 копии патча")) { bad++; continue; }
            if (!WriteFile($"{BackupDir}/{dev}", cur)) { _log($"[ОШИБКА] {asset}: не удалось сохранить резервную копию"); bad++; continue; }
            Sh($"am force-stop {Pkg}");
            if (!WriteFile(path, patched)) { _log($"[ОШИБКА] {asset}: запись не прошла проверку"); bad++; continue; }
            if (!VerifyInGame(asset, path, origCrc, cur)) { bad++; continue; }
            Preferences.Set("stamp_" + asset, stamp);
            _log($"[УСТАНОВЛЕНО] {asset}: установлено из копии на телефоне, {patched.Length / 1024} КБ");
            ok++;
        }
        if (ok + same > 0) RememberListVersion();
        _log($"[ИТОГО] из копий на телефоне: установлено {ok}, уже актуально {same}, пропущено {bad}. Запустите игру.");
        return true;
    }

    /// <summary>
    /// Размер и CRC32 глав из списка ресурсов ЭТОГО телефона (сервер разбирает присланный список в /sync/check).
    /// /pack_info для этого не годится: он отражает последнее синхронизированное устройство и может быть другой версии игры.
    /// null - список не прочитан или сервер не ответил.
    /// </summary>
    async Task<Dictionary<string, (long size, long crc)>?> GetOwnListInfoAsync(HttpClient http, string baseUrl)
    {
        try
        {
            var lp = await Task.Run(() => FindPath(ListName));
            var ld = lp == null ? null : await Task.Run(() => ReadFile(lp));
            if (ld == null) return null;
            using var resp = await http.PostAsync(baseUrl + "/sync/check", new ByteArrayContent(ld));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var res = new Dictionary<string, (long, long)>();
            foreach (var b in doc.RootElement.GetProperty("bundles").EnumerateArray())
                res[b.GetProperty("asset").GetString()!] = (b.GetProperty("size").GetInt64(), b.GetProperty("crc").GetInt64());
            return res;
        }
        catch (Exception) { return null; }
    }

    public async Task InstallAsync(string serverUrl)
    {
        if (!await EnsureShizukuAsync()) return;
        var baseUrl = serverUrl.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        List<(string asset, string dev, long size, long crc)> list;
        try
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(8));   // не ждём недоступный сервер дольше 8 с
            using var doc = JsonDocument.Parse(await http.GetStringAsync(baseUrl + "/pack_info", probe.Token));
            _log($"[INFO] Сервер на ПК отвечает (пак для версии данных {doc.RootElement.GetProperty("game_version").GetString()}).");
            list = doc.RootElement.GetProperty("bundles").EnumerateArray()
                .Select(b => (b.GetProperty("asset").GetString()!, b.GetProperty("device_file").GetString()!,
                    b.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0L, b.TryGetProperty("crc", out var cr) ? cr.GetInt64() : 0L)).ToList();
        }
        catch (Exception ex)
        {
            _log($"[WARN] Сервер на ПК недоступен или нет списка глав ({ex.Message}). Ищу копии патча на телефоне...");
            if (await Task.Run(InstallFromCache)) return;
            _log("[INFO] Копий патча на телефоне нет. Пробую готовый пак...");
            var pack = await LoadPackAsync(serverUrl);
            if (pack != null) await Task.Run(() => InstallPack(pack));
            return;
        }

        await Task.Run(() => Sh($"mkdir -p {Q(BackupDir)}"));
        var own = await GetOwnListInfoAsync(http, baseUrl);
        if (own == null) _log("[WARN] Не удалось получить размеры глав из списка этого телефона: оригинал определяю без сверки со списком.");
        int ok = 0, bad = 0, same = 0;
        foreach (var (asset, dev, lsize0, lcrc0) in list)
        {
            if (asset.EndsWith("/Common") || asset.EndsWith("/ScriptFileList")) continue;   // служебные файлы, сцен для перевода нет
            long lsize = 0, lcrc = 0;   // 0 = сверки со списком нет
            if (own != null)
            {
                if (!own.TryGetValue(asset, out var oi)) { _log($"[ПРОПУСК] {asset}: для этой главы нет переводов"); bad++; continue; }
                (lsize, lcrc) = oi;
            }
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

            var origCrc = Crc32(prep.Original);
            if (!CrcMatchesOriginal(asset, patched, origCrc, "CRC32 собранного патча")) { bad++; continue; }   // битый патч не пишем и не кладём в копии
            await Task.Run(() => SaveToCache(asset, dev, prep.Original, patched, scenes, stamp));
            if (Md5(patched) == Md5(prep.Current)) { Preferences.Set("stamp_" + asset, stamp); _log($"[OK] {asset}: уже установлено, актуально ({scenes} сцен)"); same++; continue; }
            bool written = await Task.Run(() =>
            {
                Sh($"am force-stop {Pkg}");
                return WriteFile(prep.Path, patched);
            });
            if (!written) { _log($"[ОШИБКА] {asset}: запись не прошла проверку"); bad++; continue; }
            if (!await Task.Run(() => VerifyInGame(asset, prep.Path, origCrc, prep.Current))) { bad++; continue; }
            Preferences.Set("stamp_" + asset, stamp);
            _log($"[УСТАНОВЛЕНО] {asset}: сцен {scenes}, {patched.Length / 1024} КБ");
            ok++;
        }
        if (ok + same > 0) await Task.Run(RememberListVersion);
        _log($"[ИТОГО] установлено {ok}, уже актуально {same}, пропущено {bad}. Запустите игру.");
    }

    // ---------------------------------------------------------------- передача глав на ПК
    const string ListName = "cfb1d36393fd67385e046b084b7cf7ed";

    /// <summary>Отправляет на ПК локальный список игры и все скачанные оригиналы глав, которых там ещё нет. false - синхронизация не состоялась (нет Shizuku, списка или сервера).</summary>
    public async Task<bool> SyncAsync(string serverUrl)
    {
        if (!await EnsureShizukuAsync()) return false;
        var baseUrl = serverUrl.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var listPath = await Task.Run(() => FindPath(ListName));
        if (listPath == null) { _log("[ОШИБКА] Список ресурсов игры не найден. Запустите игру хотя бы раз."); return false; }
        var dir = listPath[..listPath.LastIndexOf('/')];
        var list = await Task.Run(() => ReadFile(listPath));
        if (list == null) { _log("[ОШИБКА] Не удалось прочитать список ресурсов."); return false; }

        JsonElement need;
        try
        {
            using var resp = await http.PostAsync(baseUrl + "/sync/list", new ByteArrayContent(list));
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) { _log($"[ОШИБКА] Сервер: {text}"); return false; }
            var root = JsonDocument.Parse(text).RootElement;
            need = root.GetProperty("need").Clone();
            _log($"[INFO] Версия данных {root.GetProperty("version").GetString()}: на ПК уже {root.GetProperty("have").GetInt32()}, " +
                 $"не скачано игрой {root.GetProperty("not_downloaded").GetInt32()}, нужно передать {need.GetArrayLength()}.");
        }
        catch (Exception ex) { _log($"[ОШИБКА] Сервер недоступен: {ex.Message}"); return false; }
        if (need.GetArrayLength() == 0) { _log("[ИТОГО] Всё уже на ПК."); return true; }

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
        return true;
    }

    // ---------------------------------------------------------------- обнаружение обновления игры (без сервера)
    const string PrefListMd5 = "list_md5";
    static string _lastQuietSig = "";

    /// <summary>Запоминает версию списка ресурсов игры после успешной установки, чтобы потом заметить обновление игры.</summary>
    void RememberListVersion()
    {
        try
        {
            var p = FindPath(ListName);
            var d = p == null ? null : ReadFile(p);
            if (d != null) Preferences.Set(PrefListMd5, Md5(d));
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Без сервера отвечает на вопрос «игра обновилась?»: сравнивает файлы глав с копиями патча на телефоне
    /// (оригинал, из которого собран патч, или сам патч) и список ресурсов игры с запомненным при установке.
    /// quiet = true: не просит разрешение Shizuku и пишет в журнал только то, что требует внимания (и только когда результат изменился).
    /// Возвращает строку статуса (по-русски) и признак «нужно действие», либо null, если сказать нечего.
    /// </summary>
    public async Task<(string Status, bool Attention)?> DetectGameUpdateAsync(bool quiet = false)
    {
        if (quiet) { if (!ShizukuShell.ClassPresent() || !ShizukuShell.PingBinder() || ShizukuShell.CheckPermission() != 0) return null; }
        else if (!await EnsureShizukuAsync()) return null;
        return await Task.Run<(string, bool)?>(() =>
        {
            var lines = new List<string>();
            void Info(string m) { if (!quiet) lines.Add(m); }
            void Flush(string sig) { if (!quiet || sig != _lastQuietSig) foreach (var l in lines) _log(l); _lastQuietSig = quiet ? sig : _lastQuietSig; }

            var names = Sh($"ls {Q(PatchDir)} 2>/dev/null").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.EndsWith(".bin.json")).ToList();
            if (names.Count == 0)
            {
                if (quiet) return null;
                _log("[INFO] Копий патча на телефоне пока нет, сравнивать не с чем. Нажмите «Установить перевод».");
                return ("Статус: перевод ещё не устанавливался", false);
            }
            int ok = 0, original = 0, updated = 0, missing = 0;
            foreach (var name in names)
            {
                string asset, dev, origMd5, patchedMd5;
                try
                {
                    var mb = ReadFile($"{PatchDir}/{name}");
                    if (mb == null) continue;
                    using var doc = JsonDocument.Parse(mb);
                    var m = doc.RootElement;
                    asset = m.GetProperty("asset").GetString()!;
                    dev = m.GetProperty("device_file").GetString()!;
                    origMd5 = m.GetProperty("orig_md5").GetString()!;
                    patchedMd5 = m.GetProperty("patched_md5").GetString()!;
                }
                catch (Exception) { continue; }
                var path = FindPath(dev);
                var cur = path == null ? null : ReadFile(path);
                if (cur == null) { missing++; Info($"[НЕТ] {asset}: файла нет на телефоне (игра его не скачала). Откройте сцену этой главы в игре."); continue; }
                var md5 = Md5(cur);
                if (md5 == patchedMd5) { ok++; Info($"[OK] {asset}: перевод установлен, файл главы не менялся."); }
                else if (md5 == origMd5) { original++; lines.Add($"[НЕ УСТАНОВЛЕНО] {asset}: на телефоне оригинал (перевод слетел или не ставился). Нажмите «Установить перевод»."); }
                else { updated++; lines.Add($"[ИГРА ОБНОВИЛАСЬ] {asset}: файл главы изменился, копия патча больше не подходит. Нажмите «Передать главы на ПК», затем «Установить перевод»."); }
            }
            var lp = FindPath(ListName);
            var ld = lp == null ? null : ReadFile(lp);
            var listMd5 = ld == null ? "" : Md5(ld);
            var savedList = Preferences.Get(PrefListMd5, "");
            bool listChanged = listMd5 != "" && savedList != "" && listMd5 != savedList;
            var sig = $"{ok}|{original}|{updated}|{missing}|{listChanged}";

            (string, bool)? result;
            if (updated > 0) result = ("Статус: игра обновила главы, нужна синхронизация", true);
            else if (original > 0) result = ("Статус: перевод слетел, нажмите «Установить перевод»", true);
            else if (listChanged)
            {
                lines.Add("[INFO] Список ресурсов игры изменился (игра обновилась), но установленные главы не затронуты. Новые главы появятся после «Передать главы на ПК».");
                result = ("Статус: перевод актуален, игра обновила список ресурсов", false);
            }
            else if (ok > 0) result = ("Статус: перевод установлен и актуален", false);
            else if (missing > 0) result = ("Статус: файлов глав нет, откройте сцену в игре", true);
            else result = null;
            Flush(sig);
            return result;
        });
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
            if (dev == "") { _log($"[?] {asset}: переводов {n}, но глава ещё не передавалась на ПК. Нажмите «Передать главы на ПК», затем «Установить перевод»."); needInstall++; continue; }
            var path = await Task.Run(() => FindPath(dev));
            if (path == null) { _log($"[НЕТ] {asset}: файла нет на телефоне (игра его не скачала). Откройте сцену этой главы в игре."); continue; }
            var data = await Task.Run(() => ReadFile(path));
            if (data == null) { _log($"[ОШИБКА] {asset}: не удалось прочитать файл"); continue; }
            var fcrc = Crc32(data);
            var saved = Preferences.Get("stamp_" + asset, "");
            if (fcrc != crc)
            {
                _log($"[ВЕРСИЯ ИЗМЕНИЛАСЬ] {asset}: файл не соответствует списку игры (игра обновляет или докачивает данные). Откройте сцену в игре, затем «Передать главы на ПК» и «Установить перевод».");
                needInstall++;
            }
            else if (data.Length == size)
            {
                _log(saved != ""
                    ? $"[ВЕРСИЯ ИЗМЕНИЛАСЬ] {asset}: игра заменила файл оригиналом, перевод слетел ({n} сцен). Нажмите «Установить перевод», чтобы переустановить."
                    : $"[НЕ УСТАНОВЛЕНО] {asset}: на телефоне оригинал, переводов {n}. Нажмите «Установить перевод».");
                needInstall++;
            }
            else if (saved != stamp)
            {
                _log($"[ПЕРЕВОД ИЗМЕНИЛСЯ] {asset}: на ПК новые переводы ({n} сцен). Нажмите «Установить перевод», чтобы обновить.");
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
