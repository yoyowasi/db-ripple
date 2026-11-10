using DbRipple.Contracts;
using DbRipple.Core.Capture;
using DbRipple.Execution;

namespace DbRipple.Execution.Tests;

/// <summary>골격 스모크: 구현 클래스가 계약 인터페이스를 구현하고, C12 없이 가짜 INetDiffer로 조립된다.</summary>
public class SmokeTests
{
    sealed class FakeNetDiffer : INetDiffer
    {
        public Task<IReadOnlyList<NetChange>> DiffAsync(SqlSession session, string liveDb, string snapshotDb, IReadOnlyList<TableInfo> tables, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<NetChange>>([]);
    }

    [Fact]
    public void Shells_implement_contracts()
    {
        Assert.IsAssignableFrom<IPreviewExecutor>(new PreviewExecutor("Server=unused", new ExecutionSettings(), new FakeNetDiffer()));
        Assert.IsAssignableFrom<IChangeCollector>(new ChangeCollector());
    }
}
