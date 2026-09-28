using System.Collections.Concurrent;
using System.Net.Sockets;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace VIPCore;

/// <summary>
/// Бросается, когда БД сейчас недоступна (соединение потеряно или ещё не установлено).
/// Это не «ошибка запроса» - монитор соединения уже занимается переподключением.
/// </summary>
public sealed class DatabaseUnavailableException(string operation, Exception? inner = null)
    : Exception($"Database is unavailable (operation: {operation})", inner);

/// <summary>
/// Работа с БД + фоновый монитор соединения.
///
/// Как это работает:
///  1. <see cref="Start"/> запускает фоновый цикл. Пока БД не отвечает, он пытается подключиться
///     с нарастающей паузой (DbRetryInitialDelay -> DbRetryMaxDelay) и НЕ блокирует загрузку плагина.
///     Как только подключение удалось - создаются таблицы и вызывается <see cref="ConnectionEstablished"/>.
///  2. Пока БД доступна, каждые DbHealthCheckInterval секунд выполняется проверка (SELECT 1).
///     Если она не прошла - пул соединений сбрасывается и цикл снова уходит в переподключение.
///  3. <see cref="RequestCheck"/> (вызывается при смене карты) запускает проверку немедленно.
///  4. Каждый запрос идёт через <see cref="ExecuteAsync{T}"/>: при обрыве соединения запрос
///     повторяется, а если и повтор не помог - БД помечается как недоступная и монитор просыпается.
/// </summary>
public sealed class Database : IDisposable
{
    private const int MaxAttempts = 2;

    private readonly VipCore _vipCore;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    // Ограничивает число одновременных запросов ядра, чтобы всплески (смена карты, таймеры) не открывали десятки соединений.
    private readonly SemaphoreSlim _gate;

    private string _connectionString;
    private int _available;
    private volatile bool _schemaReady;
    private Task? _monitorTask;
    private Task? _writerTask;

    // Очередь записей (выдача/обновление/удаление VIP): если БД недоступна, операции ждут в памяти
    // и применяются по порядку сразу после восстановления связи - без перезагрузки сервера.
    private const int MaxPendingWrites = 5000;
    private readonly ConcurrentQueue<PendingWrite> _writes = new();
    private readonly SemaphoreSlim _writeSignal = new(0, int.MaxValue);

    private sealed record PendingWrite(string Operation, Func<MySqlConnection, Task> Action);

    public int PendingWrites => _writes.Count;

    private long _lastSuccessTicks;
    private volatile string _lastError = string.Empty;

    public Database(VipCore vipCore, ILogger logger, string dbConnectionString)
    {
        _vipCore = vipCore;
        _logger = logger;
        _connectionString = dbConnectionString;
        _gate = new SemaphoreSlim(Math.Max(1, vipCore.CoreConfig.DbMaxConcurrency));
    }

    /// <summary>true - последняя проверка/запрос прошли успешно.</summary>
    public bool IsAvailable => Volatile.Read(ref _available) == 1;

    /// <summary>Вызывается при КАЖДОМ переходе «недоступна -> доступна» (в том числе при первом подключении).</summary>
    public event Action? ConnectionEstablished;

    /// <summary>Вызывается при переходе «доступна -> недоступна».</summary>
    public event Action<Exception?>? ConnectionLost;

    public string StatusText
    {
        get
        {
            var last = Interlocked.Read(ref _lastSuccessTicks);
            var lastText = last == 0
                ? "never"
                : DateTime.FromBinary(last).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

            var pending = PendingWrites == 0 ? string.Empty : $"; queued writes: {PendingWrites}";

            return IsAvailable
                ? $"connected (last successful check: {lastText}{pending})"
                : $"NOT connected, reconnecting in background (last successful check: {lastText}; last error: {(_lastError.Length == 0 ? "-" : _lastError)}{pending})";
        }
    }

    #region Monitor

    public void Start()
    {
        if (_monitorTask != null) return;

        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
        _writerTask = Task.Run(() => WriteWorkerAsync(_cts.Token));
    }

