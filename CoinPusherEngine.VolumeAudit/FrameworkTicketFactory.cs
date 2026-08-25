namespace CoinPusherEngine.VolumeAudit;

internal static class FrameworkTicketFactory
{
    internal static Ticket Create(
        TicketSerializer.TicketDto gameData,
        int tierNumber,
        int seed,
        decimal cashWin)
    {
        const decimal stake = 1m;
        var freeGame = FreeGame(stake);
        var gameState = State();

        return new Ticket
        {
            ErrorCode = 0,
            Error = "success",
            Game = new TicketGameEnvelope
            {
                PublicState = new TicketPublicState
                {
                    Game = new TicketPublicGame
                    {
                        Parameters = new TicketParameters
                        {
                            TierNumber = tierNumber,
                            StakeMultiplier = cashWin / stake,
                            IsWinner = cashWin > 0m,
                            Seed = seed,
                            CashWin = cashWin,
                            Currency = "£",
                            VersionNum = 1,
                            EngineVersion = 2.0m,
                            Stake = stake,
                            FreeGame = freeGame,
                            Jackpot = null,
                        },
                        GameData = gameData,
                        GameState = gameState,
                    },
                },
                PrivateState = new TicketPrivateState
                {
                    FreeGame = FreeGame(stake),
                    Stake = stake,
                    TierNumber = tierNumber,
                    PendingCashWin = cashWin,
                    GameState = State(),
                },
            },
            End = false,
            CreditAmount = 0m,
        };
    }

    private static TicketFreeGame FreeGame(decimal stake) =>
        new()
        {
            Active = false,
            Remaining = 0,
            Stake = stake,
            TotalBetValue = 0m,
        };

    private static TicketGameState State() =>
        new()
        {
            Current = "GAMBLED",
            Next = "TO_COLLECT",
        };
}
