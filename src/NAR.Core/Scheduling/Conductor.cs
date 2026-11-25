namespace NAR.Core.Scheduling;

public enum ConductorAction
{
    /// <summary>Rien à changer : on continue la source en cours.</summary>
    None,
    /// <summary>Démarrer l'élément <see cref="ConductorDecision.ItemIndex"/> à cette image.</summary>
    Start,
    /// <summary>Plus rien de prévu : passer sur le bouche-trou puis la mire.</summary>
    Fallback,
}

/// <param name="Action">Ce que le moteur doit faire à cette image.</param>
/// <param name="ItemIndex">Élément à démarrer (si Start).</param>
/// <param name="OffsetFrames">Images à sauter dans l'élément (prise en cours de route).</param>
/// <param name="PreloadIndex">Élément à précharger dès maintenant, ou -1.</param>
public readonly record struct ConductorDecision(
    ConductorAction Action,
    int ItemIndex,
    long OffsetFrames,
    int PreloadIndex)
{
    public static ConductorDecision Nothing(int preload = -1) => new(ConductorAction.None, -1, 0, preload);
}

/// <summary>
/// Logique d'enchaînement du conducteur, sans aucune dépendance vidéo :
/// le moteur l'interroge une fois par image. Toute la décision « quoi diffuser »
/// est ici, ce qui la rend testable image par image.
/// </summary>
public sealed class Conductor
{
    private IReadOnlyList<ScheduledItem> _items;
    private bool _loop;
    private readonly long _preloadFrames;

    private bool _started;
    private int _current = -1;
    private long _currentEndFrame;
    private bool _inFallback;
    private int _preloaded = -1;
    private int _cycle;

    public Conductor(IReadOnlyList<ScheduledItem> items, bool loop, long preloadFrames)
    {
        _items = items;
        _loop = loop;
        _preloadFrames = Math.Max(1, preloadFrames);
    }

    public IReadOnlyList<ScheduledItem> Items => _items;
    public int CurrentIndex => _inFallback ? -1 : _current;
    public bool InFallback => _inFallback;
    public int Cycle => _cycle;
    public long CurrentEndFrame => _currentEndFrame;

    /// <summary>
    /// Indice de l'élément qui suivra celui en cours, ou -1.
    /// Les éléments inutilisables (durée nulle : fichier absent ou illisible) sont sautés,
    /// pour que le préchargement vise directement le bon élément.
    /// </summary>
    public int NextIndex
    {
        get
        {
            var i = _current;
            for (var step = 0; step < _items.Count; step++)
            {
                i++;
                if (i >= _items.Count)
                {
                    if (!_loop) return -1;
                    i = 0;
                }
                if (_items[i].DurationFrames > 0) return i;
            }
            return -1;
        }
    }

    /// <summary>Horaire fixe effectif : en boucle, les horaires ne valent que pour le premier passage.</summary>
    public long? FixedStartOf(int index)
    {
        if (index < 0) return null;
        var wraps = index <= _current && _current >= 0;
        var cycle = wraps ? _cycle + 1 : _cycle;
        return cycle == 0 ? _items[index].FixedStartFrame : null;
    }

    /// <summary>
    /// Horaire fixe encore valable : un élément dont toute la plage horaire est déjà passée
    /// (moteur redémarré tard, conducteur en boucle…) n'est plus calé, il s'enchaîne normalement
    /// au lieu de couper ce qui passe pour être aussitôt terminé.
    /// </summary>
    private long? ActiveFixedStart(int index, long frame) =>
        FixedStartOf(index) is long start && frame < start + _items[index].DurationFrames ? start : null;

