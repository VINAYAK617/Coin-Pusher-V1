using GameEngine;

namespace CoinPusherEngine;

internal static class TicketSerializationValidator
{
    private const int MaxPublicWheelStackValue = 3;

    internal static void Validate(GamePlan plan, ICustomProfileSettings settings)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (!plan.Verified)
            throw new InvalidOperationException("Only a verified GamePlan can be serialized.");
        if (plan.TotalSpins <= 0 || plan.Spins.Count != plan.TotalSpins)
        {
            throw new InvalidOperationException(
                $"GamePlan spin contract is invalid: TotalSpins={plan.TotalSpins}, Spins.Count={plan.Spins.Count}.");
        }

        ValidateStartingBoard(plan.Spins[0].Board, settings);

        for (var index = 0; index < plan.Spins.Count; index++)
            ValidateSpin(plan.Spins[index], index + 1, plan, settings);
    }

    private static void ValidateStartingBoard(Cell?[,] board, ICustomProfileSettings settings)
    {
        if (board.GetLength(0) != settings.ROWS || board.GetLength(1) != settings.COLS)
        {
            throw new InvalidOperationException(
                $"Starting board is {board.GetLength(0)}x{board.GetLength(1)}; expected {settings.ROWS}x{settings.COLS}.");
        }

        for (var row = 0; row < settings.ROWS; row++)
        {
            for (var col = 0; col < settings.COLS; col++)
            {
                var cell = board[row, col] ?? throw new InvalidOperationException(
                    $"Starting board cell ({row},{col}) is empty.");
                ValidateNormalCell(cell, $"Starting board cell ({row},{col})", settings);
                if (cell.Stack != 1)
                {
                    throw new InvalidOperationException(
                        $"Starting board cell ({row},{col}) has stack {cell.Stack}; starting-board JSON cannot represent stacks.");
                }
            }
        }
    }

    private static void ValidateSpin(
        SpinPlan spin,
        int expectedSpin,
        GamePlan plan,
        ICustomProfileSettings settings)
    {
        if (spin.Spin != expectedSpin)
            throw new InvalidOperationException($"Spin index {expectedSpin} contains Spin={spin.Spin}.");
        if (spin.Push.Length != settings.COLS || spin.Flush.Length != settings.COLS)
        {
            throw new InvalidOperationException(
                $"Spin {spin.Spin} must contain exactly {settings.COLS} pusher and flush entries.");
        }

        for (var col = 0; col < settings.COLS; col++)
        {
            var valid = spin.Flush[col]
                ? spin.Push[col] == settings.ROWS
                : spin.Push[col] >= settings.MIN_PUSH && spin.Push[col] <= settings.MAX_PUSH;
            if (!valid)
            {
                throw new InvalidOperationException(
                    $"Spin {spin.Spin} column {col} has invalid " +
                    $"{(spin.Flush[col] ? "FLUSH" : "normal")} push value {spin.Push[col]}.");
            }
        }

        foreach (var entry in spin.Spawns)
        {
            var (row, col) = entry.Key;
            if (row < 0 || row >= settings.ROWS || col < 0 || col >= settings.COLS)
                throw new InvalidOperationException($"Spin {spin.Spin} has out-of-range spawn ({row},{col}).");

            var context = $"Spin {spin.Spin} spawn ({row},{col})";
            if (entry.Value.IsFeat)
                ValidateFeatureCell(entry.Value, context, plan, settings);
            else
                ValidateNormalCell(entry.Value, context, settings);
        }
    }

    private static void ValidateNormalCell(Cell cell, string context, ICustomProfileSettings settings)
    {
        ValidateNormalSymbol(cell.Sym, context, settings);
        if (cell.IsFeat)
            throw new InvalidOperationException($"{context} is marked as a feature but uses normal symbol {cell.Sym}.");
        if (cell.Stack < 1 || cell.Stack > settings.MAX_COIN_STACK)
        {
            throw new InvalidOperationException(
                $"{context} has stack {cell.Stack}; expected 1..{settings.MAX_COIN_STACK}.");
        }
    }

    private static void ValidateFeatureCell(
        Cell cell,
        string context,
        GamePlan plan,
        ICustomProfileSettings settings)
    {
        if (cell.Sym != settings.F_WHEEL && cell.Sym != settings.F_XSPIN && cell.Sym != settings.F_PRUP)
            throw new InvalidOperationException($"{context} has unsupported board feature id {cell.Sym}.");
        if (cell.Stack != 1)
            throw new InvalidOperationException($"{context} feature cell has invalid stack {cell.Stack}; expected 1.");

        ValidateNormalSymbol(cell.CvtSym, $"{context} ConvertToId", settings);
        var payload = cell.Fp ?? throw new InvalidOperationException($"{context} is missing its feature payload.");

        if (cell.Sym == settings.F_WHEEL)
        {
            ValidateNormalSymbol(payload.WheelSym, $"{context} WheelSymbolId", settings);
            var publicStack = payload.WheelStack - 1;
            var configuredMaximum = Math.Min(settings.MAX_WHEEL_STACK_VALUE, MaxPublicWheelStackValue);
            if (publicStack < settings.MIN_WHEEL_STACK_VALUE
                || publicStack > configuredMaximum
                || payload.WheelStack > settings.MAX_COIN_STACK)
            {
                throw new InvalidOperationException(
                    $"{context} has internal WHEEL stack {payload.WheelStack} (public bonus {publicStack}); " +
                    $"expected bonus {settings.MIN_WHEEL_STACK_VALUE}..{configuredMaximum} and total <= {settings.MAX_COIN_STACK}.");
            }
        }
        else if (cell.Sym == settings.F_PRUP)
        {
            ValidateNormalSymbol(payload.PrupSym, $"{context} UpgradeSymbolId", settings);
            if (payload.PrupTier <= 0
                || !plan.PrizeValues.TryGetValue(payload.PrupSym, out var tiers)
                || !tiers.ContainsKey(payload.PrupTier))
            {
                throw new InvalidOperationException(
                    $"{context} has no configured prize value for symbol {payload.PrupSym}, tier {payload.PrupTier}.");
            }
        }
    }

    private static void ValidateNormalSymbol(int symbol, string context, ICustomProfileSettings settings)
    {
        var maxSymbol = settings.PrizeLadderRows.Count;
        if (symbol < 1 || symbol > maxSymbol || settings.IsFeat(symbol))
        {
            throw new InvalidOperationException(
                $"{context} has invalid normal symbol {symbol}; expected 1..{maxSymbol}.");
        }
    }
}