    /// <summary>Заменяет строку подключения (например, после css_vip_reload) и сразу переподключается.</summary>
    public void UpdateConnectionString(string connectionString)
    {
        if (_connectionString == connectionString) return;

        var old = _connectionString;
        _connectionString = connectionString;
        _ = ClearPoolAsync(old);

        // Новая строка - новая проверка с нуля.
        if (Interlocked.Exchange(ref _available, 0) == 1)
            _logger.LogInformation("[VIP] Database connection settings changed, reconnecting...");

        Wake();
    }

    /// <summary>Просит монитор проверить соединение прямо сейчас (не ждать таймера / паузы между попытками).</summary>
    public void RequestCheck() => Wake();

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        await _wake.WaitAsync(delay, ct);
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var config = _vipCore.CoreConfig;

                if (IsAvailable)
                {
                    // Ждём таймер проверки, либо пока нас разбудят (смена карты / ошибка запроса).
                    await WaitAsync(TimeSpan.FromSeconds(Math.Max(5, config.DbHealthCheckInterval)), ct);

                    // Пока ждали, какой-то запрос мог пометить БД недоступной - идём переподключаться.
                    if (!IsAvailable) continue;

                    if (!await PingAsync(ct))
                        MarkUnavailable(null);

                    continue;
                }

                attempt++;
                if (await TryConnectAsync(attempt, ct))
                {
                    attempt = 0;
                    continue;
                }

                var initial = Math.Max(1, config.DbRetryInitialDelay);
                var max = Math.Max(initial, config.DbRetryMaxDelay);
                var seconds = Math.Min(max, initial * Math.Pow(2, Math.Min(attempt - 1, 10)));

