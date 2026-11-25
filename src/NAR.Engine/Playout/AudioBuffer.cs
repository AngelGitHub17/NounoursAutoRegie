namespace NAR.Engine.Playout;

/// <summary>
/// Tampon circulaire PCM borné, partagé entre un lecteur réseau (écriture) et le moteur (lecture).
/// L'écriture bloque quand le tampon est plein : c'est la contre-pression qui freine le décodeur.
/// </summary>
public sealed class AudioBuffer
{
    private readonly byte[] _ring;
    private readonly object _lock = new();
    private int _readPos;
    private int _count;
    private bool _completed;
    private bool _cancelled;

    public AudioBuffer(int capacityBytes) => _ring = new byte[capacityBytes];

    public int Available { get { lock (_lock) return _count; } }
    public bool IsCompleted { get { lock (_lock) return _completed && _count == 0; } }

    /// <summary>Écrit tout le bloc, en attendant de la place si nécessaire. Retourne faux si annulé.</summary>
    public bool Write(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        lock (_lock)
        {
            while (offset < data.Length)
            {
                while (_count == _ring.Length && !_cancelled)
                    Monitor.Wait(_lock, 250);
                if (_cancelled) return false;

                var writePos = (_readPos + _count) % _ring.Length;
                var chunk = Math.Min(data.Length - offset, Math.Min(_ring.Length - _count, _ring.Length - writePos));
                data.Slice(offset, chunk).CopyTo(_ring.AsSpan(writePos, chunk));
                _count += chunk;
                offset += chunk;
            }
            Monitor.PulseAll(_lock);
        }
        return true;
    }

    /// <summary>Lit au plus <c>destination.Length</c> octets sans attendre. Retourne le nombre lu.</summary>
    public int Read(Span<byte> destination)
    {
        lock (_lock)
        {
            var total = Math.Min(destination.Length, _count);
            var done = 0;
            while (done < total)
            {
                var chunk = Math.Min(total - done, _ring.Length - _readPos);
                _ring.AsSpan(_readPos, chunk).CopyTo(destination.Slice(done, chunk));
                _readPos = (_readPos + chunk) % _ring.Length;
                _count -= chunk;
                done += chunk;
            }
            if (done > 0) Monitor.PulseAll(_lock);
            return done;
        }
    }

    /// <summary>Jette jusqu'à <paramref name="bytes"/> octets (rattrapage de synchro). Retourne le nombre jeté.</summary>
    public int Skip(int bytes)
    {
        lock (_lock)
        {
            var n = Math.Min(bytes, _count);
            _readPos = (_readPos + n) % _ring.Length;
            _count -= n;
            if (n > 0) Monitor.PulseAll(_lock);
            return n;
        }
    }

    public void Complete()
    {
        lock (_lock) { _completed = true; Monitor.PulseAll(_lock); }
    }

    public void Cancel()
    {
        lock (_lock) { _cancelled = true; _completed = true; Monitor.PulseAll(_lock); }
    }
}
