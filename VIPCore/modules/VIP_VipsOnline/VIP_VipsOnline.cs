using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;

namespace VIP_VipsOnline;

public class VIPVipsOnline : BasePlugin
{
    public override string ModuleAuthor => "panda";
    public override string ModuleName => "[VIP] Vips Online";
    public override string ModuleVersion => "v1.0";
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

        if (player == null)
        {
            if (onlineVips.Count != 0)
                Console.WriteLine($"VIP players online: {string.Join(", ", onlineVips)}.");
            else
                Console.WriteLine("No VIP players online.");
            return;
        }

        // Show the list in a menu (WASD/CenterHtml/Chat depending on core config)
        // instead of printing it to chat, and auto-close it after a few seconds.
        // Menu text is rendered as HTML/plain UI, not chat, so the [color] chat
        // placeholders are stripped instead of converted to chat color codes.
        var menu = _api.CreateMenu(StripColorPlaceholders(Localizer["vip.OnlineVips.Title"]));
        menu.ExitButton = true;

        if (onlineVips.Count != 0)
        {
            foreach (var name in onlineVips)
            {
                // Disabled = display-only entry, not something to "select".
                menu.AddMenuOption(name, (_, _) => { }, true);
            }
        }
        else
        {
            menu.AddMenuOption(StripColorPlaceholders(Localizer["vip.NoVipsOnline"]), (_, _) => { }, true);
        }

        menu.Open(player);

        AddTimer(AutoCloseSeconds, () => _api.CloseMenu(player));
    }

    private const float AutoCloseSeconds = 6.0f;

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

    private string StripColorPlaceholders(string message)
    {
        foreach (var colorPlaceholder in _colorMap.Keys)
        {
            message = message.Replace(colorPlaceholder, "");
        }
        return message;
    }
}