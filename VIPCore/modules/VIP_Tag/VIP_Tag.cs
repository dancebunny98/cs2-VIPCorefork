using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Tag;

public class VIP_Tag : BasePlugin
{
    public override string ModuleAuthor => "Toil";
    public override string ModuleName => "[VIP] Tag";
    public override string ModuleVersion => "v1.0.2";

    private Tag _tag;
    private IVipCoreApi? _api;
    
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

    private readonly UserSettings?[] _userSettings = new UserSettings?[65];

    public Tag(VIPTag vipTag, IVipCoreApi api) : base(api)
    {
        vipTag.RegisterListener<Listeners.OnClientConnected>(slot =>
        {
            var cookie = GetPlayerCookie<string>(Utilities.GetPlayerFromSlot(slot).SteamID, "player_tag");

            _userSettings[slot + 1] = new UserSettings { Tag = cookie };
        });

        vipTag.RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
        {
            var player = @event.Userid;
            if (!IsClientVip(player))
            {
                _userSettings[player.Index]!.Tag = "\0";
                ChangeTag(player);
            }

            _userSettings[player.Index] = null;
            return HookResult.Continue;
        });
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_userSettings[player.Index] == null) return;

        var userTag = GetFeatureValue<List<string>>(player);

        // Use the same menu backend as VIPCore. If MenuManagerCS2/WASD is enabled
        // in VIPCore, tags are shown as real menu items instead of requiring !1/!2/!3.
        var menu = Api.CreateMenu(GetTranslatedText("tag.MenuTitle"));

        menu.AddMenuOption(GetTranslatedText("tag.Disable"), (controller, _) =>
        {
            _userSettings[player.Index]!.Tag = "\0";

            PrintToChat(player, GetTranslatedText("tag.Off"));
            ChangeTag(controller);
        }, _userSettings[player.Index]!.Tag == "\0");

        foreach (var tag in userTag)
        {
            var selectedTag = tag;
            menu.AddMenuOption(selectedTag, (controller, _) =>
            {
                _userSettings[player.Index]!.Tag = selectedTag;
                PrintToChat(player, GetTranslatedText("tag.On", selectedTag));
                ChangeTag(controller);
            }, _userSettings[player.Index]!.Tag == selectedTag);
        }

        menu.Open(player);
    }

    private void ChangeTag(CCSPlayerController player)
    {
        if (!(player != null && player.IsValid && !player.IsBot && !player.IsHLTV && _userSettings[player.Index]!.Tag != null)) return;

        var tag = _userSettings[player.Index]!.Tag;
        SetPlayerCookie(player.SteamID, "player_tag", tag);
        player.Clan = tag;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_szClan");
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (_userSettings[player.Index] == null) return;
        if (!PlayerHasFeature(player))
            _userSettings[player.Index]!.Tag = "\0";

        ChangeTag(player);
    }
}
