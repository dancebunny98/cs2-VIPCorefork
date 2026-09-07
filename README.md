![GitHub all releases](https://img.shields.io/github/downloads/partiusfabaa/cs2-VIPCore/total?style=social)

# cs2-VIPCore

#### If you find an error or anything else. Please message me in discord: thesamefabius

## Installation
1. Install [CounterStrike Sharp](https://github.com/roflmuffin/CounterStrikeSharp), [Metamod:Source](https://www.sourcemm.net/downloads.php/?branch=master)
and fix WASD menu [MenuManagerCS2Firk](https://github.com/Stimayk/MenuManagerCS2/releases)
3. Download [VIPCore](https://github.com/dancebunny98/cs2-VIPCorefork/releases) or Beta and Fix [Auto build](https://github.com/dancebunny98/cs2-VIPCorefork/actions)
4. Unpack the archive and upload it to the game server **(example path: `addons/counterstrikesharp/plugins`)**

### Put the modules in this path `addons/counterstrikesharp/plugins`

## Commands 

| **Command**                             | **Description**                                               |
|-------------------------------------|-----------------------------------------------------------|
| **`css_vip_reload` or `!vip_reload`**    | Reloads the configuration **(`@css/root`)** |
| **`css_vip_adduser <steamid or accountid> <vipgroup> <time or 0 permanently>`** | Adds a VIP player **(for server console only)** |
| **`css_vip_updateuser <steamid or accountid> <group or -s> <time or -s>`** | Updates the player's VIP **(for server console only)** |
| **`css_vip_deleteuser <steamid or accountid>`** | Allows you to delete a player by SteamID identifier **(for server console only)** |
| **`css_vip`** or **`!vip`** | Opens the VIP menu |

## Configs
Located in the folder `addons/counterstrikesharp/configs/plugins/VIPCore`

### Core.json
```json
{
  "TimeMode": 0,		   // 0 - seconds | 1 - minutes | 2 - hours | 3 - days)
  "ServerId": 0,		   // SERVER ID
  "UseCenterHtmlMenu": true,	   //If `true`, the menu will be in the center, if `false`, it will be in the chat. Note that if you have another plugin that uses `CenterHtml`, server crashes may occur
  "UseWasdMenu": true, // fix UseCenterHtmlMenu WASD menu MenuManagerCore 
  "ServerIP": "0.0.0.0", 	   // default ip
  "ServerPort": 27015, 		   // default port
  "ReOpenMenuAfterItemClick": true,//Whether to reopen the menu after selecting an item | true - yes | false - no
  "VipLogging": true,	   	   //Whether to log VIPCore | true - yes | false - no
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
