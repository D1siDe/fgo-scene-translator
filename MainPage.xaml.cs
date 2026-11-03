using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;

namespace FGOPatcher.Maui;

public partial class MainPage : ContentPage
{
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    // сборка перевода на сервере может занять несколько минут (первый запуск читает бандл главы)
    private static readonly HttpClient _httpLong = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    private string ServerUrl => (ServerEntry.Text ?? "http://127.0.0.1:5000").Trim().TrimEnd('/');

    public MainPage()
    {
        InitializeComponent();
        ApplyLanguage();
        try { ServerEntry.Text = Microsoft.Maui.Storage.Preferences.Get("server_url", "http://127.0.0.1:5000"); } catch { }
        ServerEntry.TextChanged += (_, e) => { try { Microsoft.Maui.Storage.Preferences.Set("server_url", e.NewTextValue ?? ""); } catch { } };
        AppendLog("[INIT] FGO Scene Translator готов. Сначала проверьте сервер.");
    }

    private void AppendLog(string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LogEditor.Text += $"[{DateTime.Now:HH:mm:ss}] {L.Loc(message)}\n";
        });
    }

    // Текст статуса хранится по-русски и переводится при показе, поэтому переключение языка обновляет его на лету
    private string _statusRu = "Проверьте сервер";
    private void SetStatus(string ru)
    {
        _statusRu = ru;
        StatusLabel.Text = L.Loc(ru);
    }

    private bool _advancedOpen;

    private void ApplyLanguage()
    {
        LangButton.Text = L.En ? "RU" : "EN";
        HintLabel.Text = L.T(
            "Порядок: проверить сервер и Shizuku → «Установить перевод». «Передать главы на ПК» нужна после скачивания новых глав в игре.",
            "Order: check the server and Shizuku → “Install translation”. Use “Send chapters to PC” after the game downloads new chapters.");
        ServerEntry.Placeholder = L.T("Адрес сервера на ПК", "Server address on the PC");
        CheckButton.Text = L.T("Проверить сервер", "Check server");
        ShizukuButton.Text = L.T("Проверить Shizuku", "Check Shizuku");
        PhoneInstallButton.Text = L.T("Установить перевод", "Install translation");
        PhoneCheckButton.Text = L.T("Проверить обновления", "Check for updates");
        PhoneSyncButton.Text = L.T("Передать главы на ПК", "Send chapters to PC");
        PhoneRestoreButton.Text = L.T("Вернуть оригиналы", "Restore originals");
        PatchButton.Text = L.T("Установить через ПК (эмулятор)", "Install via PC (emulator)");
        RestoreButton.Text = L.T("Вернуть оригиналы через ПК (эмулятор)", "Restore originals via PC (emulator)");
        UpdateAdvancedToggle();
        StatusLabel.Text = L.Loc(_statusRu);
    }

    private void UpdateAdvancedToggle()
    {
        AdvancedToggle.Text = (_advancedOpen ? "▾ " : "▸ ") +
            L.T("Режим эмулятора (установка через adb на ПК)", "Emulator mode (install through adb on the PC)");
    }

    private void OnLangClicked(object sender, EventArgs e)
    {
        L.Set(!L.En);
        ApplyLanguage();
    }

    private void OnAdvancedClicked(object sender, EventArgs e)
    {
        _advancedOpen = !_advancedOpen;
        AdvancedPanel.IsVisible = _advancedOpen;
        UpdateAdvancedToggle();
    }

    private async void OnCheckServerClicked(object sender, EventArgs e)
    {
        SetStatus("Статус: проверка сервера...");
        StatusLabel.TextColor = Colors.Orange;
        AppendLog($"Запрос {ServerUrl}/status ...");

        try
        {
            using var response = await _http.GetAsync($"{ServerUrl}/status");
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                bool fontReady = root.TryGetProperty("font_ready", out var f) && f.GetBoolean();
                int scenarios = root.TryGetProperty("scenarios_count", out var s) ? s.GetInt32() : 0;
                int cached = root.TryGetProperty("cached_bundles", out var c) ? c.GetInt32() : 0;
                bool deviceReady = root.TryGetProperty("device_ready", out var dr) && dr.GetBoolean();

                SetStatus("Статус: сервер онлайн");
                StatusLabel.TextColor = Color.FromArgb("#4CAF50");
                AppendLog("[OK] Сервер подключен.");
                AppendLog($" • Шрифт (font.ttf): {(fontReady ? "найден" : "отсутствует")}");
                AppendLog($" • Файлов перевода: {scenarios}");
                AppendLog($" • Файлов в raw_game_cache: {cached}");
                AppendLog($" • Эмулятор по adb: {(deviceReady ? "доступен" : "НЕ доступен (перевод не будет установлен)")}");
            }
            else
            {
                SetStatus($"Статус: HTTP {(int)response.StatusCode}");
                StatusLabel.TextColor = Colors.Red;
                AppendLog($"[WARN] Ответ сервера: {body}");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Статус: сервер недоступен");
            StatusLabel.TextColor = Colors.Red;
            AppendLog($"[ERROR] {ex.Message}");
            AppendLog("[HINT] Запустите server.py на ПК и выполните: adb reverse tcp:5000 tcp:5000");
        }
    }

    private static List<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ToString()).ToList()
            : new List<string>();

    private void SetBusy(bool busy)
    {
        PatchButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
        ShizukuButton.IsEnabled = !busy;
        PhoneInstallButton.IsEnabled = !busy;
        PhoneRestoreButton.IsEnabled = !busy;
        PhoneSyncButton.IsEnabled = !busy;
        PhoneCheckButton.IsEnabled = !busy;
    }

    // Сервер сам собирает перевод и ставит его на эмулятор по adb (папка Android/data для приложений закрыта)
    private async void OnApplyPatchClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        SetStatus("Статус: сборка и установка перевода (может занять несколько минут)...");
        StatusLabel.TextColor = Colors.Orange;
        AppendLog("[PATCH] Сервер собирает перевод и ставит его на устройство через adb ...");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var response = await _httpLong.GetAsync($"{ServerUrl}/patch", cts.Token);
            var body = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var errors = Strings(root, "errors");

            if (!response.IsSuccessStatusCode)
            {
                SetStatus("Статус: ошибка сервера");
                StatusLabel.TextColor = Colors.Red;
                AppendLog($"[ERROR] {(root.TryGetProperty("error", out var er) ? er.ToString() : body)}");
                return;
            }

            string mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
            int scanned = root.TryGetProperty("total_scanned", out var ts) ? ts.GetInt32() : 0;
            int patched = root.TryGetProperty("patched_count", out var pc) ? pc.GetInt32() : 0;
            var scripts = Strings(root, "scripts_replaced");
            var skipped = Strings(root, "skipped_no_space");
            var noBundle = Strings(root, "translations_without_bundle");

            if (root.TryGetProperty("game_version", out var gv) && gv.ValueKind == JsonValueKind.String)
                AppendLog($"[INFO] Версия данных игры: {gv.GetString()}");
            foreach (var pr in Strings(root, "problems")) AppendLog($"[WARN] {pr}  → запустите игру, дождитесь обновления данных и повторите");
            AppendLog($"[OK] Бандлов сценариев найдено: {scanned}, изменено файлов: {patched}");
            AppendLog($"[OK] Заменено сценариев: {scripts.Count} {(scripts.Count > 0 ? "(" + string.Join(", ", scripts) + ")" : "")}");
            if (root.TryGetProperty("wrap", out var wrap) && wrap.ValueKind == JsonValueKind.Object && wrap.TryGetProperty("width", out var ww))
                AppendLog($"[OK] Переносы строк: ширина {ww}, строк в окне {wrap.GetProperty("lines")}, добавлено страниц {wrap.GetProperty("extra_pages")}");
            if (root.TryGetProperty("bundle_sizes", out var bsz) && bsz.ValueKind == JsonValueKind.Object)
                foreach (var f in bsz.EnumerateObject()) AppendLog($"[OK] {f.Name}: {f.Value.GetInt64() / 1024} КБ (ограничения по размеру нет, проверяется только CRC32)");
            foreach (var n in skipped) AppendLog($"[WARN] Не удалось собрать, пропущено: {n}");
            foreach (var n in noBundle) AppendLog($"[WARN] Для перевода нет бандла (проверьте имя файла): {n}");
            foreach (var err in errors) AppendLog($"[WARN] {err}");

            if (mode != "device")
            {
                SetStatus("Статус: перевод собран, но НЕ установлен");
                StatusLabel.TextColor = Colors.Orange;
                AppendLog("[WARN] Эмулятор недоступен по adb: файлы лежат в fgo_local_mods на ПК, в игру ничего не поставлено.");
            }
            else if (patched == 0)
            {
                SetStatus("Статус: ничего не изменено");
                StatusLabel.TextColor = Colors.Orange;
                AppendLog("[WARN] Нечего ставить: проверьте переводы и ошибки выше.");
            }
            else
            {
                SetStatus("Статус: перевод установлен");
                StatusLabel.TextColor = errors.Count == 0 && skipped.Count == 0 ? Color.FromArgb("#4CAF50") : Colors.Orange;
                AppendLog("[SUCCESS] Перевод установлен. Игра была закрыта, запустите FGO заново.");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Статус: превышено время ожидания");
            StatusLabel.TextColor = Colors.Red;
            AppendLog("[ERROR] Сервер не ответил за 20 минут.");
        }
        catch (Exception ex)
        {
            SetStatus("Статус: ошибка");
            StatusLabel.TextColor = Colors.Red;
            AppendLog($"[ERROR] {ex.Message}");
            AppendLog("[HINT] Запущен ли server.py и выполнен ли: adb reverse tcp:5000 tcp:5000");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Установка пака на ЭТОТ телефон через Shizuku (пак берётся с ПК: server.py /pack.zip, либо Download/translate_pack.zip)
    private async void OnPhoneInstallClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        SetStatus("Статус: установка на этот телефон...");
        StatusLabel.TextColor = Colors.Orange;
        try
        {
            await new PhoneInstaller(AppendLog).InstallAsync(ServerUrl);
            SetStatus("Статус: готово (см. журнал)");
            StatusLabel.TextColor = Color.FromArgb("#4CAF50");
        }
        catch (Exception ex)
        {
            SetStatus("Статус: ошибка");
            StatusLabel.TextColor = Colors.Red;
            AppendLog($"[ERROR] {ex.GetType().Name}: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    private async void OnPhoneSyncClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        try { await new PhoneInstaller(AppendLog).SyncAsync(ServerUrl); }
        catch (Exception ex) { AppendLog($"[ERROR] {ex.GetType().Name}: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void OnPhoneCheckClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        try { await new PhoneInstaller(AppendLog).CheckUpdatesAsync(ServerUrl); }
        catch (Exception ex) { AppendLog($"[ERROR] {ex.GetType().Name}: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void OnPhoneRestoreClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        try { await new PhoneInstaller(AppendLog).RestoreAsync(); }
        catch (Exception ex) { AppendLog($"[ERROR] {ex.GetType().Name}: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    // Проверка доступа через Shizuku: класс найден -> Shizuku запущен -> разрешение -> команда от имени shell
    private async void OnCheckShizukuClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        try
        {
            if (!ShizukuShell.ClassPresent())
            {
                AppendLog("[ERROR] Классы Shizuku не найдены в приложении (положите libs\\shizuku-*.jar и пересоберите).");
                return;
            }
            AppendLog("[OK] Классы Shizuku в приложении есть.");
            if (!ShizukuShell.PingBinder())
            {
                AppendLog("[WARN] Shizuku не запущен (или приложение ещё не получило связь). Запустите приложение Shizuku, затем полностью закройте и откройте этот патчер.");
                return;
            }
            AppendLog("[OK] Shizuku запущен.");
            if (ShizukuShell.CheckPermission() != 0)
            {
                AppendLog("[INFO] Разрешите доступ в окне Shizuku (30 секунд)...");
                ShizukuShell.RequestPermission(1000);
                for (int i = 0; i < 30 && ShizukuShell.CheckPermission() != 0; i++) await Task.Delay(1000);
                if (ShizukuShell.CheckPermission() != 0)
                {
                    AppendLog("[WARN] Разрешение не выдано. Нажмите кнопку ещё раз и подтвердите в окне Shizuku.");
                    return;
                }
            }
            AppendLog("[OK] Разрешение Shizuku выдано.");
            var r = await Task.Run(() => ShizukuShell.Run(
                "id; ls -l /storage/emulated/0/Android/data/com.aniplex.fategrandorder/files/data/d713 | head -5"));
            AppendLog($"[OK] shell завершился с кодом {r.Exit}:");
            foreach (var line in r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) AppendLog("   " + line.TrimEnd());
            if (r.Err.Length > 0) AppendLog($"[WARN] stderr: {r.Err}");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Shizuku: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Возвращает на эмулятор оригинальные файлы (сохранены сервером в fgo_backups)
    private async void OnRestoreClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        SetStatus("Статус: возврат оригиналов...");
        StatusLabel.TextColor = Colors.Orange;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var response = await _httpLong.GetAsync($"{ServerUrl}/restore", cts.Token);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!response.IsSuccessStatusCode)
            {
                SetStatus("Статус: ошибка возврата");
                StatusLabel.TextColor = Colors.Red;
                AppendLog($"[ERROR] {(root.TryGetProperty("error", out var er) ? er.ToString() : body)}");
                return;
            }

            var restored = Strings(root, "restored");
            SetStatus(restored.Count > 0 ? "Статус: оригиналы возвращены" : "Статус: нечего возвращать");
            StatusLabel.TextColor = restored.Count > 0 ? Color.FromArgb("#4CAF50") : Colors.Orange;
            AppendLog(restored.Count > 0
                ? $"[OK] Возвращено файлов: {restored.Count} ({string.Join(", ", restored)})"
                : "[INFO] Нечего возвращать: на устройстве нет нашей сборки (либо перевод не ставился, либо игра уже обновила файлы).");
        }
        catch (Exception ex)
        {
            SetStatus("Статус: ошибка");
            StatusLabel.TextColor = Colors.Red;
            AppendLog($"[ERROR] {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }
}
