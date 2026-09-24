#if LINUX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.Services
{
    /// <summary>
    /// Подсистема автоматического обновления (Linux/Avalonia). Запускает фоновую проверку
    /// новых версий через <see cref="GitHubReleaseService"/>, показывает диалог «Доступна новая
    /// версия» (через <see cref="IDialogService"/>) и по подтверждению скачивает self-contained
    /// single-file бинарник <c>ConfigurationManagement</c>. Установка выполняется отдельным
    /// сценарием-помощником, который дожидается завершения основного процесса, заменяет
    /// целевой исполняемый файл и при необходимости перезапускает приложение (issue #161).
    /// </summary>
    public sealed class UpdateService
    {
        /// <summary>Сколько ждать ответа на запрос прав администратора (PolicyKit).</summary>
        private static readonly TimeSpan PrivilegedUpdateTimeout = TimeSpan.FromMinutes(3);

        /// <summary>Каталог этого запуска для загрузки и временных сценариев обновления.</summary>
        private static string? _updateDirectory;

        /// <summary>Признак того, что цепочка диалогов обновления уже идёт.</summary>
        private int _updateInProgress;

        /// <summary>
        /// Возвращает каталог для скачанного бинарника и сценариев обновления, один на запуск
        /// приложения. Сценарий обновления установки в системном каталоге исполняется от имени
        /// root через pkexec, поэтому путь к нему не должен быть предсказуем и доступен на
        /// запись другим пользователям машины: иначе содержимое можно подменить между записью
        /// и запуском. <see cref="Directory.CreateTempSubdirectory"/> создаёт каталог со
        /// случайным именем и правами 0700 сразу, без промежуточного состояния и без
        /// переиспользования чужого каталога с известным именем.
        /// </summary>
        private static string EnsureUpdateDirectory()
        {
            var existing = _updateDirectory;
            if (existing is not null && Directory.Exists(existing))
                return existing;

            var dir = Directory.CreateTempSubdirectory("cm-update-").FullName;
            _updateDirectory = dir;
            return dir;
        }

        private readonly GitHubReleaseService _gitHub;
        private readonly IDialogService _dialogs;
        private readonly HttpClient _http;

        /// <summary>
        /// Флаг «автоматически обновлять приложение без подтверждения». Если включён —
        /// при обнаружении новой версии она скачивается и применяется молча (без диалога)
        /// через <see cref="DownloadAndInstallAutoAsync"/>; если выключен — показывается
        /// диалог с вопросом о применении. Значение сохраняется из настроек при старте
        /// (в App.OnFrameworkInitializationCompleted).
        /// </summary>
        public bool AutoUpdateEnabled { get; set; } = true;

        /// <summary>
        /// Ставит флаг автообновления по настройке пользователя. Вызывается при старте и
        /// при сохранении настроек: службу обслуживает и кнопка «Проверить обновления»,
        /// поэтому значение не должно отставать от настроек до перезапуска.
        /// На виртуализации и при программном рендере молчаливый авто-рестарт выглядит
        /// как «окно закрывается само через несколько секунд» после успешного запуска
        /// (issue #153), поэтому там обновление всегда идёт через вопрос пользователю.
        /// На реальном железе с рабочим GPU поведение не меняется.
        /// </summary>
        public void ApplyAutoUpdatePolicy(bool autoUpdateEnabled)
            => AutoUpdateEnabled = autoUpdateEnabled
                && !(LinuxRendering.Virtualized || LinuxRendering.SoftwareRender);

        public UpdateService(GitHubReleaseService gitHub, IDialogService dialogs)
        {
            _gitHub = gitHub;
            _dialogs = dialogs;

            _http = new HttpClient();
            // GitHub требует корректный User-Agent.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ConfigurationManagement/1.0");
            // Self-contained single-file бинарник может весить десятки МБ — таймаут больше, чем у API.
            _http.Timeout = TimeSpan.FromMinutes(20);
        }

        /// <summary>
        /// Проверяет наличие новой версии приложения. Если версия новее — показывает диалог
        /// обновления. Вызывается из фона; переход в UI-поток выполняется внутри через
        /// Dispatcher. Ошибки сети/парсинга и отображения диалога не всплывают наружу.
        /// </summary>
        public async Task CheckForUpdatesAsync()
        {
            try
            {
                var release = await _gitHub.GetLatestReleaseAsync().ConfigureAwait(false);
                if (release is null)
                    return;

                if (!GitHubReleaseService.IsNewerThan(release, VersionInfo.Display()))
                    return;

                if (AutoUpdateEnabled)
                {
                    // Автообновление включено — применяем новую версию без вопросов.
                    await DownloadAndInstallAutoAsync(release).ConfigureAwait(false);
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() => ShowUpdateDialogAsync(release));
            }
            catch
            {
                // Фоновая проверка не должна ронять приложение.
            }
        }

        /// <summary>
        /// Ручная проверка обновлений (кнопка «Проверить обновления» во вкладке «О программе»).
        /// Явно сообщает результат: ошибку проверки, «версия актуальна» или показывает диалог.
        /// </summary>
        public async Task CheckForUpdatesManualAsync()
        {
            try
            {
                var release = await _gitHub.GetLatestReleaseAsync().ConfigureAwait(false);
                if (release is null)
                {
                    ShowOnUi(() => _dialogs.ShowError(
                        LocalizationManager.T("Update.CheckFailed"),
                        LocalizationManager.T("Update.NewVersionAvailable")));
                    return;
                }

                if (!GitHubReleaseService.IsNewerThan(release, VersionInfo.Display()))
                {
                    ShowOnUi(() => _dialogs.ShowInfo(
                        LocalizationManager.T("Update.UpToDate"),
                        LocalizationManager.T("Update.NewVersionAvailable")));
                    return;
                }

                if (AutoUpdateEnabled)
                {
                    // Автообновление включено — применяем новую версию без вопросов.
                    await DownloadAndInstallAutoAsync(release).ConfigureAwait(false);
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() => ShowUpdateDialogAsync(release));
            }
            catch
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.CheckFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
            }
        }

        /// <summary>
        /// Показывает единый диалог обновления. Выбирает способ установки и ведёт
        /// пользователя по нему (issue #225):
        /// <list type="bullet">
        /// <item>запуск из пакета AppImage — обновление невозможно, показывается диалог
        /// с кликабельной ссылкой на страницу выпуска;</item>
        /// <item>single-file в каталоге пользователя (есть права записи) — обновление
        /// прямой заменой исполняемого файла;</item>
        /// <item>установка в системный каталог (например deb в /usr/bin) — запрос прав
        /// администратора через pkexec после явного согласия пользователя.</item>
        /// </list>
        /// Все ошибки обрабатываются внутри и не роняют приложение.
        /// </summary>
        private async Task ShowUpdateDialogAsync(ReleaseInfo release)
        {
            // Пока цепочка идёт, окно остаётся отзывчивым, поэтому вторую проверку
            // обновлений нужно отсекать: обе писали бы в один и тот же файл загрузки.
            if (Interlocked.CompareExchange(ref _updateInProgress, 1, 0) != 0)
                return;

            try
            {
                var target = ResolveTargetBinary();
                if (target is null)
                {
                    ShowOnUi(() => _dialogs.ShowError(
                        LocalizationManager.T("Update.InstallFailed"),
                        LocalizationManager.T("Update.NewVersionAvailable")));
                    return;
                }

                // Запуск из пакета AppImage самообновлению не поддаётся: исполняемый файл
                // лежит внутри разового монтирования, доступного только на чтение.
                if (IsRunningFromAppImage(target))
                {
                    ShowManualUpdateDialog(
                        LocalizationManager.T("Update.PackageManualUpdate"), release.HtmlUrl);
                    return;
                }

                // Single-file в каталоге пользователя — прав на запись достаточно,
                // обновляем заменой исполняемого файла напрямую.
                if (IsDirectoryWritable(Path.GetDirectoryName(target)))
                {
                    await ShowSelfUpdateDialogAsync(release, target);
                    return;
                }

                // Каталог не на запись: вероятно, установка через deb в системный каталог.
                // Для замены нужны права администратора — предлагаем обновление через pkexec.
                await ShowPrivilegedUpdateDialogAsync(release, target);
            }
            catch
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.InstallFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
            }
            finally
            {
                Interlocked.Exchange(ref _updateInProgress, 0);
            }
        }

        /// <summary>
        /// Проводит обновление single-file в каталоге пользователя: спрашивает подтверждение
        /// скачивания, скачивает бинарник, затем предлагает применить обновление
        /// (перезапустить сейчас или после закрытия).
        /// </summary>
        private async Task ShowSelfUpdateDialogAsync(ReleaseInfo release, string target)
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.NoDownloadUrl"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            // Спрашиваем разрешение до скачивания: отказ прекращает обновление целиком.
            var current = string.Format(
                LocalizationManager.T("Update.CurrentVersion"), VersionInfo.Display());
            var offered = string.Format(
                LocalizationManager.T("Update.NewVersion"), NormalizeTag(release.TagName));
            var summary = current + Environment.NewLine + offered
                + Environment.NewLine + Environment.NewLine
                + LocalizationManager.T("Update.DownloadPrompt");
            var accepted = ConfirmUpdate(summary, release.Body);
            if (!accepted)
                return;

            // Скачиваем новый бинарник, не блокируя поток интерфейса: диалог показан
            // из UI-потока, и синхронное ожидание здесь замораживало окно на всё время
            // загрузки (десятки МБ).
            var newBinary = await DownloadWithProgressAsync(release.DownloadUrl!)
                .ConfigureAwait(true);
            if (newBinary is null)
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.DownloadFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            // Спрашиваем, как применить обновление: перезапустить сейчас или после закрытия.
            var restartNow = _dialogs.Confirm(
                LocalizationManager.T("Update.RestartNowPrompt"),
                LocalizationManager.T("Update.NewVersionAvailable"));

            if (restartNow)
            {
                if (!ApplyRestartNow(target, newBinary))
                {
                    ShowOnUi(() => _dialogs.ShowError(
                        LocalizationManager.T("Update.InstallFailed"),
                        LocalizationManager.T("Update.NewVersionAvailable")));
                    return;
                }
                // Помощник запущен — закрываем приложение, чтобы замена прошла после выхода.
                ShutdownNow();
            }
            else
            {
                // Обновление применится при следующем естественном закрытии приложения.
                if (!ApplyAfterClose(target, newBinary))
                {
                    ShowOnUi(() => _dialogs.ShowError(
                        LocalizationManager.T("Update.InstallFailed"),
                        LocalizationManager.T("Update.NewVersionAvailable")));
                    return;
                }
                ShowOnUi(() => _dialogs.ShowInfo(
                    LocalizationManager.T("Update.WillApplyOnExit"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
            }
        }

        /// <summary>
        /// Обновляет установку в системном каталоге (например deb в /usr/bin), где без прав
        /// администратора заменить исполняемый файл нельзя. Сначала запрашивает явное согласие
        /// пользователя на повышение прав (пароль в терминале из GUI не запрашивается без
        /// согласия), затем скачивает бинарник и запускает замену через pkexec. При отказе
        /// от повышения прав показывается запасной диалог с кликабельной ссылкой на страницу
        /// выпуска (issue #225).
        /// </summary>
        private async Task ShowPrivilegedUpdateDialogAsync(ReleaseInfo release, string target)
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.NoDownloadUrl"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            // dpkg -S ждёт до 15 секунд, поэтому спрашиваем не на потоке интерфейса.
            var isDeb = await Task.Run(() => IsDebPackage(target)).ConfigureAwait(true);
            var prompt = isDeb
                ? LocalizationManager.T("Update.AdminPromptDeb")
                : LocalizationManager.T("Update.AdminPromptGeneric");

            // Повышение прав запускаем только после явного согласия пользователя.
            var accepted = ConfirmUpdate(prompt, release.Body);
            if (!accepted)
            {
                // Отказ — запасной вариант: ссылка на страницу выпуска для ручного обновления.
                ShowManualUpdateDialog(
                    LocalizationManager.T("Update.TargetNotWritable"), release.HtmlUrl);
                return;
            }

            var newBinary = await DownloadWithProgressAsync(release.DownloadUrl!)
                .ConfigureAwait(true);
            if (newBinary is null)
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.DownloadFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            // Ждём, пока помощник действительно получит права: пока пользователь не ответил
            // на запрос PolicyKit, закрывать приложение нельзя. При отказе или недоступности
            // pkexec приложение остаётся работать и показывает ручной путь обновления.
            if (!await ApplyPrivilegedUpdateAsync(target, newBinary).ConfigureAwait(true))
            {
                ShowManualUpdateDialog(
                    LocalizationManager.T("Update.TargetNotWritable"), release.HtmlUrl);
                return;
            }

            // Помощник получил права и ждёт нашего выхода — закрываемся, чтобы он заменил бинарник.
            ShutdownNow();
        }

        /// <summary>
        /// Возвращает текст объяснения, почему самозамена невозможна, или <c>null</c>,
        /// если приложение вправе заменить свой исполняемый файл. Проверяются два
        /// случая: запуск из пакета AppImage и установка в каталог, недоступный
        /// пользователю на запись (так лежит бинарник из deb-пакета, в <c>/usr/bin</c>).
        /// </summary>
        private static string? GetSelfUpdateBlocker(string target)
        {
            if (IsRunningFromAppImage(target))
                return LocalizationManager.T("Update.PackageManualUpdate");

            return IsDirectoryWritable(Path.GetDirectoryName(target))
                ? null
                : LocalizationManager.T("Update.TargetNotWritable");
        }

        /// <summary>
        /// Признак запуска из пакета AppImage: переменные <c>APPIMAGE</c> и <c>APPDIR</c>
        /// выставляет сам пакет, а исполняемый файл лежит внутри разового монтирования,
        /// доступного только на чтение. Одной переменной мало: её наследует любой
        /// дочерний процесс, запущенный из пакета, поэтому дополнительно проверяется,
        /// что текущий исполняемый файл действительно находится внутри <c>APPDIR</c>.
        /// Заменять сам пакет скачанным бинарником нельзя: обёртка AppImage теряется.
        /// </summary>
        private static bool IsRunningFromAppImage(string target)
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            var appDir = Environment.GetEnvironmentVariable("APPDIR");
            if (string.IsNullOrWhiteSpace(appImage) || string.IsNullOrWhiteSpace(appDir))
                return false;

            try
            {
                if (!File.Exists(appImage))
                    return false;

                var mount = Path.GetFullPath(appDir);
                if (!mount.EndsWith(Path.DirectorySeparatorChar))
                    mount += Path.DirectorySeparatorChar;

                return Path.GetFullPath(target).StartsWith(mount, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Проверяет каталог на запись созданием и удалением временного файла. Замена
        /// исполняемого файла идёт переименованием внутри его каталога, поэтому прав
        /// на сам файл недостаточно, нужны права на каталог.
        /// </summary>
        private static bool IsDirectoryWritable(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            var probe = Path.Combine(directory, $".cm-update-probe-{Guid.NewGuid():N}");
            try
            {
                using (File.Create(probe))
                {
                }
                File.Delete(probe);
                return true;
            }
            catch
            {
                TryDelete(probe);
                return false;
            }
        }

        /// <summary>Обрезает ведущий символ «v» у тега версии для отображения.</summary>
        private static string NormalizeTag(string tag) =>
            !string.IsNullOrEmpty(tag) && (tag[0] == 'v' || tag[0] == 'V') ? tag.Substring(1) : tag;

    /// <summary>
    /// Скачивает новый бинарник и сразу применяет обновление режимом «Перезапустить
    /// сейчас», не задавая пользователю вопросов. Используется, когда включено
    /// автообновление (<see cref="AutoUpdateEnabled"/>), — аналог <c>DownloadAndInstallAutoAsync</c>
    /// Windows-версии. Все ошибки обрабатываются внутри и не роняют приложение.
    /// </summary>
    public async Task DownloadAndInstallAutoAsync(ReleaseInfo release)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.NoDownloadUrl"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            var target = ResolveTargetBinary();
            if (target is null)
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.InstallFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            // Способ установки проверяется до скачивания, как и в режиме с вопросом
            // (issue #153). Без этой проверки автообновление в пакетной установке
            // (deb в /usr/bin, AppImage) доходило до запуска помощника и закрывало
            // приложение, а заменить файл помощник не мог: каталог не на запись.
            // Со стороны пользователя это выглядело как самопроизвольный выход
            // через несколько секунд после запуска.
            var autoBlocker = GetSelfUpdateBlocker(target);
            if (autoBlocker is not null)
            {
                // Самообновление недоступно: показываем понятный диалог с кликабельной
                // ссылкой на страницу выпуска вместо молчаливого завершения (issue #225).
                ShowManualUpdateDialog(autoBlocker, release.HtmlUrl);
                return;
            }

            var newBinary = await DownloadWithProgressAsync(release.DownloadUrl!);
            if (newBinary is null)
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.DownloadFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            // Автоматический режим всегда применяет обновление с перезапуском «сейчас».
            if (!ApplyRestartNow(target, newBinary))
            {
                ShowOnUi(() => _dialogs.ShowError(
                    LocalizationManager.T("Update.InstallFailed"),
                    LocalizationManager.T("Update.NewVersionAvailable")));
                return;
            }

            // Помощник запущен — закрываем приложение, чтобы замена прошла после выхода.
            ShutdownNow();
        }
        catch
        {
            ShowOnUi(() => _dialogs.ShowError(
                LocalizationManager.T("Update.InstallFailed"),
                LocalizationManager.T("Update.NewVersionAvailable")));
        }
    }

        /// <summary>
        /// Путь к журналу сценария-помощника. Лежит рядом с <c>errors.log</c>, потому что
        /// помощник работает уже после выхода приложения: свой вывод он отдать некому,
        /// его каналы закрыты вместе с родительским процессом, и при неудачной замене
        /// от него не остаётся ни строки (issue #225). Журнал подрезается, когда
        /// перерастает порог очистки: запись ведётся при каждом обновлении.
        /// </summary>
        private static string EnsureUpdaterLogPath()
        {
            const long maxLogBytes = 512 * 1024;
            var dir = Configuration_Management.Services.PlatformPaths.AppDataDirectory;

            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "update-helper.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > maxLogBytes)
                    TryDelete(path);

                return path;
            }
            catch
            {
                // Каталог данных недоступен: пишем рядом со сценарием, лишь бы не молча.
                return Path.Combine(EnsureUpdateDirectory(), "update-helper.log");
            }
        }

        /// <summary>
        /// Записывает текст сценария-помощника, приводя переводы строк к виду, который
        /// понимает <c>bash</c>. Текст сценария лежит в исходнике буквальной строкой,
        /// поэтому переводы строк попадают в него прямо из файла исходного кода: если
        /// рабочая копия выгружена на Windows (autocrlf), сценарий получает CRLF, и
        /// каждая строка кончается лишним символом. Bash принимает его за часть команды:
        /// <c>set -u</c> отвергается с подсказкой по использованию, следующая команда
        /// не находится, сценарий выходит с кодом 2 и не заменяет исполняемый файл.
        /// Со стороны пользователя это выглядит так, что приложение закрылось и ничего
        /// не произошло (issue #225).
        /// </summary>
        private static void WriteShellScript(string scriptPath, string script)
        {
            File.WriteAllText(scriptPath, script.Replace("\r\n", "\n"));
        }

        /// <summary>Возвращает путь к текущему исполняемому файлу приложения или null.</summary>
        internal string? ResolveTargetBinary()
        {
            var target = Environment.ProcessPath
                         ?? Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }

        /// <summary>
        /// Скачивает новый бинарник, показывая на это время окно хода загрузки. Размер
        /// файла составляет десятки МБ, и без индикатора отрезок между согласием на
        /// обновление и вопросом о перезапуске выглядит как зависание приложения
        /// (issue #225). В Windows-версии тот же этап показан полосой прогресса
        /// в едином диалоге обновления (<c>UpdateAvailableWindow</c>).
        /// </summary>
        private async Task<string?> DownloadWithProgressAsync(string url)
        {
            UpdateProgressWindowAvalonia? window = null;
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    window = new UpdateProgressWindowAvalonia();
                    window.Show();
                });
            }
            catch
            {
                // Окно индикатора не должно мешать самому обновлению, но закрыть его
                // всё равно нужно: платформенное окно создаётся конструктором, и сбой
                // мог прийти уже из показа.
            }

            try
            {
                return await DownloadNewBinaryAsync(url, window).ConfigureAwait(true);
            }
            finally
            {
                var closing = window;
                if (closing is not null)
                {
                    // С потока интерфейса окно закрывается сразу, а не отложенно: иначе
                    // следующий за загрузкой вопрос успевает открыться поверх ещё живого
                    // окна прогресса, становится его дочерним, и закрытие прогресса гасит
                    // вопрос вместо пользователя (ответ читается как отказ). Проверено
                    // прогоном: вопрос о перезапуске снимался сам.
                    if (Dispatcher.UIThread.CheckAccess())
                        closing.Close();
                    else
                        // С фонового потока ждать нельзя: при закрытии приложения во время
                        // загрузки цикл сообщений уже остановлен, и ожидание не завершится.
                        Dispatcher.UIThread.Post(() => closing.Close());
                }
            }
        }

        /// <summary>
        /// Скачивает новый бинарник по прямой ссылке во временный каталог. Возвращает путь
        /// к файлу или null при сетевой ошибке / пустом файле. Временный файл удаляется при неудаче.
        /// О ходе загрузки сообщается окну <paramref name="progress"/>, если оно показано.
        /// Сначала пробуется многопоточная загрузка (issue #284), при любом сбое —
        /// переход к однопоточной загрузке ниже.
        /// </summary>
        private async Task<string?> DownloadNewBinaryAsync(
            string url, UpdateProgressWindowAvalonia? progress = null)
        {
            var dir = EnsureUpdateDirectory();
            var dest = Path.Combine(dir, "ConfigurationManagement.new");

            try
            {
                // Многопоточная загрузка по HTTP Range (N сегментов): файл делится на части,
                // каждая скачивается отдельным соединением, затем части склеиваются. Прогресс
                // агрегированный (сумма по сегментам / общий размер), публикуется не чаще раза
                // на процент. При сбое (сервер без Range, неизвестный размер, ошибка сегмента) —
                // fallback на однопоточный путь ниже.
                var parallelPath = await ParallelDownloader.TryDownloadAsync(
                    _http, url, dest, p => progress?.SetProgress(p)).ConfigureAwait(false);
                if (parallelPath is not null)
                    return parallelPath;

                // ResponseHeadersRead: тело пишется на диск потоком, а не буферизуется
                // целиком в памяти (бинарник весит десятки МБ). При этом HttpClient.Timeout
                // перестаёт покрывать чтение тела, поэтому срок задаётся здесь явно, иначе
                // залипшее соединение висело бы вместо честной ошибки загрузки.
                using var cancellation = new CancellationTokenSource(_http.Timeout);
                using var response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content
                    .ReadAsStreamAsync(cancellation.Token)
                    .ConfigureAwait(false);
                // Общий размер сервер сообщает не всегда: без него доля неизвестна,
                // и окно показывает бегущую полосу вместо процентов.
                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                await using (var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long readTotal = 0;
                    var lastPercent = -1;
                    progress?.SetProgress(totalBytes > 0 ? 0 : -1);

                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellation.Token)
                            .ConfigureAwait(false);
                        readTotal += read;

                        if (progress is null || totalBytes <= 0)
                            continue;

                        // Отчёт только на смене целого процента: иначе на каждый блок
                        // в 80 КБ приходилась бы отправка в поток интерфейса.
                        var percent = (int)Math.Min(100, readTotal * 100 / totalBytes);
                        if (percent == lastPercent)
                            continue;

                        lastPercent = percent;
                        progress.SetProgress(percent);
                    }

                    await target.FlushAsync(cancellation.Token).ConfigureAwait(false);
                }

                // Размер теперь известен, поэтому обрыв, не бросивший исключение, ловится
                // здесь: недокачанный бинарник не должен подставляться вместо рабочего.
                // Так же принимает файл Windows-версия (size >= totalBytes).
                var size = new FileInfo(dest).Length;
                if (size <= 0 || (totalBytes > 0 && size < totalBytes))
                {
                    TryDelete(dest);
                    return null;
                }

                return dest;
            }
            catch
            {
                TryDelete(dest);
                return null;
            }
        }

        /// <summary>Применяет обновление режимом «Перезапустить сейчас». Возвращает true при успехе.</summary>
        internal bool ApplyRestartNow(string target, string newBinary)
        {
            var script = CreateUpdaterScript(target, newBinary, Environment.ProcessId, restart: true);
            return LaunchUpdater(script);
        }

        /// <summary>Применяет обновление режимом «Обновить после закрытия». Возвращает true при успехе.</summary>
        internal bool ApplyAfterClose(string target, string newBinary)
        {
            var script = CreateUpdaterScript(target, newBinary, Environment.ProcessId, restart: false);
            return LaunchUpdater(script);
        }

        /// <summary>
        /// Применяет обновление установки в системном каталоге с повышением прав: создаёт
        /// временный bash-сценарий и запускает его через <c>pkexec</c>. Возвращает true,
        /// если процесс повышения прав удалось запустить (graphical PolicyKit-диалог).
        /// </summary>
        internal async Task<bool> ApplyPrivilegedUpdateAsync(string target, string newBinary)
        {
            var readyMarker = Path.Combine(
                EnsureUpdateDirectory(), $"priv-ready-{Guid.NewGuid():N}");
            var script = CreatePrivilegedUpdaterScript(
                target, newBinary, Environment.ProcessId, readyMarker);

            using var process = LaunchPrivilegedUpdater(script);
            if (process is null)
            {
                CleanUpdateLeftovers(script, newBinary);
                return false;
            }

            try
            {
                var granted = await WaitForPrivilegedUpdaterAsync(process, readyMarker)
                    .ConfigureAwait(true);
                if (!granted)
                    CleanUpdateLeftovers(script, newBinary);
                return granted;
            }
            finally
            {
                TryDelete(readyMarker);
            }
        }

        /// <summary>Удаляет временные файлы обновления, если помощник до них не добрался.</summary>
        private static void CleanUpdateLeftovers(string script, string newBinary)
        {
            TryDelete(script);
            TryDelete(newBinary);

            try
            {
                // Каталог этого запуска убирается, только если в нём больше ничего нет.
                var dir = Path.GetDirectoryName(script);
                if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    if (string.Equals(dir, _updateDirectory, StringComparison.Ordinal))
                        _updateDirectory = null;
                }
            }
            catch
            {
                // Пустой каталог в %TEMP% не мешает работе.
            }
        }

        /// <summary>
        /// Дожидается ответа пользователя на запрос PolicyKit. Помощник, получив права,
        /// первым делом создаёт файл-маркер: его появление означает, что пароль принят
        /// и сценарий работает. Дальше помощник ждёт нашего выхода, поэтому дожидаться
        /// завершения самого <c>pkexec</c> нельзя — это взаимная блокировка. Возвращает
        /// false, если пользователь отказался (pkexec вышел с ненулевым кодом) или ответа
        /// не было дольше <see cref="PrivilegedUpdateTimeout"/>.
        /// </summary>
        private static async Task<bool> WaitForPrivilegedUpdaterAsync(Process process, string readyMarker)
        {
            // Отсчёт по Stopwatch, а не по часам: перевод системного времени не должен
            // ни обрывать ожидание, ни продлевать его.
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < PrivilegedUpdateTimeout)
            {
                if (File.Exists(readyMarker))
                    return true;

                if (process.HasExited)
                {
                    // Помощник, получивший права, живёт до нашего выхода. Ранний выход —
                    // это отказ в PolicyKit (код 126), сбой запуска (127) или ошибка сценария.
                    return File.Exists(readyMarker);
                }

                await Task.Delay(200).ConfigureAwait(true);
            }

            // Маркер мог появиться в последнюю паузу: проверяем ещё раз, иначе снятый запрос
            // разошёлся бы с уже работающим помощником.
            if (File.Exists(readyMarker))
                return true;

            // Ответа так и не было: снимаем запрос, чтобы диалог пароля не остался висеть,
            // и дожидаемся конца, потому что Kill возвращается раньше завершения процесса.
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)
                    .ConfigureAwait(true);
            }
            catch { /* процесс мог завершиться сам, ждать больше нечего */ }

            return File.Exists(readyMarker);
        }

        /// <summary>Закрывает текущее приложение (вызывается после успешного запуска помощника).</summary>
        internal void ShutdownNow()
        {
            // Диагностика issue #153: фиксируем, что закрытие окна — это применение обновления,
            // а не сбой. Если в логе появилась эта запись и следом managed exit из App — значит
            // «самозакрытие» вызвано автообновлением, а не рендером/вводом окружения.
            try
            {
                var note =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Обновление: помощник запущен, закрываем приложение для перезапуска.";
                Console.WriteLine(note);
                var dataDir = Configuration_Management.Services.PlatformPaths.AppDataDirectory;
                Directory.CreateDirectory(dataDir);
                File.AppendAllText(Path.Combine(dataDir, "errors.log"), note + Environment.NewLine);
            }
            catch { /* логирование не должно мешать закрытию */ }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        desktop.Shutdown();
                }
                catch
                {
                    // При любом сбое завершаем процесс явно, чтобы помощник мог заменить бинарник.
                    Environment.Exit(0);
                }
            });
        }

        /// <summary>
        /// Создаёт временный bash-сценарий, который дожидается завершения основного процесса
        /// (по PID), заменяет текущий исполняемый файл скачанным (chmod +x) и удаляет сам скрипт.
        /// Если <paramref name="restart"/> равен true — после замены перезапускает приложение.
        /// Возвращает путь к созданному скрипту.
        /// <para>
        /// Замена идёт переименованием внутри каталога цели, а не копированием поверх:
        /// <c>cp</c> при занятом файле удаляет его и создаёт заново, поэтому обрыв на
        /// середине оставил бы обрезанный исполняемый файл. Ожидание завершения процесса
        /// в режиме «после закрытия» безусловное, как в версии для Windows: обещание
        /// «применится при закрытии» не должно нарушаться по истечении таймаута.
        /// </para>
        /// </summary>
        private static string CreateUpdaterScript(string target, string newBinary, int currentPid, bool restart)
        {
            var logPath = EnsureUpdaterLogPath();
            var scriptPath = Path.Combine(
                EnsureUpdateDirectory(), $"apply-update-{Guid.NewGuid():N}.sh");

            // Пустое тело if недопустимо в bash, поэтому в режиме «после закрытия»
            // подставляется команда-заглушка, а не один комментарий.
            var relaunchBlock = restart
                ? "nohup \"$TARGET\" >/dev/null 2>&1 &"
                : ": # Перезапуск не требуется, обновление применится при следующем закрытии.";

            // Предел ожидания задаётся только режиму «перезапустить сейчас»: там
            // пользователь ждёт приложение обратно. В режиме «после закрытия» помощник
            // ждёт столько, сколько работает приложение.
            var waitCondition = restart
                ? "kill -0 \"$PID_TARGET\" 2>/dev/null && [ $i -lt 300 ]"
                : "kill -0 \"$PID_TARGET\" 2>/dev/null";

            var script = $@"#!/usr/bin/env bash
set -u
TARGET='{Bq(target)}'
NEW='{Bq(newBinary)}'
STAGED=""$TARGET.cm-update-$$""
PID_TARGET={currentPid}
RESTART={(restart ? 1 : 0)}
LOG='{Bq(logPath)}'

# Весь вывод уходит в журнал: приложение к этому моменту закрыто, его каналы
# закрыты вместе с ним, и без журнала неудачная замена не оставляет следов.
# Если журнал открыть не удалось, вывод уводится в никуда, и это обязательно:
# унаследованные потоки ведут в трубу закрывшегося приложения, и первая же
# запись в неё убила бы помощника сигналом PIPE до замены файла.
if ! exec >>""$LOG"" 2>&1; then
  exec >/dev/null 2>&1
else
  # Права журнала не должны зависеть от umask сборки: в нём пути пользователя.
  chmod 600 ""$LOG"" 2>/dev/null || true
fi

log() {{ echo ""[$(date '+%Y-%m-%d %H:%M:%S')] $*""; }}

log ""=== помощник обновления, pid $$, режим RESTART=$RESTART""
log ""цель: $TARGET""
log ""новый файл: $NEW, размер $(stat -c%s ""$NEW"" 2>/dev/null || echo '?') байт""
FREE_KB=$(df -Pk ""$(dirname ""$TARGET"")"" 2>/dev/null | awk 'NR==2 {{print $4}}')
log ""свободно в каталоге цели: ${{FREE_KB:-?}} КБ""

# Ожидание завершения основного процесса, чтобы не было гонки при замене файла.
i=0
while {waitCondition}; do
  sleep 1
  i=$((i+1))
done
if kill -0 ""$PID_TARGET"" 2>/dev/null; then
  log ""предупреждение: процесс $PID_TARGET всё ещё работает после $i с, продолжаем замену""
else
  log ""процесс $PID_TARGET завершился, ожидание заняло $i с""
fi
sleep 1

# Замена в два шага: сначала копия рядом с целью, затем атомарное переименование.
# Так недокачанный или недокопированный файл никогда не окажется на месте рабочего.
if ! cp -f ""$NEW"" ""$STAGED""; then
  log ""ошибка: не удалось скопировать новый файл в $STAGED""
  rm -f ""$STAGED""
  exit 1
fi
if ! chmod +x ""$STAGED""; then
  log ""ошибка: не удалось выставить признак исполняемого для $STAGED""
  rm -f ""$STAGED""
  exit 1
fi
if ! mv -f ""$STAGED"" ""$TARGET""; then
  log ""ошибка: не удалось переименовать $STAGED в $TARGET""
  rm -f ""$STAGED""
  exit 1
fi
log ""замена файла выполнена""

# Перезапуск приложения (только по явному запросу пользователя).
if [ ""$RESTART"" = ""1"" ]; then
  {relaunchBlock}
  NEW_PID=$!
  sleep 1
  if kill -0 ""$NEW_PID"" 2>/dev/null; then
    log ""перезапуск: процесс $NEW_PID работает""
  else
    wait ""$NEW_PID""
    log ""ошибка: перезапущенный процесс завершился с кодом $?""
  fi
fi

# Убираем временный бинарник, сам скрипт и опустевший каталог обновления.
WORK_DIR=""$(dirname ""$0"")""
rm -f ""$NEW""
log ""=== помощник закончил работу""
rm -f ""$0""
# Запасной журнал лежит в рабочем каталоге и после успеха не нужен: иначе
# каталог не удаляется и копится по одному на каждое обновление.
case ""$LOG"" in
  ""$WORK_DIR""/*) rm -f ""$LOG"" ;;
esac
rmdir ""$WORK_DIR"" 2>/dev/null || true
";

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            WriteShellScript(scriptPath, script);
            return scriptPath;
        }

        /// <summary>
        /// Запускает временный bash-помощник detached, без ожидания его завершения.
        /// Возвращает true, если процесс удалось запустить.
        /// </summary>
        private static bool LaunchUpdater(string scriptPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
                // Запускаем bash на сценарии; CreateNoWindow уводит процесс с терминала,
                // поэтому он продолжит работу и после выхода основного процесса.
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/env",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("bash");
                psi.ArgumentList.Add(scriptPath);
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Определяет, установлен ли текущий исполняемый файл из deb-пакета. Спрашивает у
        /// <c>dpkg -S</c>, какому пакету принадлежит файл: exit code 0 означает, что файл
        /// отслеживается системным пакетным менеджером. Если dpkg недоступен или файл не
        /// принадлежит пакету — возвращает false.
        /// </summary>
        private static bool IsDebPackage(string target)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dpkg",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-S");
                psi.ArgumentList.Add(target);

                using var process = Process.Start(psi);
                if (process is null)
                    return false;

                // dpkg -S завершается нулём только если файл принадлежит установленному пакету.
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { /* процесс мог завершиться сам */ }
                    return false;
                }
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Создаёт временный bash-сценарий для обновления установки в системном каталоге с
        /// повышением прав. Дожидается завершения основного процесса (по PID), заменяет
        /// исполняемый файл скачанным и перезапускает приложение. В отличие от
        /// <see cref="CreateUpdaterScript"/>, запускается через pkexec (root), поэтому
        /// перезапуск выполняется от имени обычного пользователя через <c>runuser</c>,
        /// чтобы приложение не осталось работать под правами администратора.
        /// </summary>
        private static string CreatePrivilegedUpdaterScript(
            string target, string newBinary, int currentPid, string readyMarker)
        {
            var scriptPath = Path.Combine(
                EnsureUpdateDirectory(), $"apply-update-priv-{Guid.NewGuid():N}.sh");

            // Имя обычного пользователя для перезапуска после замены под root.
            var userName = Bq(Environment.UserName);

            // Сценарий работает от root, а runuser не наследует окружение сеанса.
            // Без переменных графического сеанса перезапущенное приложение не найдёт
            // дисплей и молча закроется, то есть обновление пройдёт, а окно не вернётся.
            var sessionEnvironment = BuildRestartEnvironment();

            var script = $@"#!/usr/bin/env bash
set -u
TARGET='{Bq(target)}'
NEW='{Bq(newBinary)}'
STAGED=""$TARGET.cm-update-$$""
PID_TARGET={currentPid}
USER_NAME='{userName}'
RESTART_ENV=({sessionEnvironment})
READY='{Bq(readyMarker)}'

# Замена готовится до отметки: копия рядом с целью доказывает, что права получены
# и записать в целевой каталог удалось. Только после этого приложению разрешается
# закрыться, иначе оно закрывалось бы навстречу обновлению, которое не состоится.
if ! cp -f ""$NEW"" ""$STAGED""; then
  rm -f ""$STAGED""
  exit 1
fi
if ! chmod +x ""$STAGED""; then
  rm -f ""$STAGED""
  exit 1
fi
if ! : > ""$READY""; then
  rm -f ""$STAGED""
  exit 1
fi

# Ожидание завершения основного процесса, чтобы не было гонки при замене файла.
i=0
while kill -0 ""$PID_TARGET"" 2>/dev/null && [ $i -lt 300 ]; do
  sleep 1
  i=$((i+1))
done
sleep 1

# Приложение вышло — остаётся атомарное переименование подготовленной копии.
if ! mv -f ""$STAGED"" ""$TARGET""; then
  rm -f ""$STAGED""
  exit 1
fi

# Перезапуск приложения от имени обычного пользователя, а не root: скрипт работает
# с правами администратора, а приложение должно вернуться к обычным пользователям.
if [ -n ""$USER_NAME"" ] && command -v runuser >/dev/null 2>&1; then
  runuser -u ""$USER_NAME"" -- env ""${{RESTART_ENV[@]}}"" nohup ""$TARGET"" >/dev/null 2>&1 &
else
  nohup ""$TARGET"" >/dev/null 2>&1 &
fi

# Убираем временный бинарник, сам скрипт и опустевший каталог обновления.
WORK_DIR=""$(dirname ""$0"")""
rm -f ""$NEW""
rm -f ""$0""
rmdir ""$WORK_DIR"" 2>/dev/null || true
";

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            WriteShellScript(scriptPath, script);
            return scriptPath;
        }

        /// <summary>
        /// Запускает сценарий обновления с повышением прав через <c>pkexec env bash</c>.
        /// pkexec показывает графический диалог PolicyKit для ввода пароля администратора;
        /// пароль в терминале из GUI не запрашивается. Возвращает true, если процесс удалось
        /// запустить. Если pkexec недоступен — возвращает false (обновление недоступно).
        /// </summary>
        private static Process? LaunchPrivilegedUpdater(string scriptPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
                // «env» обязателен: политика pkexec по умолчанию разрешает выполнение
                // только /usr/bin/env, что и используется как обходной путь для запуска
                // произвольной команды с правами администратора.
                var psi = new ProcessStartInfo
                {
                    FileName = "pkexec",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("env");
                psi.ArgumentList.Add("bash");
                psi.ArgumentList.Add(scriptPath);
                return Process.Start(psi);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Собирает присваивания переменных графического сеанса для перезапуска приложения
        /// после обновления с правами администратора. Пустые значения пропускаются.
        /// </summary>
        private static string BuildRestartEnvironment()
        {
            string[] names =
            [
                "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY",
                "XDG_RUNTIME_DIR", "XDG_SESSION_TYPE", "DBUS_SESSION_BUS_ADDRESS"
            ];

            var assignments = new List<string>();
            foreach (var name in names)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                    assignments.Add($"{name}='{Bq(value)}'");
            }

            return string.Join(' ', assignments);
        }

        /// <summary>Экранирует строку для одинарных кавычек bash: ' → '\''.</summary>
        private static string Bq(string value) => value.Replace("'", "'\\''");

        /// <summary>
        /// Запрашивает подтверждение скачивания/применения обновления. На Avalonia
        /// показывает также описание релиза «Что нового» в формате markdown.
        /// </summary>
        private bool ConfirmUpdate(string summary, string? body)
        {
            var title = LocalizationManager.T("Update.NewVersionAvailable");
            return _dialogs is AvaloniaDialogService avalonia
                ? avalonia.ConfirmUpdate(summary, body, title)
                : _dialogs.Confirm(summary, title);
        }

        /// <summary>
        /// Показывает диалог «самообновление недоступно» с кликабельной ссылкой на
        /// страницу выпуска (issue #225). Ссылка открывается в браузере по умолчанию.
        /// Запасной путь для диалоговой службы без поддержки ссылок — обычное сообщение
        /// с текстовым адресом страницы.
        /// </summary>
        private void ShowManualUpdateDialog(string message, string? releaseUrl)
        {
            ShowOnUi(() =>
            {
                if (_dialogs is AvaloniaDialogService avalonia)
                {
                    avalonia.ShowManualUpdate(
                        message, releaseUrl ?? string.Empty,
                        LocalizationManager.T("Update.NewVersionAvailable"));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(releaseUrl))
                    message += Environment.NewLine + Environment.NewLine + releaseUrl;
                _dialogs.ShowInfo(message, LocalizationManager.T("Update.NewVersionAvailable"));
            });
        }

        /// <summary>Выполняет действие в UI-потоке, если вызывающий поток — не UI.</summary>
        private static void ShowOnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
                return;
            }
            Dispatcher.UIThread.InvokeAsync(action);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Не критично — временный файл останется в %TEMP%.
            }
        }
    }
}
#endif