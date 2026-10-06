using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using MenuManager;
using Microsoft.Extensions.Logging;
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
    private int _chatMessageId = -1;

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _tag = new Tag(this, _api);
        _api.RegisterFeature(_tag, FeatureType.Selectable);
        try
        {
            // UM_SayText2 is 118 in csgo/usermessages.proto.
            HookUserMessage(118, _tag.OnChatMessage, HookMode.Pre);
            _chatMessageId = 118;
        }
        catch (NativeException ex)
        {
            Logger.LogError(ex, "Chat tag hook unavailable");
        }
    }

    public override void Unload(bool hotReload)
    {
        if (_chatMessageId >= 0)
            UnhookUserMessage(_chatMessageId, _tag.OnChatMessage, HookMode.Pre);
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
    private static readonly PluginCapability<IMenuApi?> MenuCapability = new("menu:nfcore");

    public Tag(VIPTag vipTag, IVipCoreApi api) : base(api)
    {
        // OnClientConnected срабатывает ДО завершения Steam-авторизации - .SteamID в этот
        // момент может быть ещё невалиден, из-за чего кука с тегом ищется не по тому ID и
        // тег "сбрасывается". OnClientAuthorized даёт уже проверенный SteamID.
        vipTag.RegisterListener<Listeners.OnClientAuthorized>((slot, steamId) =>
        {
            var cookie = GetPlayerCookie<string>(steamId.SteamId64, "player_tag");

            _userSettings[slot] = new UserSettings { Tag = cookie ?? string.Empty };
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

    public HookResult OnChatMessage(UserMessage message)
    {
        if (!message.ReadBool("chat") ||
            !message.ReadString("messagename").TrimStart('#').StartsWith("Cstrike_Chat_", StringComparison.Ordinal))
            return HookResult.Continue;

        var player = Utilities.GetPlayerFromIndex(message.ReadInt("entityindex"));
        if (player == null || !player.IsValid || player.IsBot || !IsClientVip(player))
            return HookResult.Continue;

        var isModerator = HasModeratorFlag(player);
        if (!isModerator && !PlayerHasFeature(player))
            return HookResult.Continue;

        var selectedTag = _userSettings[player.Slot]?.Tag;
        if (selectedTag == "\0")
            return HookResult.Continue;

        var tag = isModerator
            ? "MOD"
            : !string.IsNullOrWhiteSpace(selectedTag)
                ? selectedTag
                : GetDefaultTag(player);
        if (string.IsNullOrWhiteSpace(tag) || tag == "\0")
            return HookResult.Continue;

        message.SetString("param1", $"{ChatColors.Grey}[{tag}] \x03{message.ReadString("param1")}");
        return HookResult.Continue;
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
        _userSettings[player.Slot] = new UserSettings { Tag = cookie ?? GetDefaultTag(player) };
        EnsureAutomaticTag(player);
        ChangeTag(player);
    }

    public override string[] GetPanoramaChoices(CCSPlayerController player) =>
        [GetTranslatedText("tag.Disable"), .. GetFeatureValue<List<string>>(player)];

    public override string GetPanoramaValue(CCSPlayerController player) =>
        _userSettings[player.Slot]?.Tag == "\0"
            ? GetTranslatedText("tag.Disable")
            : _userSettings[player.Slot]?.Tag ?? GetDefaultTag(player);

    public override void SelectPanoramaChoice(CCSPlayerController player, int index)
    {
        var tags = GetFeatureValue<List<string>>(player);
        if (index < 0 || index > tags.Count || _userSettings[player.Slot] == null) return;

        _userSettings[player.Slot]!.Tag = index == 0 ? "\0" : tags[index - 1];
        ChangeTag(player);
        var menuApi = MenuCapability.Get();
        menuApi?.Notify(player, GetTranslatedText(Feature), GetPanoramaValue(player));
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_userSettings[player.Slot] == null) return;

        var userTag = GetFeatureValue<List<string>>(player);

        // Goes through Api.CreateMenu so it respects PanoramaMenuManager settings,
        // instead of always forcing the native chat (!1 !2 !3) menu.
        var menu = CreateMenu(GetTranslatedText(Feature));
        IMenuApi? menuApi;
        try { menuApi = MenuCapability.Get(); }
        catch { menuApi = null; }

        if (menuApi?.GetMenuType(player) == MenuManager.MenuType.PanoramaMenu)
        {
            var choices = GetPanoramaChoices(player);
            menu.PostSelectAction = PostSelectAction.Nothing;
            menuApi.AddSelect(menu, GetTranslatedText(Feature), GetPanoramaValue(player), choices, (controller, _, index) =>
            {
                SelectPanoramaChoice(controller, index);
                OnSelectItem(controller, state);
            });
            menu.Open(player);
            return;
        }

        menu.AddMenuOption(GetTranslatedText("tag.Disable"), (controller, option) =>
        {
            _userSettings[controller.Slot]!.Tag = "\0";

            PrintToChat(controller, GetTranslatedText("tag.Off"));
            ChangeTag(controller);
        }, _userSettings[player.Slot]!.Tag == "\0");
        foreach (var tag in userTag)
        {
            menu.AddMenuOption(tag, (controller, option) =>
            {
                _userSettings[controller.Slot]!.Tag = tag;
                PrintToChat(controller, GetTranslatedText("tag.On", tag));
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
        player.Clan = tag == "\0" ? string.Empty : tag;
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

        if (_userSettings[player.Slot]!.Tag == "\0") return;

        var tags = GetFeatureValue<List<string>>(player);
        if (!tags.Contains(_userSettings[player.Slot]!.Tag))
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
