namespace EutherDrive.OpenRA;

public enum OpenRaTouchPhase
{
    Down,
    Move,
    Up,
    Cancel
}

public enum OpenRaMobileGestureKind
{
    None,
    Tap,
    LongPress,
    Pan,
    PinchZoom,
    DragSelect
}

public readonly struct OpenRaTouchSample
{
    public OpenRaTouchSample(long id, OpenRaTouchPhase phase, double x, double y, long timestampMilliseconds)
    {
        Id = id;
        Phase = phase;
        X = x;
        Y = y;
        TimestampMilliseconds = timestampMilliseconds;
    }

    public long Id { get; }

    public OpenRaTouchPhase Phase { get; }

    public double X { get; }

    public double Y { get; }

    public long TimestampMilliseconds { get; }
}

public readonly struct OpenRaMobileGesture
{
    public OpenRaMobileGesture(
        OpenRaMobileGestureKind kind,
        double x,
        double y,
        double deltaX = 0,
        double deltaY = 0,
        double scale = 1,
        double endX = 0,
        double endY = 0)
    {
        Kind = kind;
        X = x;
        Y = y;
        DeltaX = deltaX;
        DeltaY = deltaY;
        Scale = scale;
        EndX = endX;
        EndY = endY;
    }

    public OpenRaMobileGestureKind Kind { get; }

    public double X { get; }

    public double Y { get; }

    public double DeltaX { get; }

    public double DeltaY { get; }

    public double Scale { get; }

    public double EndX { get; }

    public double EndY { get; }
}

public sealed class OpenRaMobileInputSettings
{
    public double TapSlopPixels { get; set; } = 12;

    public long TapMaxMilliseconds { get; set; } = 220;

    public long LongPressMilliseconds { get; set; } = 420;

    public double DragSelectThresholdPixels { get; set; } = 20;
}

public sealed class OpenRaMobileInputMapper
{
    private readonly OpenRaMobileInputSettings _settings;
    private readonly Dictionary<long, TouchState> _touches = new Dictionary<long, TouchState>();
    private double _lastPinchDistance;

    public OpenRaMobileInputMapper(OpenRaMobileInputSettings? settings = null)
    {
        _settings = settings ?? new OpenRaMobileInputSettings();
    }

    public OpenRaMobileGesture Update(OpenRaTouchSample sample)
    {
        switch (sample.Phase)
        {
            case OpenRaTouchPhase.Down:
                _touches[sample.Id] = new TouchState(sample.X, sample.Y, sample.TimestampMilliseconds);
                ResetPinchIfNeeded();
                return default;

            case OpenRaTouchPhase.Move:
                return Move(sample);

            case OpenRaTouchPhase.Up:
                return Release(sample, cancelled: false);

            case OpenRaTouchPhase.Cancel:
                return Release(sample, cancelled: true);

            default:
                return default;
        }
    }

    public void Reset()
    {
        _touches.Clear();
        _lastPinchDistance = 0;
    }

    private OpenRaMobileGesture Move(OpenRaTouchSample sample)
    {
        if (!_touches.TryGetValue(sample.Id, out var state))
            return default;

        var updated = state.WithCurrent(sample.X, sample.Y);
        _touches[sample.Id] = updated;

        if (_touches.Count >= 2)
            return PinchGesture();

        var dx = updated.CurrentX - updated.PreviousX;
        var dy = updated.CurrentY - updated.PreviousY;
        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon)
            return default;

        return new OpenRaMobileGesture(OpenRaMobileGestureKind.Pan, updated.CurrentX, updated.CurrentY, dx, dy);
    }

    private OpenRaMobileGesture Release(OpenRaTouchSample sample, bool cancelled)
    {
        if (!_touches.TryGetValue(sample.Id, out var state))
            return default;

        _touches.Remove(sample.Id);
        ResetPinchIfNeeded();

        if (cancelled)
            return default;

        var endX = sample.X;
        var endY = sample.Y;
        var duration = sample.TimestampMilliseconds - state.StartTimestampMilliseconds;
        var distance = Distance(state.StartX, state.StartY, endX, endY);

        if (duration >= _settings.LongPressMilliseconds && distance <= _settings.TapSlopPixels)
            return new OpenRaMobileGesture(OpenRaMobileGestureKind.LongPress, endX, endY);

        if (duration <= _settings.TapMaxMilliseconds && distance <= _settings.TapSlopPixels)
            return new OpenRaMobileGesture(OpenRaMobileGestureKind.Tap, endX, endY);

        if (distance >= _settings.DragSelectThresholdPixels)
            return new OpenRaMobileGesture(OpenRaMobileGestureKind.DragSelect, state.StartX, state.StartY, endX: endX, endY: endY);

        return default;
    }

    private OpenRaMobileGesture PinchGesture()
    {
        var first = default(TouchState);
        var second = default(TouchState);
        var index = 0;

        foreach (var touch in _touches.Values)
        {
            if (index == 0)
                first = touch;
            else if (index == 1)
            {
                second = touch;
                break;
            }

            index++;
        }

        var distance = Distance(first.CurrentX, first.CurrentY, second.CurrentX, second.CurrentY);
        if (_lastPinchDistance <= 0)
        {
            _lastPinchDistance = distance;
            return default;
        }

        var scale = distance <= 0 ? 1 : distance / _lastPinchDistance;
        _lastPinchDistance = distance;

        var centerX = (first.CurrentX + second.CurrentX) * 0.5;
        var centerY = (first.CurrentY + second.CurrentY) * 0.5;
        return new OpenRaMobileGesture(OpenRaMobileGestureKind.PinchZoom, centerX, centerY, scale: scale);
    }

    private void ResetPinchIfNeeded()
    {
        if (_touches.Count < 2)
            _lastPinchDistance = 0;
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private readonly struct TouchState
    {
        public TouchState(double startX, double startY, long startTimestampMilliseconds)
        {
            StartX = startX;
            StartY = startY;
            CurrentX = startX;
            CurrentY = startY;
            PreviousX = startX;
            PreviousY = startY;
            StartTimestampMilliseconds = startTimestampMilliseconds;
        }

        public double StartX { get; }

        public double StartY { get; }

        public double CurrentX { get; }

        public double CurrentY { get; }

        public double PreviousX { get; }

        public double PreviousY { get; }

        public long StartTimestampMilliseconds { get; }

        public TouchState WithCurrent(double x, double y)
        {
            return new TouchState(StartX, StartY, x, y, CurrentX, CurrentY, StartTimestampMilliseconds);
        }

        private TouchState(
            double startX,
            double startY,
            double currentX,
            double currentY,
            double previousX,
            double previousY,
            long startTimestampMilliseconds)
        {
            StartX = startX;
            StartY = startY;
            CurrentX = currentX;
            CurrentY = currentY;
            PreviousX = previousX;
            PreviousY = previousY;
            StartTimestampMilliseconds = startTimestampMilliseconds;
        }
    }
}
