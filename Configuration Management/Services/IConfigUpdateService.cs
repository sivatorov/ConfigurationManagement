using Configuration_Management.Models;

namespace Configuration_Management.Services;

/// <summary>
/// Обновление конфигурации информационной базы из файла .cf
/// (/LoadCfg + /UpdateDBCfg через пакетный режим конфигуратора).
/// Используется заданиями по расписанию (issue #286).
/// </summary>
public interface IConfigUpdateService
{
    /// <summary>
    /// Загружает конфигурацию из <paramref name="cfgFilePath"/> и обновляет конфигурацию БД.
    /// </summary>
    Task<BackupRunResult> UpdateConfigAsync(Infobase infobase, string cfgFilePath, BackupCredential? credential = null);
}