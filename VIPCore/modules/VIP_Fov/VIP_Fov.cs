using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Menu;
using MenuManager;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Fov;

public class VipFov : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Fov";
    public override string ModuleVersion => VipBuild.BuildInfo.Full;
    
    private IVipCoreApi? _api;
    private Fov _fov;

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _fov = new Fov(this, _api);
        _api.RegisterFeature(_fov, FeatureType.Selectable);
    }

    public override void Unload(bool hotReload)
    {
        _api?.UnRegisterFeature(_fov);
    }
}

public class Fov : VipFeatureBase
{
    public override string Feature => "Fov";
    private readonly int[] _fovSettings = new int[67];
    private static readonly PluginCapability<IMenuApi?> MenuCapability = new("menu:nfcore");

    public Fov(BasePlugin vipFov, IVipCoreApi api) : base(api)
    {
        vipFov.RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
        {
            var player = @event.Userid;

            if (player != null && !IsClientVip(player))
            {
                _fovSettings[player.Slot] = 90;
                ChangeFov(player);
            }
            return HookResult.Continue;
        });
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        var cookie = GetPlayerCookie<int>(player.SteamID, "player_fov");

        _fovSettings[player.Slot] = cookie == 0 ? 90 : cookie;
    }

    public override string[] GetPanoramaChoices(CCSPlayerController player) =>
        [GetTranslatedText("fov.Disable"), .. GetFeatureValue<List<int>>(player).Select(value => value.ToString())];

    public override string GetPanoramaValue(CCSPlayerController player) =>
        _fovSettings[player.Slot] == 90
            ? GetTranslatedText("fov.Disable")
            : _fovSettings[player.Slot].ToString();

    public override void SelectPanoramaChoice(CCSPlayerController player, int index)
    {
        var values = GetFeatureValue<List<int>>(player);
        if (index < 0 || index > values.Count) return;

        _fovSettings[player.Slot] = index == 0 ? 90 : values[index - 1];
        ChangeFov(player);
        var menuApi = MenuCapability.Get();
        menuApi?.Notify(player, GetTranslatedText(Feature), GetPanoramaValue(player));
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        var userFov = GetFeatureValue<List<int>>(player);

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

        menu.AddMenuOption(GetTranslatedText("fov.Disable"), (controller, option) =>
        {
            _fovSettings[player.Slot] = 90;

            PrintToChat(player, GetTranslatedText("fov.Off"));
            ChangeFov(controller);
        }, _fovSettings[player.Slot] == 90);
        
        foreach (var i in userFov)
        {
            menu.AddMenuOption(i.ToString(), (controller, option) =>
            {
                _fovSettings[player.Slot] = i;
                PrintToChat(player, GetTranslatedText("fov.On", i));
                ChangeFov(controller);
            }, _fovSettings[player.Slot] == i);
        }

        menu.Open(player);
    }

    private void ChangeFov(CCSPlayerController player)
    {
        var fov = (uint)_fovSettings[player.Slot];
        
        if(IsClientVip(player))
            SetPlayerCookie(player.SteamID, "player_fov", fov);
        
        player.DesiredFOV = fov;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iDesiredFOV");
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        if (!PlayerHasFeature(player))
            _fovSettings[player.Slot] = 90;

        ChangeFov(player);
    }
}
