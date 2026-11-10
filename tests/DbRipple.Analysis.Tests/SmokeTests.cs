using DbRipple.Analysis;
using DbRipple.Contracts;

namespace DbRipple.Analysis.Tests;

/// <summary>골격 스모크: 구현 클래스가 계약 인터페이스를 구현한다. 구현이 들어오면 실제 테스트로 늘린다.</summary>
public class SmokeTests
{
    [Fact]
    public void Shells_implement_contracts()
    {
        Assert.IsAssignableFrom<IBatchAnalyzer>(new BatchAnalyzer());
        Assert.IsAssignableFrom<ITriggerAnalyzer>(new TriggerAnalyzer());
    }
}
