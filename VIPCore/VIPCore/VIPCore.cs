using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using MenuManager;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIPCore;

public partial class VipCore : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Core";
    public override string ModuleVersion => VipBuild.BuildInfo.Full;

    public Config Config { get; set; } = null!;
    public CoreConfig CoreConfig { get; set; } = null!;
    public VipCoreApi VipApi { get; set; } = null!;

    public Database Database = null!;

    public readonly bool[] IsClientVip = new bool[70];

    public readonly ConcurrentDictionary<ulong, User> Users = new();
    public readonly ConcurrentDictionary<string, Feature> Features = new();

    public readonly HashSet<string> ForcedDisabledFeatures = new();

    private readonly PluginCapability<IVipCoreApi> _pluginCapability = new("vipcore:core");

    // Optional dependency on MenuManagerCS2 ("menu:nfcore") to render the VIP menu
    // as a WASD button menu instead of the native CenterHtml/Chat menu.
    private readonly PluginCapability<IMenuApi?> _menuManagerCapability = new("menu:nfcore");
    public IMenuApi? MenuApi { get; private set; }

    public readonly FakeConVar<bool> IsCoreEnableConVar = new("css_vip_enable", "", true);

    public string DbConnectionString = string.Empty;


    private string[] _sortedItems = [];

    public override void Load(bool hotReload)
    {
        VipApi = new VipCoreApi(this);
        Capabilities.RegisterPluginCapability(_pluginCapability, () => VipApi);
        Server.NextWorldUpdate(() => VipApi.CoreReady());

        LoadConfig();

        DbConnectionString = BuildConnectionString();
        Database = new Database(this, Logger, DbConnectionString);

        // Подключение к БД идёт в фоне с автоповторами - загрузка плагина не блокируется
        // и не зависит от того, доступна ли БД в этот момент.
        Database.ConnectionEstablished += OnDatabaseConnectionEstablished;
        Database.Start();

        RegisterEventHandlers();
        SetupTimers();

        AddCommand("css_vip", "command that opens the VIP MENU", (player, _) => CreateMenu(player));
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            MenuApi = _menuManagerCapability.Get();
        }
        catch
        {
            MenuApi = null;
        }

        Logger.LogInformation(MenuApi != null
            ? "MenuManagerCS2 found, VIP menu will use WASD (ButtonMenu)."
            : "MenuManagerCS2 not found, falling back to the native Chat/CenterHtml menu.");
    }

    private void LoadConfig()
    {
        var coreConfigDirectory = VipApi.CoreConfigDirectory;

        Config = VipApi.LoadConfig<Config>("vip", coreConfigDirectory);
        CoreConfig = VipApi.LoadConfig<CoreConfig>("vip_core", coreConfigDirectory);

        var sortMenuPath = Path.Combine(coreConfigDirectory, "sort_menu.txt");

        if (!File.Exists(sortMenuPath))
            File.WriteAllLines(sortMenuPath, ["feature1", "feature2"]);

        _sortedItems = File.ReadAllLines(sortMenuPath);
    }

    private void RegisterEventHandlers()
    {
        RegisterListener<Listeners.OnClientAuthorized>((slot, id) =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is null || !player.IsValid) return;

            Task.Run(() => OnClientAuthorizedAsync(player, id));
        });

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            VipApi.LoadCookies();

            // После смены карты соединение могло «протухнуть» - проверяем сразу, не дожидаясь таймера.
            Database.RequestCheck();
        });
        RegisterListener<Listeners.OnMapEnd>(() => VipApi.SaveCookies());
        RegisterEventHandler<EventServerShutdown>((@event, info) =>
        {
            VipApi.SaveCookies();
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnect);
        RegisterEventHandler<EventPlayerSpawn>(EventPlayerSpawn);
    }

    private HookResult EventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsClientVip[player.Slot])
            return HookResult.Continue;

        IsClientVip[player.Slot] = false;
        if (Users.TryGetValue(player.SteamID, out var user))
        {
            foreach (var featureState in user.FeatureState.Where(f =>
                         Features[f.Key].FeatureType is FeatureType.Toggle))
            {
                VipApi.SetPlayerCookie(player.SteamID, featureState.Key, (int)featureState.Value);
            }
        }

        Users.Remove(player.SteamID, out var _);

        var authAccId = player.AuthorizedSteamID;
        if (authAccId == null) return HookResult.Continue;

        var playerName = player.PlayerName;
        Task.Run(() => Database.UpdateUserVip(authAccId.AccountId, name: playerName));

        return HookResult.Continue;
    }

    private HookResult EventPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid || player.Handle == IntPtr.Zero || player.UserId == null)
            return HookResult.Continue;

        if (player.IsBot || !IsClientVip[player.Slot])
            return HookResult.Continue;

        AddTimer(Config.Delay, () =>
        {
            if (player.Connected != PlayerConnectedState.Connected) return;

            try
            {
                VipApi.PlayerSpawn(player);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Exception in VipApi.PlayerSpawn: {ex}");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private void SetupTimers()
    {
        AddTimer(300.0f, () =>
        {
            var players = new List<(CCSPlayerController Player, SteamID Id)>();

            foreach (var player in Utilities.GetPlayers().Where(player => player.IsValid))
            {
                var authId = player.AuthorizedSteamID;
                if (authId == null) continue;

                players.Add((player, authId));
                IsClientVip[player.Slot] = IsUserActiveVip(player);
            }

            // Раньше здесь создавалась отдельная задача (и соединение) на КАЖДОГО игрока одновременно.
            // Теперь одна фоновая задача обходит игроков по очереди; если БД недоступна - пропускаем цикл.
            if (players.Count == 0 || !Database.IsAvailable) return;

            Task.Run(async () =>
            {
                foreach (var (player, id) in players)
                    await Database.RemoveExpiredUsers(player, id);
            });
        }, TimerFlags.REPEAT);
    }

    private void OnDatabaseConnectionEstablished()
    {
        // Вызывается из фонового потока. Игроки, подключившиеся пока БД была недоступна,
        // остались без VIP - подгружаем их сейчас.
        Server.NextFrame(() =>
        {
            var pending = new List<(CCSPlayerController Player, SteamID Id)>();

            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsValid || player.IsBot || player.IsHLTV) continue;

                var authId = player.AuthorizedSteamID;
                if (authId == null || Users.ContainsKey(authId.SteamId64)) continue;

                pending.Add((player, authId));
            }

            if (pending.Count == 0) return;

            Task.Run(async () =>
            {
                foreach (var (player, id) in pending)
                    await OnClientAuthorizedAsync(player, id);
            });
        });
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_dbstatus", "Shows VIP database connection status (and forces a re-check)")]
    public void OnCommandDbStatus(CCSPlayerController? controller, CommandInfo command)
    {
        ReplyToCommand(controller, $"[VIP] Database: {Database.StatusText}");
        Database.RequestCheck();
    }

    public override void Unload(bool hotReload)
    {
        Database.ConnectionEstablished -= OnDatabaseConnectionEstablished;
        Database.Dispose();
    }

    public async Task OnClientAuthorizedAsync(CCSPlayerController player, SteamID steamId)
    {
        try
        {
            var userFromDb = await Database.GetUserFromDb(steamId.AccountId);
            if (userFromDb == null) return;

            Users.Remove(steamId.SteamId64, out _);
            foreach (var user in userFromDb.OfType<User>().Where(user => user.sid == CoreConfig.ServerId))
            {
                Users.TryAdd(steamId.SteamId64, user);
                SetClientFeature(steamId.SteamId64, user.group);

                var timeRemaining = DateTimeOffset.FromUnixTimeSeconds(user.expires);

                await Server.NextFrameAsync(() =>
                {
                    // IsClientVip выставляем ДО OnPlayerLoaded - если какая-то фича
                    // в своём обработчике PlayerLoaded проверяет Api.IsClientVip(player),
                    // она должна увидеть уже актуальное значение, а не false.
                    IsClientVip[player.Slot] = IsUserActiveVip(player);
                    VipApi.OnPlayerLoaded(player, user.group);

                    AddTimer(5.0f, () => PrintToChat(player,
                        Localizer["vip.WelcomeToTheServer", user.name] + (user.expires == 0
                            ? string.Empty
                            : Localizer["vip.Expires", user.group, timeRemaining.ToString("G")])));
                });
                return;
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e.ToString());
        }
    }

    public void SetClientFeature(ulong steamId, string vipGroup)
    {
        if (!Config.Groups.TryGetValue(vipGroup, out var group) ||
            !Users.TryGetValue(steamId, out var user)) return;

        foreach (var (key, _) in Features)
        {
            if (!group.Values.TryGetValue(key, out _))
            {
                user.FeatureState[key] = FeatureState.NoAccess;
                continue;
            }

            var cookie = VipApi.GetPlayerCookie<int>(steamId, key);
            var cookieValue = cookie == 2 ? 0 : cookie;
            user.FeatureState[key] = (FeatureState)cookieValue;
        }
    }

    public User CreateNewUser(int accountId, string username, string group, int endTime)
    {
        return new User
        {
            account_id = accountId,
            name = username,
            lastvisit = DateTime.UtcNow.GetUnixEpoch(),
            sid = CoreConfig.ServerId,
            group = group,
            expires = endTime == 0 ? 0 : CalculateEndTimeInSeconds(endTime)
        };
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_adduser")]
    public void OnCmdAddUser(CCSPlayerController? controller, CommandInfo command)
    {
        if (command.ArgCount is > 4 or < 4)
        {
            PrintLogInfo("Usage: css_vip_adduser {usage}", $"<steamid or accountid> <group> <time_{GetTimeUnitName}>");
            return;
        }

        var accountId = Utils.GetAccountIdFromCommand(command.GetArg(1), out var player);
        if (accountId == -1)
            return;

        var vipGroup = command.GetArg(2);
        var endVipTime = Convert.ToInt32(command.GetArg(3));

        if (!Config.Groups.ContainsKey(vipGroup))
        {
            PrintLogError("This {VIP} group was not found!", "VIP");
            return;
        }

        var username = player == null ? "unknown" : player.PlayerName;

        var user = CreateNewUser(accountId, username, vipGroup, endVipTime);

        AddVip(player, user);
    }

    public void AddVip(CCSPlayerController? player, User user) // :)
    {
        Task.Run(() => Database.AddUserToDb(user));

        if (player == null) return;

        Users.TryAdd(player.SteamID, user);
        IsClientVip[player.Slot] = true;

        SetClientFeature(player.SteamID, user.group);
        VipApi.OnPlayerLoaded(player, user.group);
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_deleteuser")]
    public void OnCmdDeleteVipUser(CCSPlayerController? controller, CommandInfo command)
    {
        if (command.ArgCount is < 2 or > 2)
        {
            ReplyToCommand(controller, "Using: css_vip_deleteuser <steamid or accountid>");
            return;
        }

        var accountId = Utils.GetAccountIdFromCommand(command.GetArg(1), out var player);
        if (accountId == -1)
            return;

        RemoveVip(player, accountId);
    }

    public void RemoveVip(CCSPlayerController? player, int accountId) // :)
    {
        if (player != null)
        {
            VipApi.OnPlayerRemoved(player, Users[player.SteamID].group);

            Users.TryRemove(player.SteamID, out _);
            IsClientVip[player.Slot] = false;
        }

        Task.Run(() => Database.RemoveUserFromDb(accountId));
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_updateuser")]
    public void OnCmdUpdateUserGroup(CCSPlayerController? controller, CommandInfo command)
    {
        if (command.ArgCount is > 4 or < 4)
        {
            PrintLogInfo("Usage: css_vip_updateuser {usage}\n{t}", "<steamid or accountid> [group or -s] [time or -s]",
                "if you don't want to update something, don't leave it blank, write `-` or `-s`\nExample of updating time: css_vip_updateuser \"STEAM_0:0:123456\" -s 3600");
            return;
        }

        var accountId = Utils.GetAccountIdFromCommand(command.GetArg(1), out var player);
        if (accountId == -1)
            return;

        var vipGroup = command.GetArg(2);

        if (vipGroup is not ("-" or "-s"))
        {
            if (!Config.Groups.ContainsKey(vipGroup))
            {
                PrintLogError("This {VIP} group was not found!", "VIP");
                return;
            }
        }
        else
            vipGroup = string.Empty;

        var time = int.TryParse(command.GetArg(3), out var arg) ? arg : -1;

        if (player != null)
        {
            if (!Users.TryGetValue(player.SteamID, out var user)) return;

            user.group = vipGroup;
        }

        Task.Run(() => Database.UpdateUserVip(accountId, group: vipGroup, time: time));
    }

    [RequiresPermissions("@css/root")]
    [CommandHelper(1, "<steamid>")]
    [ConsoleCommand("css_reload_vip_player")]
    public void OnCommandVipReloadInfractions(CCSPlayerController? player, CommandInfo command)
    {
        var target = Utils.GetPlayerFromSteamId(command.GetArg(1));

        if (target == null) return;
        if (target.AuthorizedSteamID == null) return;

        var steamid = target.AuthorizedSteamID;

        Task.Run(async () => await OnClientAuthorizedAsync(target, steamid));
    }

    [RequiresPermissions("@css/root")]
    [ConsoleCommand("css_vip_reload")]
    public void OnCommandReloadConfig(CCSPlayerController? controller, CommandInfo command)
    {
        LoadConfig();
        DbConnectionString = BuildConnectionString();
        Database.UpdateConnectionString(DbConnectionString);

        const string msg = "configuration successfully rebooted!";

        ReplyToCommand(controller, msg);
    }

    private void CreateMenu(CCSPlayerController? player)
    {
        if (player == null) return;

        if (!IsClientVip[player.Slot])
        {
            PrintToChat(player, Localizer["vip.NoAccess"]);
            return;
        }

        if (!Users.TryGetValue(player.SteamID, out var user)) return;

        var menu = VipApi.CreateMenu(Localizer["menu.Title", user.group]);
        if (Config.Groups.TryGetValue(user.group, out var vipGroup))
        {
            var sortedFeatures = Features.Where(setting => setting.Value.FeatureType is not FeatureType.Hide)
                .OrderBy(setting => Array.IndexOf(_sortedItems, setting.Key))
                .ThenBy(setting => setting.Key);

            foreach (var (key, feature) in sortedFeatures)
            {
                if (!vipGroup.Values.TryGetValue(key, out var featureValue)) continue;
                if (string.IsNullOrEmpty(featureValue.ToString())) continue;
                if (!user.FeatureState.TryGetValue(key, out var featureState)) continue;

                var value = string.Empty;
                if (feature.FeatureType is FeatureType.Toggle)
                {
                    value = featureState switch
                    {
                        FeatureState.Enabled => $"{Localizer["chat.Enabled"]}",
                        FeatureState.Disabled => $"{Localizer["chat.Disabled"]}",
                        FeatureState.NoAccess => $"{Localizer["chat.NoAccess"]}",
                        _ => throw new ArgumentOutOfRangeException()
                    };
                }

                var featureType = feature.FeatureType;

                menu.AddMenuOption(
                    Localizer[key] + (featureType == FeatureType.Selectable
                        ? string.Empty
                        : $" [{value}]"),
                    (controller, _) =>
                    {
                        var result = VipApi.PlayerUseFeature(player, key, featureState, featureType);

                        if (result == HookResult.Handled || result == HookResult.Stop)
                        {
                            CreateMenu(player);
                            return;
                        }

                        var returnState = featureState;
                        if (featureType != FeatureType.Selectable)
                        {
                            returnState = featureState switch
                            {
                                FeatureState.Enabled => FeatureState.Disabled,
                                FeatureState.Disabled => FeatureState.Enabled,
                                _ => returnState
                            };

                            VipApi.PrintToChat(player,
                                $"{Localizer[key]}: {(returnState == FeatureState.Enabled ? $"{Localizer["chat.Enabled"]}" : $"{Localizer["chat.Disabled"]}")}");
                        }

                        user.FeatureState[key] = returnState;
                        feature.OnSelectItem?.Invoke(controller, returnState);

                        if (CoreConfig.ReOpenMenuAfterItemClick && featureType != FeatureType.Selectable)
                        {
                            CreateMenu(controller);
                        }
                    }, featureState == FeatureState.NoAccess || ForcedDisabledFeatures.Contains(key));
            }
        }

        menu.Open(player);
    }


    /// <summary>
    /// Читает vip_core.json с диска и возвращает строку подключения, если файл читается.
    /// Не трогает CoreConfig - используется базой данных, чтобы подхватить исправленные
    /// логин/пароль/хост без перезагрузки сервера. При любой ошибке возвращает null.
    /// </summary>
    public string? TryReadConnectionStringFromDisk()
    {
        try
        {
            var path = Path.Combine(VipApi.CoreConfigDirectory, "vip_core.json");
            if (!File.Exists(path)) return null;

            var config = System.Text.Json.JsonSerializer.Deserialize<CoreConfig>(File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions
                {
                    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            return config == null ? null : BuildConnectionString(config.Connection, config.DbMaxPoolSize);
        }
        catch
        {
            return null;
        }
    }

    private string BuildConnectionString() => BuildConnectionString(CoreConfig.Connection, CoreConfig.DbMaxPoolSize);

    private static string BuildConnectionString(VipDb connection, int maxPoolSize) =>
        DatabaseConnectionStringBuilder.Build(connection, maxPoolSize);

        // Короткий таймаут подключения, чтобы недоступная БД не вешала запросы надолго;
        // ConnectionReset очищает состояние соединения при возврате в пул.
    public bool IsPlayerVip(CCSPlayerController player)
    {
        return IsClientVip[player.Slot];
    }

    private bool IsUserActiveVip(CCSPlayerController player)
    {
        if (!IsCoreEnableConVar.Value || !Utils.IsValidEntity(player) || !player.IsValid || player.IsBot)
            return false;

        var authorizedSteamId = player.AuthorizedSteamID;
        if (authorizedSteamId == null)
        {
            PrintLogError("{steamid} is null", "AuthorizedSteamId");
            return false;
        }

        if (!Users.TryGetValue(authorizedSteamId.SteamId64, out var user))
            return false;

        if (user.expires != 0 && DateTime.UtcNow.GetUnixEpoch() > user.expires)
        {
            Users.Remove(authorizedSteamId.SteamId64, out _);
            return false;
        }

        return user.expires == 0 || DateTime.UtcNow.GetUnixEpoch() < user.expires;
    }

    private void ReplyToCommand(CCSPlayerController? controller, string msg)
    {
        if (controller != null)
            PrintToChat(controller, msg);
        else
            Console.WriteLine(msg);
    }

    public void PrintToChat(CCSPlayerController player, string msg)
    {
        if (!player.IsValid) return;

        player.PrintToChat($"{Localizer["vip.Prefix"]} {msg}");
    }

    public void PrintToChatAll(string msg)
    {
        Server.PrintToChatAll($"{Localizer["vip.Prefix"]} {msg}");
    }

    public void PrintLogError(string? message, params object?[] args)
    {
        if (!CoreConfig.VipLogging) return;

        Logger.LogError($"{message}", args);
    }

    public void PrintLogInfo(string? message, params object?[] args)
    {
        if (!CoreConfig.VipLogging) return;

        Logger.LogInformation($"{message}", args);
    }

    public void PrintLogWarning(string? message, params object?[] args)
    {
        if (!CoreConfig.VipLogging) return;

        Logger.LogWarning($"{message}", args);
    }

    private string GetTimeUnitName => CoreConfig.TimeMode switch
    {
        0 => "second",
        1 => "minute",
        2 => "hours",
        3 => "days",
        _ => throw new KeyNotFoundException("No such number was found!")
    };

    public int CalculateEndTimeInSeconds(int time) => DateTime.UtcNow.AddSeconds(CoreConfig.TimeMode switch
    {
        1 => time * 60,
        2 => time * 3600,
        3 => time * 86400,
        _ => time
    }).GetUnixEpoch();
}
