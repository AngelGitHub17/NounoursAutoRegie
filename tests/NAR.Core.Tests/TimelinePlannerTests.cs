using NAR.Core.Scheduling;

namespace NAR.Core.Tests;

public class TimelinePlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 20, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Enchaine_apres_un_horaire_fixe()
    {
        var plan = TimelinePlanner.Plan([
            new PlanInput("JT", TimeSpan.FromMinutes(30), T0),
            new PlanInput("Météo", TimeSpan.FromMinutes(5), null),
        ]);
        Assert.Equal(T0.AddMinutes(30), plan[1].Start);
    }

    [Fact]
    public void Horaire_fixe_coupe_le_precedent()
    {
        var plan = TimelinePlanner.Plan([
            new PlanInput("Film", TimeSpan.FromMinutes(100), T0),
            new PlanInput("JT", TimeSpan.FromMinutes(30), T0.AddMinutes(90)),
        ]);
        Assert.Equal(TimeSpan.FromMinutes(90), plan[0].Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), plan[0].CutBy);
    }

    [Fact]
    public void Trou_avant_un_horaire_fixe_signale_le_bouche_trou()
    {
        var plan = TimelinePlanner.Plan([
            new PlanInput("Clip", TimeSpan.FromMinutes(3), T0),
            new PlanInput("JT", TimeSpan.FromMinutes(30), T0.AddMinutes(5)),
        ]);
        Assert.Equal(TimeSpan.FromMinutes(2), plan[1].GapBefore);
    }

    [Fact]
    public void Sans_horaire_connu_les_positions_sont_relatives()
    {
        var plan = TimelinePlanner.Plan([
            new PlanInput("A", TimeSpan.FromMinutes(3), null),
            new PlanInput("B", TimeSpan.FromMinutes(4), null),
        ]);
        Assert.Null(plan[1].Start);
        Assert.Equal(TimeSpan.FromMinutes(3), plan[1].Offset);
    }
}
