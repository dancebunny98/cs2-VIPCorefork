using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
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

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        // OnClientAuthorized is not emitted for players already connected during hot reload.
        // Loading here also guarantees that the core has loaded the cookie file first.
        var cookie = GetPlayerCookie<string>(player.SteamID, "player_tag");
        _userSettings[player.Slot] = new UserSettings { Tag = cookie ?? "\0" };
        ChangeTag(player);
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_userSettings[player.Slot] == null) return;

        var userTag = GetFeatureValue<List<string>>(player);

        // Goes through Api.CreateMenu so it respects UseWasdMenu/MenuManagerCS2,
        // instead of always forcing the native chat (!1 !2 !3) menu.
        var menu = CreateMenu(GetTranslatedText(Feature));

        menu.AddMenuOption(GetTranslatedText("tag.Disable"), (controller, option) =>
        {
            _userSettings[player.Slot]!.Tag = "\0";

            PrintToChat(player, GetTranslatedText("tag.Off"));
            ChangeTag(controller);
        }, _userSettings[player.Slot]!.Tag == "\0");
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

        var tag = _userSettings[player.Slot]!.Tag;
        SetPlayerCookie(player.SteamID, "player_tag", tag);
        Api.SaveCookies();
        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (_userSettings[player.Slot] == null) return;
        if (!PlayerHasFeature(player))
            _userSettings[player.Slot]!.Tag = "\0";

        ChangeTag(player);
    }
}
