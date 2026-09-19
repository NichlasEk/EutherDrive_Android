using EutherDrive.Rendering;

static void Check(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

var owner = new object();
var other = new object();
var requests = new LatestPresentationRequest<object>();
Check(requests.Publish(owner), "First request must post");
Check(!requests.Publish(other), "Queued requests must coalesce");
Check(ReferenceEquals(requests.Take(), other), "Must take latest owner");
Check(!requests.Publish(owner), "Producer must not post while consumer is rendering");
Check(requests.Complete(), "Consumer must post exactly one follow-up");
Check(!requests.Publish(other), "Follow-up is already queued");
Check(ReferenceEquals(requests.Take(), other), "Follow-up must take latest");
Check(!requests.Complete(), "Drained queue must stop");
Check(requests.Publish(owner), "Queue must restart after drain");
requests.Clear();
Check(requests.Take() == null && !requests.Complete(), "Clear must discard old owner");

var callbacks = new System.Collections.Concurrent.ConcurrentQueue<int>();
int publishingDone = 0;
var publishing = Task.Run(() =>
{
    for (int sequence = 1; sequence <= 10000; sequence++)
    {
        if (requests.Publish(sequence)) callbacks.Enqueue(0);
        Thread.Yield();
    }
    Volatile.Write(ref publishingDone, 1);
});
int lastSequence = 0;
int callbackCount = 0;
while (Volatile.Read(ref publishingDone) == 0 || !callbacks.IsEmpty)
{
    if (!callbacks.TryDequeue(out _)) { Thread.Yield(); continue; }
    object? request = requests.Take();
    Check(request is int next && next > lastSequence, "Duplicate or stale callback");
    lastSequence = (int)request!;
    // Take already consumed the request; a new publication during this yield
    // must be coalesced into one follow-up, not posted by both threads.
    callbackCount++;
    Thread.Yield();
    if (requests.Complete()) callbacks.Enqueue(0);
}
publishing.GetAwaiter().GetResult();
Check(callbackCount > 0 && lastSequence == 10000 && requests.Take() == null, "Queue did not drain or lost final request");

var published = new PublishedFrameBuffer();
byte[] destination = [];
Check(!published.TryCopy(owner, ref destination, out _, out _, out _, out _), "No initial frame");
byte[] source = new byte[320 * 232 * 4];
Array.Fill(source, (byte)7);
published.Publish(owner, source, 320, 232, 1280, 7);
Array.Clear(source);
Check(published.TryCopy(owner, ref destination, out int w, out int h, out int stride, out long id)
    && w == 320 && h == 232 && stride == 1280 && id == 7 && destination.All(b => b == 7),
    "Published pixels must be independent of the running core");
Check(!published.TryCopy(other, ref destination, out _, out _, out _, out _), "Reject stale core");

// Reproduce presentation while the emulation thread holds the core lock.
object coreLock = new();
using var held = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
var producer = Task.Run(() => { lock (coreLock) { held.Set(); release.Wait(); } });
held.Wait();
try
{
    bool acquired = Monitor.TryEnter(coreLock, 3);
    if (acquired) Monitor.Exit(coreLock);
    Check(!acquired, "Old UI path should miss its 3ms lock deadline");
    Check(published.TryCopy(owner, ref destination, out _, out _, out _, out id) && id == 7,
        "New path must deliver completed pixels while core is busy");
}
finally { release.Set(); }
producer.GetAwaiter().GetResult();

// Concurrent publication/copy must never combine pixels from different frames.
int done = 0;
producer = Task.Run(() =>
{
    for (int frame = 1; frame <= 1000; frame++)
    {
        Array.Fill(source, (byte)frame);
        published.Publish(owner, source, 320, 232, 1280, frame);
    }
    Volatile.Write(ref done, 1);
});
int copies = 0;
do
{
    Check(published.TryCopy(owner, ref destination, out _, out _, out _, out id), "Missing concurrent frame");
    Check(destination.All(b => b == (byte)id), "Torn frame or mismatched frame ID");
    copies++;
} while (Volatile.Read(ref done) == 0);
producer.GetAwaiter().GetResult();
Check(published.TryCopy(owner, ref destination, out _, out _, out _, out id) && id == 1000, "Latest frame lost");
published.Clear();
Check(!published.TryCopy(owner, ref destination, out _, out _, out _, out _), "Clear must hide stale pixels");
Console.WriteLine($"presentationChecks=passed publications=1000 concurrentCopies={copies} busyCoreDelivery=passed queueRequests=10000 callbacks={callbackCount} queueCoalescing=passed");
