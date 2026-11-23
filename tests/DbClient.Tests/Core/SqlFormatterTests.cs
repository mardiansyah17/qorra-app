using DbClient.Core;

namespace DbClient.Tests.Core;

public sealed class SqlFormatterTests
{
    [Fact]
    public void PutsClausesOnTheirOwnLines() =>
        Assert.Equal(
            "SELECT a,\n    b.c,\n    COUNT(*)\nFROM t\nLEFT JOIN u b ON b.id = t.id\nWHERE t.x >= 1\n    AND t.y BETWEEN 1 AND 2\nGROUP BY a, b.c\nORDER BY a DESC",
            Format("select a, b.c, COUNT(*) from t left join u b on b.id=t.id where t.x>=1 and t.y between 1 and 2 group by a, b.c order by a desc"));

    [Fact]
    public void IndentsSubqueriesAndKeepsInlineParens() =>
        Assert.Equal(
            "SELECT *\nFROM t\nWHERE id IN (\n    SELECT id\n    FROM u\n    WHERE n = COALESCE(x, 1)\n)",
            Format("select * from t where id in (select id from u where n = COALESCE(x, 1))"));

    [Fact]
    public void KeepsStringsCommentsAndOperators() =>
        Assert.Equal(
            "SELECT 'from where' AS \"Select\",\n    a::int,\n    -1 -- note\nFROM t\nWHERE b <> $1;\n\nUPDATE t\nSET a = 1,\n    b = :p",
            Format("select 'from where' as \"Select\", a::int, -1 -- note\nfrom t where b<>$1; update t set a = 1, b = :p"));

    [Fact]
    public void LeavesForeignKeyActionsAndCaseInline() =>
        Assert.Equal(
            "SELECT CASE WHEN a AND b THEN 1 END\nFROM t;\n\nDELETE FROM t\nWHERE x = 1",
            Format("select case when a and b then 1 end from t; delete from t where x = 1"));

    [Fact]
    public void KeepsGoSeparatorsOnTheirOwnLine() =>
        Assert.Equal("SELECT 1\nGO\n\nSELECT go\nFROM t", Format("select 1\ngo\nselect go from t"));

    private static string Format(string sql) => SqlFormatter.Format(sql);
}
