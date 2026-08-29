using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;

namespace VIP_VipsOnline;

public class VIPVipsOnline : BasePlugin
{
    public override string ModuleAuthor => "panda";
    public override string ModuleName => "[VIP] Vips Online";
    public override string ModuleVersion => "v1.0.1";
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        AddCommand("css_vips", "List online VIP players", ListVipOnlinePlayers);
        AddCommand("css_vipsonline", "List online VIP players", ListVipOnlinePlayers);
    }

    private void ListVipOnlinePlayers(CCSPlayerController? player, CommandInfo info)
    {
        if (_api == null) return;

        var onlineVips = Utilities.GetPlayers().Where(p => p.IsValid && _api.IsClientVip(p)).Select(p => $"{p.PlayerName}").ToList();

        var vipList = string.Join(", ", onlineVips);

        // Console/RCON still receives a readable text response.
        if (player == null)
        {
            if (onlineVips.Count != 0)
                Console.WriteLine($"VIP players online: {vipList}.");
            else
                Console.WriteLine("No VIP players online.");
            return;
        }

        // In-game !vips opens a real menu instead of printing the localization
        // template to chat. The same menu backend as VIPCore is used, so when
        // WASD/MenuManager is enabled it is a ButtonMenu.
        var menu = _api.CreateMenu(ReplaceColorPlaceholders(Localizer["vip.MenuTitle"]));

        if (onlineVips.Count == 0)
        {
            menu.AddMenuOption(
                ReplaceColorPlaceholders(Localizer["vip.NoVipsOnline"]),
                (_, _) => { },
                true);
        }
        else
        {
            foreach (var vipName in onlineVips)
            {
                menu.AddMenuOption(vipName, (_, _) => { }, true);
            }
        }

        menu.Open(player);

        // Do not leave the informational menu hanging on the screen.
        AddTimer(5.0f, () =>
        {
            if (player.IsValid)
                MenuManager.CloseActiveMenu(player);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private readonly Dictionary<string, char> _colorMap = new()
    {
        { "[default]", ChatColors.Default },
        { "[white]", ChatColors.White },
        { "[darkred]", ChatColors.DarkRed },
        { "[green]", ChatColors.Green },
        { "[lightyellow]", ChatColors.LightYellow },
        { "[lightblue]", ChatColors.LightBlue },
        { "[olive]", ChatColors.Olive },
        { "[lime]", ChatColors.Lime },
        { "[red]", ChatColors.Red },
        { "[lightpurple]", ChatColors.LightPurple },
        { "[purple]", ChatColors.Purple },
        { "[grey]", ChatColors.Grey },
        { "[yellow]", ChatColors.Yellow },
        { "[gold]", ChatColors.Gold },
        { "[silver]", ChatColors.Silver },
        { "[blue]", ChatColors.Blue },
        { "[darkblue]", ChatColors.DarkBlue },
        { "[bluegrey]", ChatColors.BlueGrey },
        { "[magenta]", ChatColors.Magenta },
        { "[lightred]", ChatColors.LightRed },
        { "[orange]", ChatColors.Orange }
    };

    private string ReplaceColorPlaceholders(string message)
    {
        foreach (var colorPlaceholder in _colorMap)
        {
            message = message.Replace(colorPlaceholder.Key, colorPlaceholder.Value.ToString());
        }
        return message;
    }
}
