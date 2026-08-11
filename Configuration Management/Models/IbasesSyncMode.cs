namespace Configuration_Management.Models;

/// <summary>
/// Режим синхронизации приложения с файлом списка баз 1С (ibases.v8i).
/// </summary>
public enum IbasesSyncMode
{
    /// <summary>Синхронизация отключена.</summary>
    None,

    /// <summary>Только импорт: загружать новые базы из файла ibases.v8i в приложение.</summary>
    Import,

    /// <summary>Только экспорт: выгружать новые базы из приложения в файл ibases.v8i.</summary>
    Export,

    /// <summary>Двусторонняя синхронизация: импорт и экспорт.</summary>
    Both
}