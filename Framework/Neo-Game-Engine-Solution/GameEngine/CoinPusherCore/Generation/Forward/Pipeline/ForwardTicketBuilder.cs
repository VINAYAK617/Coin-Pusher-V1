namespace CoinPusherEngine;

internal enum ForwardTicketBuildStatus
{
    Valid,
    MathInputFailed,
    WinningPolicyFailed,
    ObjectiveFailed,
    FeatureBudgetFailed,
    FeatureTimingFailed,
    FeatureIntentFailed,
    TurnFrameFailed,
    ObjectiveFinalizationFailed,
    EnvelopeFailed,
    PipelineFailed,
}

internal sealed class ForwardTicketBuildResult
{
    internal ForwardTicketBuildResult(
        ForwardTicketBuildStatus status,
        string detail,
        ForwardMathInputResult? mathInput,
        ForwardObjectiveResult? objectives,
        ForwardFeatureBudgetResult? featureBudget,
        ForwardFeatureTimingResult? featureTiming,
        ForwardFeatureIntentResult? featureIntents,
        ForwardTurnFrameResult? turnFrames,
        ForwardTicketEnvelopeResult? envelope,
        ForwardTicketPipelineResult? pipeline)
    {
        Status = status;
        Detail = detail;
        MathInput = mathInput;
        Objectives = objectives;
        FeatureBudget = featureBudget;
        FeatureTiming = featureTiming;
        FeatureIntents = featureIntents;
        TurnFrames = turnFrames;
        Envelope = envelope;
        Pipeline = pipeline;
    }

    internal ForwardTicketBuildStatus Status { get; }
    internal string Detail { get; }
    internal ForwardMathInputResult? MathInput { get; }
    internal ForwardObjectiveResult? Objectives { get; }
    internal ForwardFeatureBudgetResult? FeatureBudget { get; }
    internal ForwardFeatureTimingResult? FeatureTiming { get; }
    internal ForwardFeatureIntentResult? FeatureIntents { get; }
    internal ForwardTurnFrameResult? TurnFrames { get; }
    internal ForwardTicketEnvelopeResult? Envelope { get; }
    internal ForwardTicketPipelineResult? Pipeline { get; }
    internal bool IsValid => Status == ForwardTicketBuildStatus.Valid;
}

internal sealed class ForwardTicketBuilder
{
    private readonly ICustomProfileSettings _settings;
    private readonly int _seed;

    internal ForwardTicketBuilder(ICustomProfileSettings settings, int seed)
    {
        _settings = settings;
        _seed = seed;
    }

    internal ForwardTicketBuildResult Build(
        IReadOnlyList<decimal>? prizeAmounts,
        int? exactPpsCombinationId = null)
    {
        var math = new ForwardMathInputResolver(_settings).Resolve(
            prizeAmounts,
            SeedFor("math"),
            exactPpsCombinationId);
        if (!math.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.MathInputFailed,
                $"{math.Status}: {math.Detail}",
                math);
        }

