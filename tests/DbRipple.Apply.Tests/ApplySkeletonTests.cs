using DbRipple.Apply;
using DbRipple.Contracts;

namespace DbRipple.Apply.Tests;

/// <summary>골격 확인(3주차 apply-1이 실제 테스트로 바꾼다). DB 없이 돈다.</summary>
public sealed class ApplySkeletonTests
{
    [Fact]
    public void Settings_defaults_follow_design()
    {
        var s = new ApplySettings();
        Assert.Equal("_pre", s.OriginSnapshotSuffix);           // §5.3 AW_Origin_pre
        Assert.Equal(TimeSpan.FromMinutes(30), s.PreviewExpiry); // §5.3 선행 조건 30분
        Assert.False(s.ForceKeyset);
    }

    [Fact]
    public void SqlHash_matches_design_format()
    {
        var h = ContractFormulas.SqlHash("DELETE FROM dbo.T WHERE id = 1;");
        Assert.StartsWith("sha256:", h);
        Assert.Equal("sha256:".Length + 64, h.Length);
    }
}
