using Newtonsoft.Json;

namespace CoinPusherEngine;

public sealed class Ticket
{
    [JsonProperty("errorCode")]
    public int ErrorCode { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonProperty("game")]
    public TicketGameEnvelope? Game { get; set; }

    [JsonProperty("end")]
    public bool End { get; set; }

    [JsonProperty("creditAmount")]
    public decimal CreditAmount { get; set; }

    [JsonProperty("Debug")]
    public string? Debug { get; set; }
}

public sealed class TicketGameEnvelope
{
    [JsonProperty("publicState")]
    public TicketPublicState? PublicState { get; set; }

    [JsonProperty("privateState")]
    public TicketPrivateState? PrivateState { get; set; }
}

public sealed class TicketPublicState
{
    [JsonProperty("game")]
    public TicketPublicGame? Game { get; set; }
}

public sealed class TicketPublicGame
{
    public TicketParameters? Parameters { get; set; }
    public TicketSerializer.TicketDto? GameData { get; set; }
    public TicketGameState? GameState { get; set; }
}

public sealed class TicketParameters
{
    public int TierNumber { get; set; }
    public decimal StakeMultiplier { get; set; }
    public bool IsWinner { get; set; }
    public int Seed { get; set; }
    public decimal CashWin { get; set; }
    public string? Currency { get; set; }
    public int VersionNum { get; set; }
    public decimal EngineVersion { get; set; }
    public decimal Stake { get; set; }
    public TicketFreeGame? FreeGame { get; set; }
    public object? Jackpot { get; set; }
}

public sealed class TicketFreeGame
{
    public bool Active { get; set; }
    public int Remaining { get; set; }
    public decimal Stake { get; set; }
    public decimal TotalBetValue { get; set; }
}

public sealed class TicketGameState
{
    public string? Current { get; set; }
    public string? Next { get; set; }
}

public sealed class TicketPrivateState
{
    public TicketFreeGame? FreeGame { get; set; }
    public decimal Stake { get; set; }
    public int TierNumber { get; set; }
    public decimal PendingCashWin { get; set; }
    public TicketGameState? GameState { get; set; }
}
