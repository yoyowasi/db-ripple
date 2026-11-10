using DbRipple.Contracts;
using DbRipple.Diff;

namespace DbRipple.Diff.Tests;

/// <summary>골격 스모크: 구현 클래스가 계약 인터페이스를 구현한다.</summary>
public class SmokeTests
{
    [Fact]
    public void Shell_implements_contract() => Assert.IsAssignableFrom<INetDiffer>(new NetDiffer());
}
