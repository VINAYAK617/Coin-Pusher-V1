namespace CoinPusherEngine;

internal enum ForwardTicketBuildStatus
{
    Valid,
    MathInputFailed,
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

    internal ForwardTicketBuildResult Build(IReadOnlyList<decimal>? prizeAmounts)
    {
        var math = new ForwardMathInputResolver(_settings).Resolve(
            prizeAmounts,
            SeedFor("math"));
        if (!math.IsValid)
        {
            return Fail(
                ForwardTicketBuildStatus.MathInputFailed,
                $"{math.Status}: {math.Detail}",
                math);
        }

        var objectives = new ForwardObjectivePlanner(_settings).Resolve(
            math.Bundle!.Input,
            SeedFor("objectives"));
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
            SeedFor("timing")).Plan(budget.Budget);
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
            SeedFor("intents")).Plan(objectives.Objectives, timing.Timing);
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

        var frames = new ForwardTurnFramePlanner(
            _settings,
            SeedFor("frames")).Plan(budget.Budget, intents.Plan, objectives.Objectives);
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
