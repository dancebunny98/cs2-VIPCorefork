using System.Text.Json.Serialization;

namespace VIPCore;

public class Config
{
    public float Delay { get; init; } = 2;

    public Dictionary<string, VipGroup> Groups { get; init; } = new()
    {
        {
            "GROUP_NAME", new VipGroup
            {
                Values = new Dictionary<string, object>()
            }
        }
    };
}

public class VipGroup
{
    public Dictionary<string, object> Values { get; init; } = new();
}

public class CoreConfig
{
    public int TimeMode { get; init; } = 0;
    public int ServerId { get; init; } = 0;
    public bool UseCenterHtmlMenu { get; init; } = true;

    // If MenuManagerCS2 ("menu:nfcore") is installed, force the VIP menu to open
    // as a WASD button menu regardless of the player's own /menu preference.
    // Has no effect if MenuManagerCS2 is not installed.
    public bool UseWasdMenu { get; init; } = true;
    [JsonPropertyName("ServerIP")] public string ServerIp { get; init; } = "0.0.0.0";
    public int ServerPort { get; init; } = 27015;

    //public bool DisplayUnavailableOptions { get; init; }
    public bool ReOpenMenuAfterItemClick { get; init; } = false;
    public bool VipLogging { get; init; } = true;

    // --- Устойчивость к потере связи с БД ---
    // Как часто (сек) проверять живое соединение с БД (SELECT 1).
    public int DbHealthCheckInterval { get; init; } = 30;
    // Пауза перед первой повторной попыткой подключения (сек), затем удваивается.
    public int DbRetryInitialDelay { get; init; } = 2;
    // Максимальная пауза между попытками подключения (сек).
    public int DbRetryMaxDelay { get; init; } = 30;
    // Сколько секунд ждать восстановления БД при записи (выдача/удаление VIP). 0 - не ждать.
    public int DbOperationWait { get; init; } = 10;
    // Максимум соединений в пуле (общий для ядра и модулей). Должен быть заметно меньше max_connections MySQL.
    public int DbMaxPoolSize { get; init; } = 20;
    // Сколько запросов ядро выполняет к БД одновременно; остальные ждут в очереди, а не открывают новые соединения.
    public int DbMaxConcurrency { get; init; } = 8;

    public VipDb Connection { get; init; } = new()
    {
        Host = "HOST",
        Database = "DATABASENAME",
        User = "USER",
        Password = "PASSWORD",
        Port = 3306
    };
}

public class VipDb
{
    public required string Host { get; init; }
    public required string Database { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }
    public int Port { get; init; }
}