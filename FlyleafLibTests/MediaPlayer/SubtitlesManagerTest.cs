using FluentAssertions;
using FluentAssertions.Execution;

namespace FlyleafLib.MediaPlayer;

public class SubManagerTests
{
    private readonly SubManager _subManager;

    public SubManagerTests()
    {
        SubManager subManager = new(new Config.SubtitlesConfig(), default, false);
        List<SubtitleData> subsData =
        [
            new() { StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(5), Text = "1. Hello World!" },
            new() { StartTime = TimeSpan.FromSeconds(10), EndTime = TimeSpan.FromSeconds(15), Text = "2. How are you" },
            new() { StartTime = TimeSpan.FromSeconds(20), EndTime = TimeSpan.FromSeconds(25), Text = "3. I'm fine" },
            new() { StartTime = TimeSpan.FromSeconds(28), EndTime = TimeSpan.FromSeconds(29), Text = "4. Thank you" },
            new() { StartTime = TimeSpan.FromSeconds(30), EndTime = TimeSpan.FromSeconds(35), Text = "5. Good bye" }
        ];

        subManager.Load(subsData);

        _subManager = subManager;
    }

    #region Seek
    [Fact]
    public void SubManagerTest_First_Yet()
    {
        // before the first subtitle
        TimeSpan currentTime = TimeSpan.FromSeconds(0.5);
        _subManager.SetCurrentTime(currentTime);

        // 1. Hello World!
        var nextIndex = 0;

        var subsData = _subManager.Subs;

        using (new AssertionScope())
        {
            _subManager.State.Should().Be(SubManager.PositionState.First);
            _subManager.CurrentIndex.Should().Be(-1);

            _subManager.GetCurrent().Should().BeNull();
            _subManager.GetPrev().Should().BeNull();
            _subManager.GetNext().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[nextIndex].Text);
        }
    }

    [Fact]
    public void SubManagerTest_First_Showing()
    {
        // During playback of the first subtitle
        TimeSpan currentTime = TimeSpan.FromSeconds(1);
        _subManager.SetCurrentTime(currentTime);

        // 1. Hello World!
        var curIndex = 0;

        var subsData = _subManager.Subs;

        using (new AssertionScope())
        {
            _subManager.State.Should().Be(SubManager.PositionState.Showing);
            _subManager.CurrentIndex.Should().Be(curIndex);

            _subManager.GetCurrent().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[curIndex].Text);
            _subManager.GetPrev().Should().BeNull();
            _subManager.GetNext().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[curIndex + 1].Text);
        }
    }

    [Fact]
    public void SubManagerTest_Middle_Showing()
    {
        // During playback of the middle subtitle
        TimeSpan currentTime = TimeSpan.FromSeconds(22);
        _subManager.SetCurrentTime(currentTime);

        // 3. I'm fine
        var curIndex = 2;

        var subsData = _subManager.Subs;

        using (new AssertionScope())
        {
            _subManager.State.Should().Be(SubManager.PositionState.Showing);
            _subManager.CurrentIndex.Should().Be(curIndex);

            _subManager.GetCurrent().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[curIndex].Text);
            _subManager.GetPrev().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[curIndex - 1].Text);
            _subManager.GetNext().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[curIndex + 1].Text);
        }
    }

    [Fact]
    public void SubManagerTest_Middle_Yet()
    {
        // just before the middle subtitle
        TimeSpan currentTime = TimeSpan.FromSeconds(18);
        _subManager.SetCurrentTime(currentTime);

        // 3. I'm fine
        var nextIndex = 2;

        var subsData = _subManager.Subs;

        using (new AssertionScope())
        {
            _subManager.State.Should().Be(SubManager.PositionState.Around);
            _subManager.CurrentIndex.Should().Be(nextIndex - 1);

            // Seek falls back to PrevSeek so we can seek, this class returns null
            _subManager.GetCurrent().Should().BeNull();
            _subManager.GetPrev().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[nextIndex - 1].Text);
            _subManager.GetNext().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[nextIndex].Text);
        }
    }

    [Fact]
    public void SubManagerTest_Last_Showing()
    {
        // During playback of the last subtitle
        TimeSpan currentTime = TimeSpan.FromSeconds(35);
        _subManager.SetCurrentTime(currentTime);

        // 5. Good bye
        var curIndex = 4;

        var subsData = _subManager.Subs;

        using (new AssertionScope())
        {
            _subManager.State.Should().Be(SubManager.PositionState.Showing);
            _subManager.CurrentIndex.Should().Be(curIndex);

            _subManager.GetCurrent().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[curIndex].Text);
            _subManager.GetPrev().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[curIndex - 1].Text);
            _subManager.GetNext().Should().BeNull();
        }
    }

    [Fact]
    public void SubManagerTest_Last_Past()
    {
        // after the last subtitle
        TimeSpan currentTime = TimeSpan.FromSeconds(99);
        _subManager.SetCurrentTime(currentTime);

        // 5. Good bye
        var prevIndex = 4;

        var subsData = _subManager.Subs;

        using (new AssertionScope())
        {
            _subManager.State.Should().Be(SubManager.PositionState.Last);
            // OK?
            _subManager.CurrentIndex.Should().Be(prevIndex);

            // Seek falls back to PrevSeek so we can seek, this returns null
            _subManager.GetCurrent().Should().BeNull();
            _subManager.GetPrev().Should()
                .NotBeNull().And
                .Match<SubtitleData>(s => s.Text == subsData[prevIndex].Text);
            _subManager.GetNext().Should().BeNull();
        }
    }
    #endregion

    #region DeleteAfter
    [Fact]
    public void SubManagerTest_DeleteAfter_DeleteFromMiddle()
    {
        _subManager.DeleteAfter(TimeSpan.FromSeconds(20));

        using (new AssertionScope())
        {
            _subManager.Subs.Count.Should().Be(2);
            _subManager.Subs.Select(s => s.Text!.Substring(0, 1))
                .Should().BeEquivalentTo(["1", "2"]);
        }
    }

    [Fact]
    public void SubManagerTest_DeleteAfter_DeleteAll()
    {
        _subManager.DeleteAfter(TimeSpan.FromSeconds(3));

        _subManager.Subs.Count.Should().Be(0);
    }

    [Fact]
    public void SubManagerTest_DeleteAfter_DeleteLast()
    {
        _subManager.DeleteAfter(TimeSpan.FromSeconds(32));

        using (new AssertionScope())
        {
            _subManager.Subs.Count.Should().Be(4);
            _subManager.Subs.Select(s => s.Text!.Substring(0, 1))
                .Should().BeEquivalentTo(["1", "2", "3", "4"]);
        }
    }

    [Fact]
    public void SubManagerTest_DeleteAfter_NoDelete()
    {
        _subManager.DeleteAfter(TimeSpan.FromSeconds(36));

        _subManager.Subs.Count.Should().Be(5);
    }
    #endregion
}
