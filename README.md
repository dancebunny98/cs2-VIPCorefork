![GitHub all releases](https://img.shields.io/github/downloads/partiusfabaa/cs2-VIPCore/total?style=social)

# cs2-VIPCore

#### If you find an error or anything else. Please message me in discord: thesamefabius

## Requirements
.NET 10 / CounterStrikeSharp **v1.0.376+** (use the `with-runtime` build). All projects target `net10.0`.

## Database connection
The plugin never blocks or fails loading because of the database. It connects in the background, retrying with a growing pause until it succeeds, then creates the tables and loads VIPs of players who are already on the server. While the server runs, the connection is checked every `DbHealthCheckInterval` seconds and immediately on every map change; if it is lost, the pool is reset and reconnection starts automatically. Queries that fail on a dropped connection are retried once. Writes (give/update/remove VIP) never get lost: while the DB is down they are queued in memory (up to 5000) and applied in order right after the connection is back. If the connection settings in `vip_core.json` are fixed while the plugin is retrying, they are picked up automatically - no `css_vip_reload` and no restart needed. Players who joined while the DB was down receive their VIP as soon as it returns.

## Versioning & build
Everything version-related lives in **one file: [`VIPCore/Versions.props`](VIPCore/Versions.props)**:

| Property | What it controls |
|---|---|
| `DotNetTargetFramework` / `DotNetSdkVersion` | Target framework of every project / .NET SDK installed by GitHub Actions |
| `PluginVersion` | Version of VIPCore and VipCoreApi |
| `ModulesVersion` | Default version of all `VIP_*` modules (by default = `PluginVersion`) |
| `ModuleVersionOverrides` | Optional per-module versions, e.g. `VIP_Bhop=1.0.3;VIP_Speed=1.1` |
| `CssApiVersion`, `DapperVersion`, `MySqlConnectorVersion` | NuGet dependency versions (Central Package Management, `Directory.Packages.props`) |
| `BuildNumber` | `0` locally; GitHub Actions passes `github.run_number` |

No `.csproj`, `.cs` or workflow file needs to be touched when bumping anything. The version is embedded into the DLLs (`AssemblyVersion` = `X.Y.Z.0`, `FileVersion` = `X.Y.Z.<build>`, `InformationalVersion` = `X.Y.Z+build.<build>`) and shown as `ModuleVersion` in `css_plugins list` (e.g. `v1.3.3+build.42`).

GitHub Actions reads `Versions.props`, discovers modules automatically (any `VIPCore/modules/<Name>/<Name>.csproj` with `.cs` files) and publishes the final artifact as **`VIPCore-v<PluginVersion>-build<run_number>`** (with a `VERSION.txt` inside). Local build with a custom number: `dotnet build -c Release -p:BuildNumber=123`.

## Installation
1. Install [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) and [Metamod:Source](https://www.sourcemm.net/downloads.php/?branch=master).
2. Install the latest [RRimmer PanoramaMenuManagerCS2](https://github.com/RRimmer/PanoramaMenuManagerCS2/releases). Keep one `MenuManagerApi.dll` in `addons/counterstrikesharp/shared/MenuManagerApi/` and remove copies from plugin folders.
3. Download [VIPCore](https://github.com/dancebunny98/cs2-VIPCorefork/releases) or the latest [Actions artifact](https://github.com/dancebunny98/cs2-VIPCorefork/actions), then unpack its `addons/` directory into the game server. Restart the server after replacing MenuManagerApi.dll.

### Put the modules in this path `addons/counterstrikesharp/plugins`

## Commands 

| **Command**                             | **Description**                                               |
|-------------------------------------|-----------------------------------------------------------|
| **`css_vip_reload` or `!vip_reload`**    | Reloads the configuration **(`@css/root`)** |
| **`css_vip_adduser <steamid or accountid> <vipgroup> <time or 0 permanently>`** | Adds a VIP player **(for server console only)** |
| **`css_vip_updateuser <steamid or accountid> <group or -s> <time or -s>`** | Updates the player's VIP **(for server console only)** |
| **`css_vip_deleteuser <steamid or accountid>`** | Allows you to delete a player by SteamID identifier **(for server console only)** |
| **`css_vip_dbstatus`** | Shows the DB connection status and forces a re-check **(`@css/root`)** |
| **`css_vip`** or **`!vip`** | Opens the VIP menu |

## Configs
Located in the folder `addons/counterstrikesharp/configs/plugins/VIPCore`

### Core.json
```json
{
  "TimeMode": 0,		   // 0 - seconds | 1 - minutes | 2 - hours | 3 - days)
  "ServerId": 0,		   // SERVER ID
  "ServerIP": "0.0.0.0", 	   // default ip
  "ServerPort": 27015, 		   // default port
  "ReOpenMenuAfterItemClick": true,//Whether to reopen the menu after selecting an item | true - yes | false - no
  "VipLogging": true,	   	   //Whether to log VIPCore | true - yes | false - no
  "DbHealthCheckInterval": 30,     // How often (sec) to check the DB connection (SELECT 1)
  "DbRetryInitialDelay": 2,        // First pause (sec) between reconnect attempts, doubles each time
  "DbRetryMaxDelay": 30,           // Maximum pause (sec) between reconnect attempts
  "DbMaxPoolSize": 20,             // Max connections in the pool (shared with modules). Keep it well below MySQL max_connections
  "DbMaxConcurrency": 8,           // Max simultaneous core queries; the rest wait in a queue instead of opening new connections
  "DbOperationWait": 10,           // How long (sec) writes (give/remove VIP) wait for the DB to come back, 0 = don't wait
  "Connection": {
	"Host": 	"host",
	"Database": "database",
	"User": 	"user",
	"Password": "password",
	"Port": 3306
  }
}
```
### vip.json
```json
{
    "Groups": {
        "VIP": {
            "Values": {
                "Speed": 1,
                "NoFallDamage": true,
                "KillScreen": 1,
				"flags": [ "@vip/vip", "@css/vip", "#css/vip" ],
                "FastPlant": true,
                "Defuser": 1,
				"DefuseKit": 1,
                "fastdefuse": 20,
				"Tag": [ "VIP"],
				"Grenades": {
					"CT": {
						"weapon_smokegrenade": 1,
						"weapon_hegrenade": 1,
						"weapon_incgrenade": 1
							},
					"T": {
						"weapon_smokegrenade": 1,
						"weapon_hegrenade": 1,
						"weapon_molotov": 1
						}
				},
				"ExperienceMultiplier": 1.5
           }
       },
       "ADM": {
            "Values": {
                "Zeus": 1,
                "Speed": 1,
                "NoFallDamage": true,
                "KillScreen": 1,
                "FastPlant": true,
                "AntiFlash": 3,
                "Bhop": {
					"Timer": 15.0,
					"MaxSpeed": 4500
				},
				"Grenades": {
					"CT": {
						"weapon_smokegrenade": 1,
						"weapon_hegrenade": 1,
						"weapon_incgrenade": 1
							},
					"T": {
						"weapon_smokegrenade": 1,
						"weapon_hegrenade": 1,
						"weapon_molotov": 1
						}
				},
                "Defuser": 1,
				"DefuseKit": 1,
                "fastdefuse": 50,
                "FastReload": true,
                "Armor": 100,
                "Tag": [ "ADMIN", "OWNER" ],
				"ExperienceMultiplier": 3.5
           }
       }
  }
}
```

## Example Module
```csharp
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using VipCoreApi;

namespace VIPMyModule;

public class VIPMyModule : BasePlugin
{
    public override string ModuleAuthor => "Author";
    public override string ModuleName => "[VIP] My Module";
    public override string ModuleVersion => "1.0.0";

    private MyPlugin _myPlugin;

    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _api.OnCoreReady += () =>
        {
            _myPlugin = new MyPlugin(this, _api);
            _api.RegisterFeature(_myPlugin);
        };
    }

    public override void Unload(bool hotReload)
    {
        _api?.UnRegisterFeature(_myPlugin);
    }
}

public class MyPlugin : VipFeatureBase
{
    public override string Feature => "MyFeature";
    private TestConfig _config;

    public MyPlugin(VIPMyModule vipMyModule, IVipCoreApi api) : base(api)
    {
        vipMyModule.AddCommand("css_viptestcommand", "", OnCmdVipCommand);
        _config = LoadConfig<TestConfig>("VIPMyModule");
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (PlayerHasFeature(player))
        {
            PrintToChat(player, $"VIP player - {player.PlayerName} has spawned");
        }
    }

    public override void OnSelectItem(CCSPlayerController player, IVipCoreApi.FeatureState state)
    {
        if (state == IVipCoreApi.FeatureState.Enabled)
        {
            PrintToChat(player, "Enabled");
        }
        else
        {
            PrintToChat(player, "Disabled");
        }
    }
    
    public void OnCmdVipCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;

        if (IsClientVip(player) && PlayerHasFeature(player))
        {
            PrintToChat(player, $"{player.PlayerName} is a VIP player");
            return;
        }

        PrintToChat(player, $"{player.PlayerName} not a VIP player");
    }
}

public class TestConfig
{
    public int Test1 { get; set; } = 50;
    public bool Test2 { get; set; } = true;
    public string Test3 { get; set; } = "TEST";
    public float Test4 { get; set; } = 30.0f;
}
```
## License

This project is licensed under the MIT License. See the LICENSE file for details.
