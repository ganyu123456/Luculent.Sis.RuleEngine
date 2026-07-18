using Luculent.Sis.RuleEngine.Shared.Interfaces;
using Luculent.Sis.RuleEngine.Shared.Models;
using Luculent.Sis.RuleEngine.Worker.Calculation;
using Luculent.Sis.RuleEngine.Worker.Storage;
using Microsoft.Extensions.Logging;
using Moq;

namespace Luculent.Sis.RuleEngine.Tests.Calculation;

public class PreruleEvaluationService_Tests
{
    private readonly PreruleDefinitionStore _defStore;
    private readonly PreruleStateStore _stateStore;
    private readonly Mock<ITrendDataReader> _trendReaderMock;
    private readonly PreruleEvaluationService _service;

    public PreruleEvaluationService_Tests()
    {
        _defStore = new PreruleDefinitionStore();
        _stateStore = new PreruleStateStore();
        _trendReaderMock = new Mock<ITrendDataReader>();
        _service = new PreruleEvaluationService(
            _defStore,
            _stateStore,
            _trendReaderMock.Object,
            Mock.Of<ILogger<PreruleEvaluationService>>());
    }

    private void LoadDef(PreruleDefinition def)
        => _defStore.LoadAll(new List<PreruleDefinition> { def });

    // ===== EvaluateRangeDuration: BreakOnHit =====

