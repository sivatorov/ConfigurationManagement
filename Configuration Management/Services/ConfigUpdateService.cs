using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Configuration_Management.Localization;
using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Реализация <see cref="IConfigUpdateService"/>. Загрузка новой конфигурации из файла .cf
/// и обновление конфигурации БД выполняется пакетным запуском конфигуратора
/// (<see cref="OneCLauncher.DesignerBatchOperation.LoadCfg"/>), ожидание завершения —
/// через событие <see cref="OneCLauncher.DesignerBatchCompleted"/> (TaskCompletionSource с таймаутом).
/// </summary>
public class ConfigUpdateService : IConfigUpdateService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(60);

    private readonly IAppLogger? _logger;

    public ConfigUpdateService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BackupRunResult> UpdateConfigAsync(Infobase infobase, string cfgFilePath, BackupCredential? credential = null)
    {
        var result = new BackupRunResult();
        if (infobase is null)
        {
            result.ErrorMessage = LocalizationManager.T("Schedule.ConfigUpdateFailed");
            return result;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(cfgFilePath) || !File.Exists(cfgFilePath))
            {
                result.ErrorMessage = LocalizationManager.T("Restore.FileNotFound");
                return result;
            }

            var started = OneCLauncher.RunDesignerBatch(
                infobase, OneCLauncher.DesignerBatchOperation.LoadCfg, cfgFilePath, credential);
            if (!started)
            {
                result.ErrorMessage = LocalizationManager.T("Schedule.ConfigUpdateStartFailed");
                return result;
            }

            var info = await WaitForCompletionAsync(OneCLauncher.DesignerBatchOperation.LoadCfg, cfgFilePath);
            if (info is null || !info.Success)
            {
                result.ErrorMessage = info?.ErrorMessage ?? LocalizationManager.T("Restore.Timeout");
                return result;
            }

            result.Success = true;
            result.CreatedFiles = new[] { cfgFilePath };
            return result;
        }
        catch (Exception ex)
        {
            _logger?.Error("Ошибка обновления конфигурации информационной базы", ex);
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Ожидает завершения конкретной пакетной операции DESIGNER (по операции и пути файла)
    /// через событие <see cref="OneCLauncher.DesignerBatchCompleted"/> с таймаутом.
    /// </summary>
    private async Task<OneCLauncher.DesignerBatchInfo?> WaitForCompletionAsync(
        OneCLauncher.DesignerBatchOperation operation, string outputPath)
    {
        using var cts = new CancellationTokenSource(DefaultTimeout);
        var tcs = new TaskCompletionSource<OneCLauncher.DesignerBatchInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<OneCLauncher.DesignerBatchInfo> handler = (_, info) =>
        {
            if (info.Operation == operation &&
                string.Equals(info.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(info);
            }
        };
        OneCLauncher.DesignerBatchCompleted += handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
            return completed == tcs.Task ? tcs.Task.Result : null;
        }
        finally
        {
            OneCLauncher.DesignerBatchCompleted -= handler;
        }
    }
}