using NewsDesk.Core.Models;

namespace NewsDesk.Tests;

public class HeadlineListTextTests
{
    [Fact]
    public void Parses_TitleAndCaption_PerLine()
    {
        var items = HeadlineListText.Parse("CLASS OF 2026 SAYS FAREWELL | Final assembly in the main hall\r\nSPORTS DAY RESULTS|Blue house takes the cup\n");

        Assert.Equal(2, items.Count);
        Assert.Equal(("CLASS OF 2026 SAYS FAREWELL", "Final assembly in the main hall"), items[0]);
        Assert.Equal(("SPORTS DAY RESULTS", "Blue house takes the cup"), items[1]);
    }

    [Fact]
    public void Accepts_TabSeparatedColumns_PastedFromExcel()
    {
        var items = HeadlineListText.Parse("PRIZE GIVING\tTop students honoured\nNEW LIBRARY OPENS\tDonated by the alumni");

        Assert.Equal(("PRIZE GIVING", "Top students honoured"), items[0]);
        Assert.Equal(("NEW LIBRARY OPENS", "Donated by the alumni"), items[1]);
    }

    [Fact]
    public void Ignores_BlankLines_AndStripsListNumbering()
    {
        var items = HeadlineListText.Parse("\n1. HEAD PREFECT SPEAKS | A message to the juniors\n\n2) TEACHERS' TRIBUTE\n   \n");

        Assert.Equal(2, items.Count);
        Assert.Equal(("HEAD PREFECT SPEAKS", "A message to the juniors"), items[0]);
        Assert.Equal(("TEACHERS' TRIBUTE", ""), items[1]);
    }

    [Fact]
    public void KeepsYearsAndNumbersThatArePartOfTheHeadline()
    {
        var items = HeadlineListText.Parse("2026 CLASS PHOTO | Taken at dawn\n10 YEARS OF EMPYREAN");

        Assert.Equal("2026 CLASS PHOTO", items[0].Title);
        Assert.Equal("10 YEARS OF EMPYREAN", items[1].Title);
    }

    [Fact]
    public void Format_RoundTripsThroughParse()
    {
        var source = new[]
        {
            new HeadlineSequenceItem { Title = "FAREWELL DINNER", Subtitle = "Tonight at seven" },
            new HeadlineSequenceItem { Title = "AWARDS" }
        };

        var parsed = HeadlineListText.Parse(HeadlineListText.Format(source));

        Assert.Equal(("FAREWELL DINNER", "Tonight at seven"), parsed[0]);
        Assert.Equal(("AWARDS", ""), parsed[1]);
    }
}
