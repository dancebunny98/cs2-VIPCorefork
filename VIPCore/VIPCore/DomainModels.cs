using CounterStrikeSharp.API.Core;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIPCore;

/// <summary>Состояние VIP-пользователя, загружаемое из хранилища.</summary>
public class User
{
    public int account_id { get; set; }
    public required string name { get; set; }
    public int lastvisit { get; set; }
    public int sid { get; set; }
    public required string group { get; set; }
    public int expires { get; set; }
    public Dictionary<string, FeatureState> FeatureState { get; set; } = new();
}

/// <summary>Персистентные настройки функций конкретного игрока.</summary>
public class PlayerCookie
{
    public ulong SteamId64 { get; set; }
    public Dictionary<string, object> Features { get; set; } = new();
}

/// <summary>Описание VIP-функции и обработчик её выбора в меню.</summary>
public class Feature
{
    public FeatureType FeatureType { get; set; }
    public VipFeatureBase? Handler { get; set; }
    public Action<CCSPlayerController, FeatureState>? OnSelectItem { get; set; }
}

public static class GetUnixTime
{
    public static int GetUnixEpoch(this DateTime dateTime)
    {
        var unixTime = dateTime.ToUniversalTime() -
                       new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return (int)unixTime.TotalSeconds;
    }
}
