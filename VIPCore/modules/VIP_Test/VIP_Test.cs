using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using Dapper;
using MySqlConnector;
using VipCoreApi;

namespace VIP_Test;

public class VipTest : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Test";
    public override string ModuleVersion => VipBuild.BuildInfo.Full;

    private static readonly string Feature = "vip_test_count";
    private IVipCoreApi? _api;
    private Config? _config;
    private Task<bool>? _tableReady;
    
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;
        _config = LoadConfig();
        _tableReady = CreateVipTestTable();
    }

    [ConsoleCommand("css_viptest")]
    [ConsoleCommand("css_testvip")]
    public void OnCommandVipTest(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller == null) return;

        // ВАЖНО: _api/_config проверялись только один раз, в OnAllPluginsLoaded.
        // Если в тот момент VIPCore ещё не поднялся (порядок загрузки плагинов),
        // _api остаётся null, а _config - null! (никогда не инициализирован),
        // и первое же обращение к _config.VipTestEnabled ниже кидало
        // NullReferenceException прямо в обработчике команды.
        if (_api == null || _config == null)
        {
            command.ReplyToCommand(" VIP_Test ещё не готов (VIPCore не найден при загрузке) - перезагрузите плагины.");
            return;
        }

        if (!_config.VipTestEnabled) return;

        if (_api.IsClientVip(controller))
        {
            _api.PrintToChat(controller, _api.GetTranslatedText("vip.AlreadyVipPrivileges"));
            return;
        }

        var authorizedSteamId = controller.AuthorizedSteamID;

        if (authorizedSteamId == null) return;

        _ = GivePlayerVipTest(controller, authorizedSteamId, _config);
    }

    private async Task GivePlayerVipTest(CCSPlayerController player, SteamID steamId, Config vipTest)
    {
        // ВАЖНО: это async void - если отсюда вылетит необработанное исключение,
        // его НЕКОМУ поймать (в отличие от async Task, где исключение уходит в
        // Task и его можно await/try-catch на вызывающей стороне). Необработанное
        // исключение из async void в CS# плагине может уронить весь процесс
        // сервера, поэтому ВСЁ тело метода обёрнуто в try/catch.
        try
        {
            if (_tableReady != null && !await _tableReady)
            {
                Server.NextFrame(() => _api?.PrintToChat(player,
                    "VIP-Test временно недоступен: база данных ещё не готова."));
                return;
            }

            var vipGroup = _api!.GetVipGroups()
                .FirstOrDefault(group => string.Equals(group, vipTest.VipTestGroup,
                    StringComparison.OrdinalIgnoreCase));
            if (vipGroup == null)
            {
                Server.NextFrame(() => _api.PrintToChat(player,
                    $"VIP-Test настроен неверно: группа '{vipTest.VipTestGroup}' не найдена."));
                return;
            }

            var vipTestEndTime = await GetEndTime(steamId.SteamId2);
            var vipTestCount = _api!.GetPlayerCookie<int>(steamId.SteamId64, Feature);

            if (vipTestCount >= vipTest.VipTestCount)
            {
                Server.NextFrame(() =>
                    _api.PrintToChat(player, _api.GetTranslatedText("viptest.YouCanNoLongerTakeTheVip")));
                return;
            }

            if (vipTestEndTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(vipTestEndTime) - DateTimeOffset.UtcNow;
                var timeRemainingFormatted =
                    $"{(time.Days == 0 ? "" : $"{time.Days}d")} {time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";

                Server.NextFrame(() =>
                    _api.PrintToChat(player, _api.GetTranslatedText("viptest.RetakenThrough", timeRemainingFormatted)));
                return;
            }

            var coolDownTime = DateTimeOffset.UtcNow.AddSeconds(vipTest.VipTestCooldown).ToUnixTimeSeconds();
            var endTime = DateTimeOffset.UtcNow.AddSeconds(vipTest.VipTestDuration).ToUnixTimeSeconds();

            if (!await AddUserOrUpdateVipTestAsync(steamId.SteamId2, (int)coolDownTime))
            {
                Server.NextFrame(() => _api?.PrintToChat(player,
                    "VIP-Test временно недоступен: не удалось сохранить попытку."));
                return;
            }
            _api.SetPlayerCookie(steamId.SteamId64, Feature, vipTestCount + 1);
            _api.SaveCookies();

            var timeRemaining = DateTimeOffset.FromUnixTimeSeconds(endTime) - DateTimeOffset.UtcNow;

            Server.NextFrame(() =>
            {
                // Игрок мог успеть получить VIP каким-то другим путём, пока шёл
                // асинхронный запрос к БД выше (другой админ выдал вручную, второй
                // клик по !viptest и т.п.) - GiveClientVip в этом случае КИДАЕТ
                // исключение ("Player already has a VIP"). Перепроверяем перед
                // самим вызовом и просто молча выходим, а не падаем.
                if (!player.IsValid || _api.IsClientVip(player))
                {
                    return;
                }

                _api.PrintToChat(player,
                    _api.GetTranslatedText("viptest.SuccessfullyPassed",
                        timeRemaining.ToString(timeRemaining.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss")));

                try
                {
                    _api.GiveClientVip(player, vipGroup, vipTest.VipTestDuration);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            });
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private async Task<bool> AddUserOrUpdateVipTestAsync(string steamId, int endTime)
    {
        if (await IsUserInVipTest(steamId))
            return await UpdateUserVipTestCount(steamId, endTime);

        return await AddUserToVipTest(steamId, endTime);
    }

    private async Task<bool> AddUserToVipTest(string steamId, long endTime)
    {
        try
        {
            await using var dbConnection = new MySqlConnection(_api.GetDatabaseConnectionString);
            await dbConnection.OpenAsync();

            var insertUserQuery = @"
            INSERT INTO `vipcore_test` (`steamid`, `end_time`)
            VALUES (@SteamId, @EndTime);";

            await dbConnection.ExecuteAsync(insertUserQuery,
                new { SteamId = steamId, EndTime = endTime });
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return false;
        }
    }

    private async Task<bool> UpdateUserVipTestCount(string steamId, long endTime)
    {
        try
        {
            await using var dbConnection = new MySqlConnection(_api.GetDatabaseConnectionString);
            await dbConnection.OpenAsync();

            var updateCountQuery = @"
            UPDATE `vipcore_test`
            SET `end_time` = @EndTime
            WHERE `steamid` = @SteamId;";

            await dbConnection.ExecuteAsync(updateCountQuery,
                new { SteamId = steamId, EndTime = endTime });
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return false;
        }
    }

    private async Task<long> GetEndTime(string steamId)
    {
        try
        {
            await using var dbConnection = new MySqlConnection(_api.GetDatabaseConnectionString);
            await dbConnection.OpenAsync();
    
            var result = await dbConnection.QuerySingleOrDefaultAsync<long>(@"
            SELECT `end_time` FROM `vipcore_test` WHERE `steamid` = @SteamId;",
                new { SteamId = steamId });
    
            return result;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return 0;
        }
    }

    private async Task<bool> IsUserInVipTest(string steamId)
    {
        try
        {
            await using var dbConnection = new MySqlConnection(_api.GetDatabaseConnectionString);
            await dbConnection.OpenAsync();

            var checkUserQuery = @"
            SELECT COUNT(*)
            FROM `vipcore_test`
            WHERE `steamid` = @SteamId;";

            var count = await dbConnection.ExecuteScalarAsync<int>(checkUserQuery, new { SteamId = steamId });

            return count > 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return false;
        }
    }

    private async Task<bool> CreateVipTestTable()
    {
        // БД может быть недоступна в момент загрузки модуля - пробуем несколько раз с растущей паузой.
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            if (await TryCreateVipTestTable()) return true;

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)));
        }

        return false;
    }

    private async Task<bool> TryCreateVipTestTable()
    {
        try
        {
            await using var dbConnection = new MySqlConnection(_api.GetDatabaseConnectionString);
            await dbConnection.OpenAsync();

            var createKeysTable = @"
            CREATE TABLE IF NOT EXISTS `vipcore_test` (
                `steamid` VARCHAR(255) NOT NULL PRIMARY KEY,
                `end_time` BIGINT NOT NULL
            );";

            await dbConnection.ExecuteAsync(createKeysTable);
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return false;
        }
    }

    private Config LoadConfig()
    {
        var configPath = Path.Combine(_api.ModulesConfigDirectory, "vip_test.json");

        if (!File.Exists(configPath)) return CreateConfig(configPath);

        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;

        return config;
    }

    private Config CreateConfig(string configPath)
    {
        var config = new Config
        {
            VipTestEnabled = true,
            VipTestDuration = 3600,
            VipTestCooldown = 86400,
            VipTestGroup = "group_name",
            VipTestCount = 2
        };

        File.WriteAllText(configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        return config;
    }
}

public class Config
{
    public bool VipTestEnabled { get; init; }
    public int VipTestDuration { get; init; }
    public int VipTestCooldown { get; init; }
    public required string VipTestGroup { get; init; }
    public int VipTestCount { get; init; }
}