    /// <param name="frame">Image en cours depuis l'époque du moteur.</param>
    /// <param name="currentEnded">Vrai si la source en cours est terminée (ou en échec).</param>
    public ConductorDecision Tick(long frame, bool currentEnded)
    {
        if (!_started)
        {
            _started = true;
            return Begin(frame);
        }

        var next = NextIndex;

        // 1. Un horaire fixe est atteint : on coupe, quoi qu'il arrive.
        if (next >= 0 && ActiveFixedStart(next, frame) is long fixedStart && frame >= fixedStart)
            return StartAt(next, frame, frame - fixedStart);

        // 2. La source est finie : on enchaîne, ou on passe en secours jusqu'au prochain horaire.
        if (currentEnded && !_inFallback)
        {
            if (next >= 0 && ActiveFixedStart(next, frame) is null)
                return StartAt(next, frame, 0);

            _inFallback = true;
            return new ConductorDecision(ConductorAction.Fallback, -1, 0, PreloadFor(next, frame));
        }

        return ConductorDecision.Nothing(PreloadFor(next, frame));
    }

    private int PreloadFor(int next, long frame)
    {
        if (next < 0 || next == _preloaded) return -1;

        bool due;
        if (ActiveFixedStart(next, frame) is long fixedStart)
            due = frame >= fixedStart - _preloadFrames;
        else
            due = !_inFallback && _current >= 0 && _currentEndFrame - frame <= _preloadFrames;

        if (!due) return -1;
        _preloaded = next;
        return next;
    }

    /// <summary>
    /// Remplace le conducteur à chaud (modification depuis la régie) sans couper l'antenne :
    /// <list type="bullet">
    /// <item>l'élément en cours est retrouvé par son identifiant et continue ;</item>
    /// <item>s'il a été retiré, il va quand même jusqu'au bout, puis on reprend là où la nouvelle
    /// grille dit d'être ;</item>
    /// <item>en secours, on se repositionne immédiatement (un élément peut devenir « en cours »).</item>
    /// </list>
    /// </summary>
    public void Reload(IReadOnlyList<ScheduledItem> items, bool loop)
    {
        var currentId = !_inFallback && _current >= 0 && _current < _items.Count ? _items[_current].Id : null;
        _items = items;
        _loop = loop;
        _preloaded = -1;
        if (!_started) return;

        if (_inFallback)
        {
            // Le prochain Tick refait le positionnement complet (Begin).
            _started = false;
            _inFallback = false;
            _current = -1;
            return;
        }

        var idx = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == currentId) { idx = i; break; }
        }
        _current = idx >= 0 ? idx : PositionAt(_currentEndFrame) - 1;
    }

    /// <summary>
    /// Indice de l'élément qui est (ou sera ensuite) à l'antenne à l'image donnée, d'après la grille.
    /// Retourne <c>Count</c> si tout est passé.
    /// </summary>
    private int PositionAt(long frame)
    {
        long? cursor = null;
        for (var i = 0; i < _items.Count; i++)
        {
            var start = _items[i].FixedStartFrame ?? cursor ?? frame;
            var end = start + _items[i].DurationFrames;
            if (start > frame || frame < end) return i;
            cursor = end;
        }
        return _loop ? 0 : _items.Count;
    }

    /// <summary>
    /// Premier appel : retrouve où l'on en est dans le conducteur (reprise après redémarrage).
    /// </summary>
    private ConductorDecision Begin(long frame)
    {
        long? cursor = null;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var start = item.FixedStartFrame ?? cursor ?? frame;

            if (start > frame)
            {
                // Rien n'est à l'antenne en ce moment : secours jusqu'à cet élément.
                _current = i - 1;
                _inFallback = true;
                return new ConductorDecision(ConductorAction.Fallback, -1, 0, PreloadFor(i, frame));
            }

            var end = start + item.DurationFrames;
            if (frame < end)
                return StartAt(i, frame, frame - start);

            cursor = end;
        }

        if (_loop && _items.Count > 0)
            return StartAt(0, frame, 0, newCycle: true);

        _current = _items.Count - 1;
        _inFallback = true;
        return new ConductorDecision(ConductorAction.Fallback, -1, 0, -1);
    }

    private ConductorDecision StartAt(int index, long frame, long offset, bool newCycle = false)
    {
        if (newCycle || (_current >= 0 && index <= _current))
            _cycle++;

        _current = index;
        _inFallback = false;
        if (_preloaded == index) _preloaded = -1;
        offset = Math.Max(0, offset);
        _currentEndFrame = frame + Math.Max(0, _items[index].DurationFrames - offset);
        return new ConductorDecision(ConductorAction.Start, index, offset, -1);
    }
}
