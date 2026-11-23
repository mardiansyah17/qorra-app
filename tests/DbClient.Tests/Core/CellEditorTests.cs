using DbClient.ViewModels;

namespace DbClient.Tests.Core;

public sealed class CellEditorTests
{
    [Theory]
    [InlineData("tinyint(1)", false, CellEditor.Boolean)]
    [InlineData("boolean", false, CellEditor.Boolean)]
    [InlineData("bit", false, CellEditor.Boolean)]
    [InlineData("tinyint(4)", false, CellEditor.Text)]
    [InlineData("enum('a','b')", true, CellEditor.Choice)]
    [InlineData("date", false, CellEditor.Date)]
    [InlineData("datetime(6)", false, CellEditor.DateTime)]
    [InlineData("timestamp without time zone", false, CellEditor.DateTime)]
    [InlineData("datetimeoffset", false, CellEditor.Text)]
    [InlineData("time", false, CellEditor.Time)]
    [InlineData("time(6)", false, CellEditor.Time)]
    [InlineData("time without time zone", false, CellEditor.Time)]
    [InlineData("time with time zone", false, CellEditor.Text)]
    [InlineData("timetz", false, CellEditor.Text)]
    [InlineData("set('red','green')", false, CellEditor.Set)]
    [InlineData("json", false, CellEditor.Json)]
    [InlineData("jsonb", false, CellEditor.Json)]
    [InlineData("longtext", false, CellEditor.LongText)]
    [InlineData("nvarchar(max)", false, CellEditor.LongText)]
    [InlineData("varchar(100)", false, CellEditor.Text)]
    [InlineData(null, false, CellEditor.Text)]
    public void For_PicksTheEditorFromTheType(string? type, bool hasChoices, CellEditor expected) =>
        Assert.Equal(expected, CellEditors.For(type, hasChoices));

    [Fact]
    public void BooleanText_ShowsZeroAndOneAsFalseAndTrue()
    {
        Assert.Equal("true", CellEditors.BooleanText((sbyte)1));
        Assert.Equal("false", CellEditors.BooleanText(0L));
        Assert.Equal("true", CellEditors.BooleanText(true));
        Assert.Null(CellEditors.BooleanText((sbyte)127));
        Assert.Null(CellEditors.BooleanText("1"));
    }

    [Fact]
    public void ParseAndFormat_RoundTripIsoText()
    {
        Assert.Equal(new DateTime(2026, 11, 22, 13, 45, 30).AddTicks(1234560), CellEditors.Parse("2026-11-22 13:45:30.123456"));
        Assert.Equal(new DateTime(2026, 11, 22), CellEditors.Parse("2026-11-22"));
        Assert.Null(CellEditors.Parse("0000-00-00"));
        Assert.Null(CellEditors.Parse("02/01/2026"));
        Assert.Equal("2026-11-22", CellEditors.Format(new DateTime(2026, 11, 22, 10, 0, 0), withTime: false));
        Assert.Equal("2026-11-22 10:00:00", CellEditors.Format(new DateTime(2026, 11, 22, 10, 0, 0), withTime: true));
        Assert.Equal("2026-11-22 10:00:00.5", CellEditors.Format(new DateTime(2026, 11, 22, 10, 0, 0).AddMilliseconds(500), withTime: true));
    }

    [Fact]
    public void ParseAndFormatTime_KeepMySqlRanges()
    {
        Assert.Equal(new TimeSpan(13, 45, 30), CellEditors.ParseTime("13:45:30"));
        Assert.Equal(new TimeSpan(13, 45, 0), CellEditors.ParseTime("13:45"));
        Assert.Equal(new TimeSpan(0, 0, 1).Add(TimeSpan.FromTicks(1234560)), CellEditors.ParseTime("00:00:01.123456"));
        Assert.Equal(-new TimeSpan(838, 59, 59), CellEditors.ParseTime("-838:59:59"));
        Assert.Null(CellEditors.ParseTime("13:61:00"));
        Assert.Null(CellEditors.ParseTime("abc"));
        Assert.Equal("13:45:30", CellEditors.FormatTime(new TimeSpan(13, 45, 30)));
        Assert.Equal("-100:00:00.5", CellEditors.FormatTime(-new TimeSpan(100, 0, 0).Add(TimeSpan.FromMilliseconds(500))));
    }

    [Fact]
    public void SetMembers_AndValue_FollowTheColumnOrder()
    {
        var members = CellEditors.SetMembers("set('red','green','it''s')");
        Assert.Equal(["red", "green", "it's"], members);
        Assert.Empty(CellEditors.SetMembers("varchar(10)"));
        Assert.Equal(["red", "green"], CellEditors.SetValue("green, RED,purple", members));
        Assert.Empty(CellEditors.SetValue(null, members));
    }

    [Fact]
    public void FormatJson_IndentsOrSaysWhereItBreaks()
    {
        Assert.Equal("{\n  \"a\": [\n    1,\n    \"é\"\n  ]\n}", CellEditors.FormatJson("{\"a\":[1,\"é\"]}").Json?.ReplaceLineEndings("\n"));
        var (json, error) = CellEditors.FormatJson("{\"a\": }");
        Assert.Null(json);
        Assert.StartsWith("Invalid JSON: line 1", error);
    }

    [Fact]
    public void Lookup_PicksALabelColumn_AndOnlySearchesKeysThatFit()
    {
        List<DbClient.Core.Models.ColumnInfo> columns =
        [
            new("id", "int", false, true, null), new("code", "varchar(10)", true, false, null),
            new("full_name", "varchar(80)", true, false, null), new("age", "int", true, false, null),
        ];
        Assert.Equal("full_name", TableTabViewModel.LabelColumn(columns, "id"));
        Assert.Equal("code", TableTabViewModel.LabelColumn(columns[..2], "id"));
        Assert.Null(TableTabViewModel.LabelColumn([columns[0], columns[3]], "id"));
        Assert.True(TableTabViewModel.Fits("12", "bigint unsigned"));
        Assert.False(TableTabViewModel.Fits("budi", "int"));
        Assert.True(TableTabViewModel.Fits("budi", "varchar(20)"));
        Assert.False(TableTabViewModel.Fits("budi", "uuid"));
    }

    [Fact]
    public void MonthPage_StartsOnTheWeeksFirstDay()
    {
        var page = CellEditors.MonthPage(2026, 11, DayOfWeek.Monday);
        Assert.Equal(42, page.Length);
        Assert.Equal(new DateTime(2026, 10, 26), page[0]);
        Assert.Equal(new DateTime(2026, 11, 1), page[6]);
    }

    [Fact]
    public void GridRow_PickedTextOfTheSameValueIsNoChange()
    {
        var row = new GridRow([(sbyte)1, new DateTime(2026, 11, 22), new TimeSpan(9, 5, 0)]);
        row.Set(0, "true");
        row.Set(1, "2026-11-22");
        row.Set(2, "09:05:00");
        Assert.Equal(RowState.Unchanged, row.State);
        row.Set(0, "false");
        Assert.Equal(RowState.Modified, row.State);
    }
}
