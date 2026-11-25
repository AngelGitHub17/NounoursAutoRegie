using NAR.Core.Scheduling;

namespace NAR.Core.Tests;

public class ConductorTests
{
    private static ScheduledItem Item(int index, long duration, long? fixedStart = null) =>
        new(index, $"id{index}", $"Élément {index}", $"/media/{index}.mp4", 0, duration, fixedStart);

    /// <summary>Simule le moteur : une source se termine quand sa durée est écoulée.</summary>
    private static List<(long Frame, ConductorDecision Decision)> Run(Conductor conductor, IReadOnlyList<ScheduledItem> items, long frames)
    {
        var log = new List<(long, ConductorDecision)>();
        long playedUntil = -1;
        for (long f = 0; f < frames; f++)
        {
            var ended = conductor.InFallback || conductor.CurrentIndex < 0 || f >= playedUntil;
            var d = conductor.Tick(f, ended);
            if (d.Action == ConductorAction.Start)
                playedUntil = f + items[d.ItemIndex].DurationFrames - d.OffsetFrames;
            if (d.Action != ConductorAction.None || d.PreloadIndex >= 0)
                log.Add((f, d));
        }
        return log;
    }

    [Fact]
    public void Enchaine_les_elements_a_l_image_pres()
    {
        var items = new[] { Item(0, 100), Item(1, 50), Item(2, 75) };
        var log = Run(new Conductor(items, loop: false, preloadFrames: 10), items, 300);
        var starts = log.Where(l => l.Decision.Action == ConductorAction.Start).Select(l => (l.Frame, l.Decision.ItemIndex)).ToList();
        Assert.Equal([(0L, 0), (100L, 1), (150L, 2)], starts);
    }

    [Fact]
    public void Passe_en_secours_apres_le_dernier_element()
    {
        var items = new[] { Item(0, 40) };
        var log = Run(new Conductor(items, loop: false, preloadFrames: 10), items, 100);
        Assert.Contains(log, l => l.Frame == 40 && l.Decision.Action == ConductorAction.Fallback);
    }

    [Fact]
    public void Precharge_le_suivant_avant_la_fin()
    {
        var items = new[] { Item(0, 100), Item(1, 50) };
        var log = Run(new Conductor(items, loop: false, preloadFrames: 25), items, 120);
        Assert.Contains(log, l => l.Frame == 75 && l.Decision.PreloadIndex == 1);
    }

    [Fact]
    public void Horaire_fixe_coupe_le_programme_en_cours()
    {
        // Le JT est calé à l'image 500 même si le film n'est pas fini.
        var items = new[] { Item(0, 1000), Item(1, 100, fixedStart: 500) };
        var log = Run(new Conductor(items, loop: false, preloadFrames: 50), items, 600);
        Assert.Contains(log, l => l.Frame == 450 && l.Decision.PreloadIndex == 1);
        Assert.Contains(log, l => l.Frame == 500 && l.Decision.Action == ConductorAction.Start && l.Decision.ItemIndex == 1);
    }

    [Fact]
    public void Attend_l_horaire_fixe_en_secours_puis_demarre_pile()
    {
        var items = new[] { Item(0, 100), Item(1, 100, fixedStart: 250) };
        var log = Run(new Conductor(items, loop: false, preloadFrames: 50), items, 300);
        Assert.Contains(log, l => l.Frame == 100 && l.Decision.Action == ConductorAction.Fallback);
        Assert.Contains(log, l => l.Frame == 250 && l.Decision.Action == ConductorAction.Start && l.Decision.ItemIndex == 1);
    }

    [Fact]
    public void Reprend_en_cours_de_route_apres_un_redemarrage()
    {
        // Le moteur redémarre à l'image 130 : le 2e élément (100 → 200) est repris à +30 images.
        var items = new[] { Item(0, 100, fixedStart: 0), Item(1, 100) };
        var conductor = new Conductor(items, loop: false, preloadFrames: 10);
        var d = conductor.Tick(130, currentEnded: true);
        Assert.Equal(ConductorAction.Start, d.Action);
        Assert.Equal(1, d.ItemIndex);
        Assert.Equal(30, d.OffsetFrames);
    }

    [Fact]
    public void Demarre_en_secours_si_le_premier_horaire_est_dans_le_futur()
    {
        var items = new[] { Item(0, 100, fixedStart: 1000) };
        var conductor = new Conductor(items, loop: false, preloadFrames: 10);
        Assert.Equal(ConductorAction.Fallback, conductor.Tick(0, currentEnded: true).Action);
    }

    [Fact]
    public void En_boucle_recommence_au_debut_sans_reutiliser_les_horaires()
    {
        var items = new[] { Item(0, 30, fixedStart: 0), Item(1, 20) };
        var log = Run(new Conductor(items, loop: true, preloadFrames: 5), items, 120);
        var starts = log.Where(l => l.Decision.Action == ConductorAction.Start).Select(l => (l.Frame, l.Decision.ItemIndex)).ToList();
        Assert.Equal([(0L, 0), (30L, 1), (50L, 0), (80L, 1), (100L, 0)], starts);
    }

    [Fact]
    public void Saute_un_element_inutilisable_et_precharge_le_suivant()
    {
        // Un élément de durée nulle (fichier absent) n'est jamais démarré : on enchaîne sur le suivant sans perdre d'image.
        var items = new[] { Item(0, 50), Item(1, 0), Item(2, 50) };
        var log = Run(new Conductor(items, loop: false, preloadFrames: 5), items, 120);
        var starts = log.Where(l => l.Decision.Action == ConductorAction.Start).Select(l => (l.Frame, l.Decision.ItemIndex)).ToList();
        Assert.Equal([(0L, 0), (50L, 2)], starts);
        Assert.Contains(log, l => l.Frame == 45 && l.Decision.PreloadIndex == 2);
    }

    [Fact]
    public void Source_en_echec_en_cours_de_route_enchaine_immediatement()
    {
        var items = new[] { Item(0, 100), Item(1, 50) };
        var conductor = new Conductor(items, loop: false, preloadFrames: 10);
        Assert.Equal(ConductorAction.Start, conductor.Tick(0, currentEnded: true).Action);
        for (long f = 1; f < 30; f++) conductor.Tick(f, currentEnded: false);
        var d = conductor.Tick(30, currentEnded: true); // la source a planté à l'image 30
        Assert.Equal(ConductorAction.Start, d.Action);
        Assert.Equal(1, d.ItemIndex);
    }
}
