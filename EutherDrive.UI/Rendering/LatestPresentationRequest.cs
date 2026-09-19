using System.Threading;

namespace EutherDrive.Rendering;

internal sealed class LatestPresentationRequest<T> where T : class
{
    private T? _latest;
    private int _queued;

    public bool Publish(T item)
    {
        Volatile.Write(ref _latest, item);
        return Interlocked.Exchange(ref _queued, 1) == 0;
    }

    public T? Take() => Interlocked.Exchange(ref _latest, null);

    public bool Complete()
    {
        Interlocked.Exchange(ref _queued, 0);
        return Volatile.Read(ref _latest) != null
            && Interlocked.CompareExchange(ref _queued, 1, 0) == 0;
    }

    public void Clear()
    {
        Interlocked.Exchange(ref _latest, null);
        Interlocked.Exchange(ref _queued, 0);
    }
}
