using Contexo.Core.Common;

namespace Contexo.Core.Tests.Common;

public sealed class AppVersionTests
{
    [Fact]
    public void Parses_the_build_metadata_style_with_git_describe_suffix()
    {
        var info = AppVersion.Parse("1.4.2+37.g3f2a9c1");

        Assert.Equal("1.4.2", info.Version);
        Assert.Equal("3f2a9c1", info.CommitSha);
        Assert.Equal("1.4.2+37.g3f2a9c1", info.InformationalVersion);
    }

    [Fact]
    public void Parses_the_minver_default_style_with_prerelease_and_full_sha()
    {
        var info = AppVersion.Parse("1.4.2-alpha.0.37+3F2A9C1A2B3C4D5E6F708192A3B4C5D6E7F80910");

        Assert.Equal("1.4.2-alpha.0.37", info.Version);
        Assert.Equal("3f2a9c1a2b3c4d5e6f708192a3b4c5d6e7f80910", info.CommitSha);
    }

    [Fact]
    public void Parses_the_minver_style_with_short_sha()
    {
        var info = AppVersion.Parse("0.0.0-alpha.0.12+3f2a9c1");

        Assert.Equal("0.0.0-alpha.0.12", info.Version);
        Assert.Equal("3f2a9c1", info.CommitSha);
    }

    [Fact]
    public void A_tagged_release_has_no_commit_sha()
    {
        var info = AppVersion.Parse("1.0.0");

        Assert.Equal("1.0.0", info.Version);
        Assert.Null(info.CommitSha);
    }

    [Fact]
    public void A_leading_v_is_dropped()
    {
        Assert.Equal("2.0.1", AppVersion.Parse("v2.0.1").Version);
    }

    [Fact]
    public void Unrecognised_text_is_returned_as_is()
    {
        var info = AppVersion.Parse("dev build");

        Assert.Equal("dev build", info.Version);
        Assert.Null(info.CommitSha);
    }

    [Fact]
    public void Build_date_is_passed_through()
    {
        var date = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(date, AppVersion.Parse("1.0.0", date).BuildDate);
    }

    [Fact]
    public void The_core_assembly_has_a_parseable_version()
    {
        var info = AppVersion.FromAssembly(typeof(AppVersion).Assembly);

        Assert.Matches(@"^\d+\.\d+\.\d+", info.Version);
        Assert.NotEmpty(info.InformationalVersion);
    }

    [Fact]
    public void Current_returns_a_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Current.Version);
    }
}
