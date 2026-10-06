using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Tag;

public class VIPTag : BasePlugin
{
    public override string ModuleAuthor => "Toil";
    public override string ModuleName => "[VIP] Tag";
    public override string ModuleVersion => VipBuild.BuildInfo.Full;

    private IVipCoreApi? _api;
    private Tag _tag;

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _tag = new Tag(this, _api);
        _api.RegisterFeature(_tag, FeatureType.Selectable);
    }

    public override void Unload(bool hotReload)
    {
        _api?.UnRegisterFeature(_tag);
    }
}

public class UserSettings
{
    public string Tag { get; set; } = "\0";
}

public class Tag : VipFeatureBase
{
    public override string Feature => "Tag";

    private readonly UserSettings?[] _userSettings = new UserSettings?[70];

    public Tag(VIPTag vipTag, IVipCoreApi api) : base(api)
    {
        // OnClientConnected срабатывает ДО завершения Steam-авторизации - .SteamID в этот
        // момент может быть ещё невалиден, из-за чего кука с тегом ищется не по тому ID и
        // тег "сбрасывается". OnClientAuthorized даёт уже проверенный SteamID.
        vipTag.RegisterListener<Listeners.OnClientAuthorized>((slot, steamId) =>
        {
            var cookie = GetPlayerCookie<string>(steamId.SteamId64, "player_tag");

            _userSettings[slot] = new UserSettings { Tag = cookie ?? "\0" };
        });

        vipTag.RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
        {
            var player = @event.Userid;

            // Боты и всё, что не проходило через OnClientAuthorized (не было записи в
            // _userSettings), не должны обрабатываться здесь - раньше это падало с NRE.
            if (player is null || !player.IsValid || _userSettings[player.Slot] is null)
            {
                return HookResult.Continue;
            }

            if (!IsClientVip(player))
            {
                _userSettings[player.Slot]!.Tag = "\0";
                ChangeTag(player);
            }

            _userSettings[player.Slot] = null;
            return HookResult.Continue;
        });
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        var player = Utilities.GetPlayerFromSlot(@event.Userid);
        if (player == null || !player.IsValid || player.IsBot || !IsClientVip(player))
            return HookResult.Continue;

        var isModerator = HasModeratorFlag(player);
        if (!isModerator && !PlayerHasFeature(player))
            return HookResult.Continue;

        // The chat event can arrive before OnPlayerLoaded has populated the
        // per-slot state (for example after a hot reload). Resolve the default
        // tag from the current VIP group instead of dropping the chat message.
        var tag = isModerator
            ? "MOD"
            : _userSettings[player.Slot]?.Tag is { } selected && !string.IsNullOrWhiteSpace(selected) && selected != "\0"
                ? selected
                : GetDefaultTag(player);
        if (string.IsNullOrWhiteSpace(tag) || tag == "\0")
            return HookResult.Continue;

        var message = $"{ChatColors.Grey}[{tag}] {ChatColors.Default}{player.PlayerName}: {@event.Text}";
        info.DontBroadcast = true;
        foreach (var recipient in Utilities.GetPlayers())
        {
            if (!recipient.IsValid || recipient.IsBot ||
                (@event.Teamonly && recipient.TeamNum != player.TeamNum))
                continue;

            recipient.PrintToChat(message);
        }

        return HookResult.Handled;
    }

    private static bool HasModeratorFlag(CCSPlayerController player)
    {
        var steamId = new SteamID(player.SteamID);
        // Source-style flags a,b,c,j,g,k,m. The root flag (z) is intentionally
        // not included, so root-only players keep their normal VIP tag.
        if (AdminManager.PlayerHasPermissions(steamId, "@css/root"))
            return false;

        return new[]
        {
            "@css/reservation", "@css/generic", "@css/kick", "@css/chat",
            "@css/changemap", "@css/vote", "@css/rcon"
        }
            .Any(permission => AdminManager.PlayerHasPermissions(steamId, permission));
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        // OnClientAuthorized is not emitted for players already connected during hot reload.
        // Loading here also guarantees that the core has loaded the cookie file first.
        var cookie = GetPlayerCookie<string>(player.SteamID, "player_tag");
        _userSettings[player.Slot] = new UserSettings { Tag = cookie ?? "\0" };
        EnsureAutomaticTag(player);
        ChangeTag(player);
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_userSettings[player.Slot] == null) return;

        var userTag = GetFeatureValue<List<string>>(player);

        // Goes through Api.CreateMenu so it respects PanoramaMenuManager settings,
        // instead of always forcing the native chat (!1 !2 !3) menu.
        var menu = CreateMenu(GetTranslatedText(Feature));

        menu.AddMenuOption(GetTranslatedText("tag.Disable"), (controller, option) =>
        {
            // VIP players always have a tag. Selecting disable restores the
            // last configured tag instead of leaving the clan tag empty.
            _userSettings[player.Slot]!.Tag = GetDefaultTag(player);

            PrintToChat(player, GetTranslatedText("tag.On", _userSettings[player.Slot]!.Tag));
            ChangeTag(controller);
        }, false);
        foreach (var tag in userTag)
        {
            menu.AddMenuOption(tag, (controller, option) =>
            {
                _userSettings[player.Slot]!.Tag = tag;
                PrintToChat(player, GetTranslatedText("tag.On", tag));
                ChangeTag(controller);
            }, _userSettings[player.Slot]!.Tag == tag);
        }

        menu.Open(player);
    }

    private void ChangeTag(CCSPlayerController player)
    {
        if (!(player != null && player.IsValid && !player.IsBot && !player.IsHLTV && _userSettings[player.Slot] != null)) return;

        EnsureAutomaticTag(player);
        var tag = _userSettings[player.Slot]!.Tag;
        SetPlayerCookie(player.SteamID, "player_tag", tag);
        Api.SaveCookies();
        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    private string GetDefaultTag(CCSPlayerController player)
    {
        try
        {
            var tags = GetFeatureValue<List<string>>(player);
            return tags.LastOrDefault(tag => !string.IsNullOrWhiteSpace(tag)) ?? "\0";
        }
        catch
        {
            return "\0";
        }
    }

    private void EnsureAutomaticTag(CCSPlayerController player)
    {
        if (_userSettings[player.Slot] == null || !PlayerHasFeature(player)) return;

        _userSettings[player.Slot]!.Tag = GetDefaultTag(player);
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (_userSettings[player.Slot] == null) return;
        if (!PlayerHasFeature(player))
            _userSettings[player.Slot]!.Tag = "\0";
        else
            EnsureAutomaticTag(player);

        ChangeTag(player);
    }
}