        var winningPolicy = new ForwardWinningRoundPolicyPlanner(_settings).Plan(
            prizeAmounts!.Sum(),
            SeedFor("winning-policy"));
        if (!winningPolicy.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.WinningPolicyFailed,
                $"{winningPolicy.Status}: {winningPolicy.Detail}",
                math);
        }

        var objectives = new ForwardObjectivePlanner(_settings).Resolve(
            math.Bundle!.Input,
            SeedFor("objectives"),
            winningPolicy.Plan);
        if (!objectives.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.ObjectiveFailed,
                $"{objectives.Status}: {objectives.Detail}",
                math,
                objectives);
        }

        var budget = new ForwardFeatureBudgetPlanner(_settings).Plan(
            math.Bundle.Input,
            objectives.Objectives,
            SeedFor("budget"));
        if (!budget.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.FeatureBudgetFailed,
                $"{budget.Status}: {budget.Detail}",
                math,
                objectives,
                budget);
        }

        var timing = new ForwardFeatureTimingPlanner(
            _settings,
            SeedFor("timing")).Plan(budget.Budget, objectives.Objectives);
        if (!timing.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.FeatureTimingFailed,
                $"{timing.Status}: {timing.Detail}",
                math,
                objectives,
                budget,
                timing);
        }

        var intents = new ForwardFeatureIntentPlanner(
            _settings,
            SeedFor("intents")).Plan(objectives.Objectives, timing.Timing, budget.Budget);
        if (!intents.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.FeatureIntentFailed,
                $"{intents.Status}: {intents.Detail}",
                math,
                objectives,
                budget,
                timing,
                intents);
        }

        ForwardTicketBuildResult? lastRealizationFailure = null;
        var localAttempts = Math.Max(1, _settings.LocalRealizationAttempts);
        var primaryGeometryAttempts = Math.Max(1, (localAttempts * 3) / 4);
        for (var localAttempt = 0; localAttempt < primaryGeometryAttempts; localAttempt++)
        {
            var realization = BuildRealization(
                math,
                objectives,
                budget,
                timing,
                intents,
                planningVariant: 0,
                localAttempt);
            if (realization.IsValid)
                return realization;

            lastRealizationFailure = realization;
        }

        var remainingCandidates = localAttempts - primaryGeometryAttempts;
        for (var planningVariant = 1; planningVariant <= remainingCandidates; planningVariant++)
        {
            var alternateTiming = new ForwardFeatureTimingPlanner(
                _settings,
                SeedForPlanningVariant("timing", planningVariant)).Plan(
                    budget.Budget,
                    objectives.Objectives);
            if (!alternateTiming.IsValid)
            {
                lastRealizationFailure = Fail(
                    ForwardTicketBuildStatus.FeatureTimingFailed,
                    $"{alternateTiming.Status}: {alternateTiming.Detail}",
                    math,
                    objectives,
                    budget,
                    alternateTiming);
                continue;
            }

            var alternateIntents = new ForwardFeatureIntentPlanner(
                _settings,
                SeedForPlanningVariant("intents", planningVariant)).Plan(
                    objectives.Objectives,
                    alternateTiming.Timing,
                    budget.Budget);
            if (!alternateIntents.IsValid)
            {
                lastRealizationFailure = Fail(
                    ForwardTicketBuildStatus.FeatureIntentFailed,
                    $"{alternateIntents.Status}: {alternateIntents.Detail}",
                    math,
                    objectives,
                    budget,
                    alternateTiming,
                    alternateIntents);
                continue;
            }

            var realization = BuildRealization(
                math,
                objectives,
                budget,
                alternateTiming,
                alternateIntents,
                planningVariant,
                localAttempt: 0);
            if (realization.IsValid)
                return realization;

            lastRealizationFailure = realization;
        }

        return lastRealizationFailure!;
    }

    private ForwardTicketBuildResult BuildRealization(
        ForwardMathInputResult math,
        ForwardObjectiveResult objectives,
        ForwardFeatureBudgetResult budget,
        ForwardFeatureTimingResult timing,
        ForwardFeatureIntentResult intents,
        int planningVariant,
        int localAttempt)
    {
        var frames = new ForwardTurnFramePlanner(
            _settings,
            SeedForLocalGeometry(planningVariant, localAttempt)).Plan(
                budget.Budget,
                intents.Plan,
                objectives.Objectives);
        if (!frames.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.TurnFrameFailed,
                $"{frames.Status}: {frames.Detail}",
                math,
                objectives,
                budget,
                timing,
                intents,
                frames);
        }

        var finalizedObjectives = new ForwardObjectiveFinalizer(_settings).Finalize(
            objectives.Objectives,
            intents.Plan,
            frames.Plan);
        if (!finalizedObjectives.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.ObjectiveFinalizationFailed,
                $"{finalizedObjectives.Status}: {finalizedObjectives.Detail}",
                math,
                objectives,
                budget,
                timing,
                intents,
                frames);
        }

        var finalObjectiveResult = new ForwardObjectiveResult(
            ForwardObjectiveStatus.Valid,
            finalizedObjectives.Detail,
            finalizedObjectives.Objectives);
        var envelope = new ForwardTicketEnvelopeValidator(_settings).Validate(
            finalObjectiveResult.Objectives,
            frames.Plan);
        if (!envelope.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.EnvelopeFailed,
                $"{envelope.Status}: {envelope.Detail}",
                math,
                finalObjectiveResult,
                budget,
                timing,
                intents,
                frames,
                envelope);
        }

        var pipeline = new ForwardTicketPipelineExecutor(
            _settings,
            SeedFor("pipeline")).Execute(finalObjectiveResult.Objectives, frames.Plan);
        if (!pipeline.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.PipelineFailed,
                $"{pipeline.Status}: {pipeline.Detail}",
                math,
                finalObjectiveResult,
                budget,
                timing,
                intents,
                frames,
                envelope,
                pipeline);
        }

        return new ForwardTicketBuildResult(
            ForwardTicketBuildStatus.Valid,
            "ok",
            math,
            finalObjectiveResult,
            budget,
            timing,
            intents,
            frames,
            envelope,
            pipeline);
    }

    private int SeedForLocalGeometry(int planningVariant, int localAttempt)
    {
        var baseSeed = SeedForPlanningVariant("frames", planningVariant);
        if (planningVariant == 0 && localAttempt == 0)
            return baseSeed;

        unchecked
        {
            var seed = (baseSeed * 397) ^ localAttempt;
            seed ^= 0x45d9f3b;
            return seed == int.MinValue ? 0 : Math.Abs(seed);
        }
    }

    private int SeedForPlanningVariant(string scope, int planningVariant)
    {
        var baseSeed = SeedFor(scope);
        if (planningVariant == 0)
            return baseSeed;

        unchecked
        {
            var seed = (baseSeed * 397) ^ planningVariant;
            seed ^= 0x27d4eb2d;
            return seed == int.MinValue ? 0 : Math.Abs(seed);
        }
    }

    private int SeedFor(string scope)
    {
        unchecked
        {
            var hash = _seed;
            foreach (var ch in scope)
                hash = (hash * 397) ^ ch;
            return hash == int.MinValue ? 0 : Math.Abs(hash);
        }
    }

    private static ForwardTicketBuildResult Fail(
        ForwardTicketBuildStatus status,
        string detail,
        ForwardMathInputResult? mathInput = null,
        ForwardObjectiveResult? objectives = null,
        ForwardFeatureBudgetResult? featureBudget = null,
        ForwardFeatureTimingResult? featureTiming = null,
        ForwardFeatureIntentResult? featureIntents = null,
        ForwardTurnFrameResult? turnFrames = null,
        ForwardTicketEnvelopeResult? envelope = null,
        ForwardTicketPipelineResult? pipeline = null) =>
        new(
            status,
            detail,
            mathInput,
            objectives,
            featureBudget,
            featureTiming,
            featureIntents,
            turnFrames,
            envelope,
            pipeline);
}