    [Fact]
    public async Task EvaluateRangeDuration_BreakOnHitTrue_ReturnsTrueImmediately()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-1",
            RuleType = 2,
            IsEnabled = true,
            MonitorSources = new List<PreruleSourceDefinition>
            {
                new() { Key = "val", SourceType = 1, SourceKey = "100" },
                new() { Key = "thr", SourceType = 1, SourceKey = "50" },
            },
            RuleRangeDurations = new List<PreruleRangeDurationDefinition>
            {
                new()
                {
                    Id = "rule-1",
                    LeftSourceKey = "100",
                    RightSourceKey = "50",
                    SymbolType = 2, // >=
                    DurationSecond = 1,
                    BreakOnHit = true,
                    IsEnabled = true,
                    Priority = 1,
                },
            },
        });

        // First call: condition met but duration < 1s
        await _service.EvaluateAllAsync();
        Assert.False(_stateStore.GetState("prerule-1"));

        // Wait for duration
        await Task.Delay(1100);

        // Second call: duration elapsed + BreakOnHit → true
        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-1"));
    }

    [Fact]
    public async Task EvaluateRangeDuration_BreakOnHitFalse_ReturnsTrueAfterDuration()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-2",
            RuleType = 2,
            IsEnabled = true,
            MonitorSources = new List<PreruleSourceDefinition>
            {
                new() { Key = "val", SourceType = 1, SourceKey = "100" },
                new() { Key = "thr", SourceType = 1, SourceKey = "50" },
            },
            RuleRangeDurations = new List<PreruleRangeDurationDefinition>
            {
                new()
                {
                    Id = "rule-2",
                    LeftSourceKey = "100",
                    RightSourceKey = "50",
                    SymbolType = 2, // >=
                    DurationSecond = 1,
                    BreakOnHit = false,
                    IsEnabled = true,
                    Priority = 1,
                },
            },
        });

        // First call: condition met but duration not elapsed
        await _service.EvaluateAllAsync();
        Assert.False(_stateStore.GetState("prerule-2"));

        // Wait for duration
        await Task.Delay(1100);

        // Duration elapsed + BreakOnHit=false → still true (verifies F1 fix)
        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-2"));
    }

    [Fact]
    public async Task EvaluateRangeDuration_NoMatchingCondition_ReturnsFalse()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-3",
            RuleType = 2,
            IsEnabled = true,
            MonitorSources = new List<PreruleSourceDefinition>
            {
                new() { Key = "val", SourceType = 1, SourceKey = "10" },
                new() { Key = "thr", SourceType = 1, SourceKey = "50" },
            },
            RuleRangeDurations = new List<PreruleRangeDurationDefinition>
            {
                new()
                {
                    Id = "rule-3",
                    LeftSourceKey = "10",
                    RightSourceKey = "50",
                    SymbolType = 1, // > (10 > 50 = false)
                    DurationSecond = 0,
                    IsEnabled = true,
                    Priority = 1,
                },
            },
        });

        await _service.EvaluateAllAsync();
        Assert.False(_stateStore.GetState("prerule-3"));
    }

    [Fact]
    public async Task EvaluateRangeDuration_EmptyRules_ReturnsFalse()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-4",
            RuleType = 2,
            IsEnabled = true,
        });

        await _service.EvaluateAllAsync();
        Assert.False(_stateStore.GetState("prerule-4"));
    }

    [Fact]
    public async Task EvaluateRangeDuration_DisabledRule_Skipped()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-5",
            RuleType = 2,
            IsEnabled = true,
            MonitorSources = new List<PreruleSourceDefinition>
            {
                new() { Key = "val", SourceType = 1, SourceKey = "100" },
                new() { Key = "thr", SourceType = 1, SourceKey = "50" },
            },
            RuleRangeDurations = new List<PreruleRangeDurationDefinition>
            {
                new()
                {
                    Id = "rule-4",
                    LeftSourceKey = "100",
                    RightSourceKey = "50",
                    SymbolType = 2,
                    DurationSecond = 0,
                    IsEnabled = false,
                    Priority = 1,
                },
            },
        });

        await _service.EvaluateAllAsync();
        Assert.False(_stateStore.GetState("prerule-5"));
    }

    [Fact]
    public async Task EvaluateRangeDuration_MultipleRules_FirstHitBreaksWithBreakOnHit()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-multi",
            RuleType = 2,
            IsEnabled = true,
            MonitorSources = new List<PreruleSourceDefinition>
            {
                new() { Key = "v1", SourceType = 1, SourceKey = "100" },
                new() { Key = "v2", SourceType = 1, SourceKey = "50" },
            },
            RuleRangeDurations = new List<PreruleRangeDurationDefinition>
            {
                new()
                {
                    Id = "r1", LeftSourceKey = "100", RightSourceKey = "50",
                    SymbolType = 2, DurationSecond = 0, Priority = 1, BreakOnHit = true, IsEnabled = true,
                },
                new()
                {
                    Id = "r2", LeftSourceKey = "100", RightSourceKey = "50",
                    SymbolType = 2, DurationSecond = 0, Priority = 2, BreakOnHit = true, IsEnabled = true,
                },
            },
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-multi"));
    }

    // ===== EvaluateExpression =====

    [Fact]
    public async Task EvaluateExpression_ValidComparison_ReturnsTrue()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-expr-1",
            RuleType = 1,
            IsEnabled = true,
            RuleExpression = new PreruleExpressionDefinition { Id = "exp-1", Code = "80 > 50" },
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-expr-1"));
    }

    [Fact]
    public async Task EvaluateExpression_InvalidComparison_ReturnsFalse()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-expr-2",
            RuleType = 1,
            IsEnabled = true,
            RuleExpression = new PreruleExpressionDefinition { Id = "exp-2", Code = "10 > 50" },
        });

        await _service.EvaluateAllAsync();
        Assert.False(_stateStore.GetState("prerule-expr-2"));
    }

    [Fact]
    public async Task EvaluateExpression_NoExpression_ReturnsFalse()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-expr-3",
            RuleType = 1,
            IsEnabled = true,
        });

        await _service.EvaluateAllAsync();
        Assert.False(_stateStore.GetState("prerule-expr-3"));
    }

    [Fact]
    public async Task EvaluateExpression_AndComposition_ReturnsTrue()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-expr-4",
            RuleType = 1,
            IsEnabled = true,
            RuleExpression = new PreruleExpressionDefinition { Id = "exp-4", Code = "80 > 50 && 10 < 20" },
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-expr-4"));
    }

    [Fact]
    public async Task EvaluateExpression_OrComposition_ReturnsCorrect()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-expr-5",
            RuleType = 1,
            IsEnabled = true,
            RuleExpression = new PreruleExpressionDefinition { Id = "exp-5", Code = "10 > 50 || 20 > 10" },
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-expr-5"));
    }

    [Fact]
    public async Task EvaluateExpression_EqualityCheck_ReturnsCorrect()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-eq",
            RuleType = 1,
            IsEnabled = true,
            RuleExpression = new PreruleExpressionDefinition { Id = "exp-eq", Code = "100 == 100" },
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-eq"));
    }

    // ===== EvaluateOne: general =====

    [Fact]
    public async Task EvaluateAllAsync_NotEnabled_ReturnsTrue()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-disabled",
            IsEnabled = false,
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-disabled"));
    }

    [Fact]
    public async Task EvaluateAllAsync_UnknownRuleType_ReturnsTrue()
    {
        LoadDef(new PreruleDefinition
        {
            Id = "prerule-unknown",
            RuleType = 99,
            IsEnabled = true,
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-unknown"));
    }

    // ===== FetchSourceData: Static vs RealDB =====

    [Fact]
    public async Task FetchSourceData_StaticSource_ParsesSourceKeyDirectly()
    {
        _trendReaderMock.Reset();

        LoadDef(new PreruleDefinition
        {
            Id = "prerule-static",
            RuleType = 1,
            IsEnabled = true,
            MonitorSources = new List<PreruleSourceDefinition>
            {
                new() { Key = "threshold", SourceType = 1, SourceKey = "80" },
            },
            RuleExpression = new PreruleExpressionDefinition { Id = "exp", Code = "threshold > 50" },
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-static"));

        // Static sources should NOT call TrendDB
        _trendReaderMock.Verify(x => x.ReadBatchAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task FetchSourceData_RealDB_UsesSourceKeyAsTagName()
    {
        _trendReaderMock
            .Setup(x => x.ReadBatchAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, double?> { ["TAG_001"] = 75.0 });

        LoadDef(new PreruleDefinition
        {
            Id = "prerule-realdb",
            RuleType = 1,
            IsEnabled = true,
            MonitorSources = new List<PreruleSourceDefinition>
            {
                new() { Key = "temp", SourceType = 3, SourceKey = "TAG_001" },
            },
            RuleExpression = new PreruleExpressionDefinition { Id = "exp", Code = "temp > 50" },
        });

        await _service.EvaluateAllAsync();
        Assert.True(_stateStore.GetState("prerule-realdb"));

        // SourceKey (not Key/alias) should be used as TrendDB tag name
        _trendReaderMock.Verify(x => x.ReadBatchAsync(It.Is<IEnumerable<string>>(
            keys => keys.Contains("TAG_001"))), Times.Once);
    }

    [Fact]
    public async Task FetchSourceData_RealDBFailure_ReturnsFalse()
    {
        _trendReaderMock
            .Setup(x => x.ReadBatchAsync(It.IsAny<IEnumerable<string>>()))
            .ThrowsAsync(new Exception("TrendDB down"));

        LoadDef(new PreruleDefinition
        {
            Id = "prerule-fail",
            RuleType = 1,
            IsEnabled = true,
            MonitorSources = new List<PreruleSourceDefinition>
            {
                new() { Key = "temp", SourceType = 3, SourceKey = "TAG_001" },
            },
            RuleExpression = new PreruleExpressionDefinition { Id = "exp", Code = "temp > 50" },
        });

        // Should not throw — catches and logs
        await _service.EvaluateAllAsync();
        Assert.False(_stateStore.GetState("prerule-fail"));
    }

    // ===== EvaluateAll: 批量 =====

    [Fact]
    public async Task EvaluateAllAsync_UpdatesAllStates()
    {
        _defStore.LoadAll(new List<PreruleDefinition>
        {
            new()
            {
                Id = "all-1", RuleType = 1, IsEnabled = true,
                RuleExpression = new PreruleExpressionDefinition { Id = "e1", Code = "100 > 50" },
            },
            new()
            {
                Id = "all-2", RuleType = 1, IsEnabled = true,
                RuleExpression = new PreruleExpressionDefinition { Id = "e2", Code = "10 > 50" },
            },
        });

        await _service.EvaluateAllAsync();

        Assert.True(_stateStore.GetState("all-1"));
        Assert.False(_stateStore.GetState("all-2"));
    }

    [Fact]
    public async Task EvaluateAllAsync_EmptyDefinitions_NoOp()
    {
        // No definitions loaded — should not throw
        await _service.EvaluateAllAsync();
    }
}