                await WaitAsync(TimeSpan.FromSeconds(seconds), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Монитор не имеет права умереть - иначе переподключаться будет некому.
                _logger.LogError(e, "[VIP] Unexpected error in database monitor");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    // Одна «проба» соединения: открыть, выполнить SELECT 1 и (при первом подключении) создать таблицы.
    private async Task ProbeAsync(bool ensureSchema, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteScalarAsync<int>("SELECT 1", commandTimeout: 10);

        if (ensureSchema && !_schemaReady)
            await EnsureSchemaAsync(connection);
    }

    // Жёсткий предел на одну пробу: даже если сетевой стек «завис», монитор не застрянет навсегда.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await ProbeAsync(false, ct).WaitAsync(ProbeTimeout, ct);

            Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.ToBinary());
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _lastError = Short(e);
            _logger.LogWarning("[VIP] Database health check failed: {error}", Short(e));
            return false;
        }
    }

    private async Task<bool> TryConnectAsync(int attempt, CancellationToken ct)
    {
        try
        {
            await ProbeAsync(true, ct).WaitAsync(ProbeTimeout, ct);

            Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.ToBinary());
            MarkAvailable();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _lastError = Short(e);
            _logger.LogWarning("[VIP] Database connection attempt #{attempt} failed: {error}", attempt, Short(e));

            await ClearPoolAsync(_connectionString);

            // Возможно, причина в неверных настройках подключения, которые уже исправили в vip_core.json:
            // подхватываем их на лету, без css_vip_reload и без перезагрузки сервера.
            var fresh = _vipCore.TryReadConnectionStringFromDisk();
            if (fresh != null && fresh != _connectionString)
            {
                _logger.LogInformation("[VIP] Connection settings in vip_core.json changed - using the new ones");
                UpdateConnectionString(fresh);
                _vipCore.DbConnectionString = fresh;
            }

            return false;
        }
    }

    private void MarkAvailable()
    {
        if (Interlocked.Exchange(ref _available, 1) == 1) return;

        _lastError = string.Empty;
        _logger.LogInformation("[VIP] Database connection established");
        RaiseSafe(() => ConnectionEstablished?.Invoke());
    }

    private void MarkUnavailable(Exception? error)
    {
        if (error != null)
            _lastError = Short(error);

        if (Interlocked.Exchange(ref _available, 0) == 0)
        {
            // Уже была помечена недоступной - просто убедимся, что монитор не спит.
            Wake();
            return;
        }

        _logger.LogWarning("[VIP] Database connection LOST, reconnecting in background...");
        _ = ClearPoolAsync(_connectionString);
        RaiseSafe(() => ConnectionLost?.Invoke(error));
        Wake();
    }

    private void RaiseSafe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[VIP] Error in database state handler");
        }
    }

    private async Task ClearPoolAsync(string connectionString)
    {
        // Чистим только СВОЙ пул (по строке подключения), чтобы не задеть другие плагины.
        try
        {
            await using var connection = new MySqlConnection(connectionString);
            await MySqlConnection.ClearPoolAsync(connection);
        }
        catch
        {
            // ignored
        }
    }

    private async Task<bool> WaitUntilAvailableAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        Wake();

        while (DateTime.UtcNow < until)
        {
            if (IsAvailable) return true;
            await Task.Delay(250);
        }

        return IsAvailable;
    }

    private static bool IsTransient(Exception e) => e switch
    {
        MySqlException me => me.IsTransient || me.InnerException is IOException or SocketException or TimeoutException,
        IOException or SocketException or TimeoutException or ObjectDisposedException => true,
        InvalidOperationException ioe => ioe.Message.Contains("connection", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static string Short(Exception e)
    {
        var inner = e;
        while (inner.InnerException != null) inner = inner.InnerException;

        return inner == e ? e.Message : $"{e.Message} -> {inner.Message}";
    }

    #endregion

    #region Schema

    private async Task EnsureSchemaAsync(MySqlConnection dbConnection)
    {
        try
        {
            const string createVipUsersTable = """
                                               CREATE TABLE IF NOT EXISTS `vip_users` (
                                                   `account_id` BIGINT NOT NULL,
                                                   `name` VARCHAR(64) NOT NULL,
                                                   `lastvisit` BIGINT NOT NULL,
                                                   `sid` BIGINT NOT NULL,
                                                   `group` VARCHAR(64) NOT NULL,
                                                   `expires` BIGINT NOT NULL,
                                               PRIMARY KEY (`account_id`, `sid`));
                                               """;

            await dbConnection.ExecuteAsync(createVipUsersTable);

            const string createVipServersTable = """
                                                 CREATE TABLE IF NOT EXISTS `vip_servers` (
                                                     `serverId` BIGINT NOT NULL,
                                                     `serverIp` VARCHAR(45) NOT NULL,
                                                     `port` INT NOT NULL,
                                                     `created_at` TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                                     `updated_at` TIMESTAMP,
                                                     PRIMARY KEY (`serverId`)
                                                 );
                                                 """;

            await dbConnection.ExecuteAsync(createVipServersTable);

            // Check if the ServerIP and ServerPort already exist
            const string checkVipServerQuery = """
                                               SELECT COUNT(*)
                                               FROM `vip_servers`
                                               WHERE `serverIp` = @ServerIP AND `port` = @ServerPort;
                                               """;

            var serverExists = await dbConnection.ExecuteScalarAsync<int>(checkVipServerQuery, new
            {
                ServerIP = _vipCore.CoreConfig.ServerIp,
                ServerPort = _vipCore.CoreConfig.ServerPort
            });

            if (serverExists == 0)
            {
                // Insert ServerIP and ServerPort from config into vip_servers table
                const string insertVipServerQuery = """
                                                    INSERT INTO `vip_servers` (`serverId`, `serverIp`, `port`)
                                                    VALUES (@ServerId, @ServerIP, @ServerPort);
                                                    """;

                try
                {
                    await dbConnection.ExecuteAsync(insertVipServerQuery, new
                    {
                        ServerId = _vipCore.CoreConfig.ServerId,
                        ServerIP = _vipCore.CoreConfig.ServerIp,
                        ServerPort = _vipCore.CoreConfig.ServerPort,
                    });
                }
                catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
                {
                    _logger.LogWarning(
                        "[VIP] vip_servers already has serverId {serverId} with a different IP/port - leaving it as is",
                        _vipCore.CoreConfig.ServerId);
                }
            }

            _schemaReady = true;
        }
        catch (Exception e) when (!IsTransient(e))
        {
            // Например, у пользователя БД нет права CREATE, а таблицы уже созданы вручную.
            // Само соединение при этом рабочее - не заставляем плагин вечно «переподключаться».
            _schemaReady = true;
            _logger.LogError("[VIP] Failed to create/check tables (connection itself is OK): {error}", Short(e));
        }
    }

    #endregion

    #region Query helper

    /// <summary>
    /// Выполняет запрос с автоповтором при обрыве соединения.
    /// </summary>
    /// <param name="operation">Название операции (для логов).</param>
    /// <param name="action">Сам запрос.</param>
    /// <param name="waitForRecovery">
    /// true - если БД сейчас недоступна, подождать её восстановления (для записи: выдача/удаление VIP админом);
    /// false - сразу бросить <see cref="DatabaseUnavailableException"/> (для загрузки игрока: он будет
    /// подгружен автоматически после восстановления связи).
    /// </param>
    private async Task<T> ExecuteAsync<T>(string operation, Func<MySqlConnection, Task<T>> action,
        bool waitForRecovery = false)
    {
        if (!IsAvailable)
        {
            var wait = TimeSpan.FromSeconds(Math.Max(0, _vipCore.CoreConfig.DbOperationWait));

            if (!waitForRecovery || wait == TimeSpan.Zero || !await WaitUntilAvailableAsync(wait))
                throw new DatabaseUnavailableException(operation);
        }

        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(30)))
            throw new DatabaseUnavailableException(operation + " (query queue is full)");

        Exception? last = null;

        try
        {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync();
                return await action(connection);
            }
            catch (Exception e) when (IsTransient(e))
            {
                last = e;
                _logger.LogWarning("[VIP] '{operation}' failed (attempt {attempt}/{max}): {error}",
                    operation, attempt, MaxAttempts, Short(e));

                await ClearPoolAsync(_connectionString);

                if (attempt < MaxAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            }
        }

        }
        finally
        {
            _gate.Release();
        }

        MarkUnavailable(last);
        throw new DatabaseUnavailableException(operation, last);
    }

    private void Enqueue(string operation, Func<MySqlConnection, Task> action)
    {
        while (_writes.Count >= MaxPendingWrites && _writes.TryDequeue(out var dropped))
            _logger.LogError("[VIP] Write queue is full, dropping the oldest operation: {operation}", dropped.Operation);

        _writes.Enqueue(new PendingWrite(operation, action));

        if (!IsAvailable)
            _logger.LogWarning("[VIP] Database is down - '{operation}' queued, will be applied after reconnect ({count} queued)",
                operation, _writes.Count);

        try
        {
            _writeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WriteWorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_writes.TryPeek(out var item))
                {
                    await _writeSignal.WaitAsync(ct);
                    continue;
                }

                if (!IsAvailable)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                try
                {
                    await ExecuteAsync<object?>(item.Operation, async connection =>
                    {
                        await item.Action(connection);
                        return null;
                    });

                    _writes.TryDequeue(out _);
                }
                catch (DatabaseUnavailableException)
                {
                    // Операция остаётся в очереди и будет выполнена, как только связь вернётся.
                    await Task.Delay(1000, ct);
                }
                catch (Exception e)
                {
                    // Ошибка самого запроса (не связи) - повтор не поможет.
                    _logger.LogError("[VIP] Queued operation '{operation}' failed and was dropped: {error}",
                        item.Operation, e.ToString());
                    _writes.TryDequeue(out _);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "[VIP] Unexpected error in database write worker");

                try
                {
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<bool> TryAsync(string operation, Func<MySqlConnection, Task> action, bool waitForRecovery)
    {
        if (waitForRecovery)
        {
            // Записи никогда не теряются из-за недоступной БД - см. Enqueue / WriteWorkerAsync.
            Enqueue(operation, action);
            return true;
        }

        try
        {
            await ExecuteAsync<object?>(operation, async connection =>
            {
                await action(connection);
                return null;
            }, waitForRecovery);

            return true;
        }
        catch (DatabaseUnavailableException e)
        {
            _logger.LogWarning("[VIP] {message}", e.Message);
            return false;
        }
        catch (Exception e)
        {
            _logger.LogError("{error}", e.ToString());
            return false;
        }
    }

    #endregion

    #region Queries

    public async Task<User?> GetExistingUserFromDb(int accountId)
    {
        try
        {
            return await ExecuteAsync("GetExistingUser", async connection =>
            {
                var serverId = _vipCore.CoreConfig.ServerId;

                return await connection.QuerySingleOrDefaultAsync<User>(
                    @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid",
                    new { AccId = accountId, sid = serverId });
            });
        }
        catch (DatabaseUnavailableException e)
        {
            _logger.LogWarning("[VIP] {message}", e.Message);
            return null;
        }
        catch (Exception e)
        {
            _logger.LogError("{error}", e.ToString());
            return null;
        }
    }

    public Task AddUserToDb(User user) => TryAsync("AddUser", async connection =>
    {
        // ВАЖНО: используем CoreConfig.ServerId напрямую, а не отдельный поход в БД
        // за vip_servers по ServerIp/ServerPort (как было раньше) - тот запрос мог
        // тихо вернуть другое значение (нет строки под текущий IP, конфиг менялся,
        // временная ошибка подключения), и тогда проверка "уже есть VIP" смотрела
        // не туда, пропуская INSERT с уже занятым (account_id, sid) -> сырое
        // исключение MySqlException "Duplicate entry" вместо аккуратного варнинга.
        var serverId = _vipCore.CoreConfig.ServerId;

        var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
            @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
            {
                AccId = user.account_id,
                sid = serverId
            });

        if (existingUser != null)
        {
            _vipCore.PrintLogWarning("User already exists");
            return;
        }

        try
        {
            await connection.ExecuteAsync(@"
                    INSERT INTO vip_users (account_id, name, lastvisit, sid, `group`, expires)
                    VALUES (@account_id, @name, @lastvisit, @sid, @group, @expires);", user);
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Страховка на случай реальной гонки (или повтора запроса после обрыва связи) -
            // вместо необработанного исключения в лог просто пишем варнинг.
            _vipCore.PrintLogWarning("User already exists (race on insert): {accId}", user.account_id);
            return;
        }

        _vipCore.PrintLogInfo("Player '{name} [{accId}]' has been successfully added", user.name, user.account_id);
    }, waitForRecovery: true);

    public Task UpdateUserInDb(User user) => TryAsync("UpdateUser", async connection =>
    {
        var serverId = _vipCore.CoreConfig.ServerId;
        var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
            @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
            {
                AccId = user.account_id,
                sid = serverId
            });

        if (existingUser == null)
        {
            _vipCore.PrintLogWarning("User does not exist");
            return;
        }

        await connection.ExecuteAsync(@"
            UPDATE 
                vip_users
            SET 
                name = @name,
                lastvisit = @lastvisit,
                `group` = @group,
                expires = @expires
            WHERE account_id = @account_id AND sid = @sid;", user);

        _vipCore.PrintLogInfo("Player '{name} [{accId}]' has been successfully updated", user.name,
            user.account_id);
    }, waitForRecovery: true);

    public Task UpdateUserVip(int accountId, string name = "", string group = "", int time = -1) =>
        TryAsync("UpdateUserVip", async connection =>
        {
            var serverId = _vipCore.CoreConfig.ServerId;
            var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
                @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid", new
                {
                    AccId = accountId,
                    sid = serverId
                });

            if (existingUser == null)
            {
                _vipCore.PrintLogWarning($"User with account ID '{accountId}' does not exist");
                return;
            }

            if (!string.IsNullOrEmpty(name))
                existingUser.name = name;

            if (!string.IsNullOrEmpty(group))
                existingUser.group = group;

            if (time > -1)
                existingUser.expires = time == 0 ? 0 : _vipCore.CalculateEndTimeInSeconds(time);

            await connection.ExecuteAsync(@"
            UPDATE 
                vip_users
            SET 
                name = @name,
                `group` = @group,
                expires = @expires
            WHERE account_id = @account_id AND sid = @sid;", existingUser);

            _vipCore.PrintLogInfo(
                $"Player '{existingUser.name} [{accountId}]' VIP information has been successfully updated");
        }, waitForRecovery: true);

    public Task RemoveUserFromDb(int accId) => TryAsync("RemoveUser", async connection =>
    {
        var serverId = _vipCore.CoreConfig.ServerId;

        var existingUser = await connection.QuerySingleOrDefaultAsync<User>(
            @"SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid",
            new { AccId = accId, sid = serverId });

        if (existingUser == null)
            return;

        await connection.ExecuteAsync(@"
            DELETE FROM vip_users
        WHERE account_id = @AccId AND sid = @sid;", new { AccId = accId, sid = serverId });

        _vipCore.PrintLogInfo("Player {name}[{accId}] has been successfully removed", existingUser.name, accId);
    }, waitForRecovery: true);

    /// <summary>
    /// Возвращает список VIP игрока. null - значит запрос НЕ удалось выполнить (БД недоступна / ошибка):
    /// вызывающий код должен отличать это от пустого списка («игрок не VIP») и повторить загрузку позже.
    /// </summary>
    public async Task<List<User?>?> GetUserFromDb(int accId)
    {
        try
        {
            return await ExecuteAsync("GetUser", async connection =>
            {
                var serverId = _vipCore.CoreConfig.ServerId;
                var user = await connection.QueryAsync<User?>(
                    "SELECT * FROM `vip_users` WHERE `account_id` = @AccId AND sid = @sid AND (expires > @CurrTime OR expires = 0)",
                    new { AccId = accId, sid = serverId, CurrTime = DateTime.UtcNow.GetUnixEpoch() }
                );

                return user.ToList();
            });
        }
        catch (DatabaseUnavailableException e)
        {
            _logger.LogWarning("[VIP] {message}. Player will be loaded after the connection is restored.",
                e.Message);
        }
        catch (Exception e)
        {
            _logger.LogError("{error}", e.ToString());
        }

        return null;
    }

    public Task RemoveExpiredUsers(CCSPlayerController player, SteamID steamId) =>
        TryAsync("RemoveExpiredUsers", async connection =>
        {
            var serverId = _vipCore.CoreConfig.ServerId;

            var expiredUsers = (await connection.QueryAsync<User>(
                "SELECT * FROM vip_users WHERE account_id = @AccId AND sid = @sid AND expires < @CurrentTime AND expires > 0",
                new
                {
                    AccId = steamId.AccountId,
                    sid = serverId,
                    CurrentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                })).ToList();

            if (expiredUsers.Count > 0)
                Console.WriteLine($"Removing expired VIPS, Current time: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

            foreach (var user in expiredUsers)
            {
                await connection.ExecuteAsync("DELETE FROM vip_users WHERE account_id = @AccId AND sid = @sid",
                    new
                    {
                        AccId = user.account_id,
                        user.sid
                    });

                await Server.NextFrameAsync(() =>
                {
                    var authSteamId = player.AuthorizedSteamID;
                    if (authSteamId != null && authSteamId.AccountId == user.account_id)
                        _vipCore.PrintToChat(player, _vipCore.Localizer["vip.Expired", user.group]);

                    _vipCore.VipApi.OnPlayerRemoved(player, user.group);
                });

                _vipCore.PrintLogInfo("User '{name} [{accId}]' has been removed due to expired VIP status.",
                    user.name, user.account_id);
            }
        }, waitForRecovery: false);

    #endregion

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _ = ClearPoolAsync(_connectionString);
    }
}
