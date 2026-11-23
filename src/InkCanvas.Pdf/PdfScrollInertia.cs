using System.Numerics;

namespace InkCanvas.Pdf;

/// <summary>Time-based release velocity and frame-rate-independent touch scrolling.</summary>
public sealed class PdfScrollInertia
{
    private Vector2 _lastPosition, _velocity;
    private double _lastSample;
    public bool IsRunning { get; private set; }
    public double RemainingSeconds => IsRunning ? Math.Max(0, Math.Log(_velocity.Length() / 12) / 7.5) : 0;
    public Vector2 Project(double seconds) => _velocity * (float)((1 - Math.Exp(-7.5 * Math.Max(0, seconds))) / 7.5);

    public void Reset(Vector2 position, double seconds)
    {
        Stop();
        _lastPosition = position;
        _lastSample = seconds;
    }

    public void Observe(Vector2 position, double seconds)
    {
        double elapsed = seconds - _lastSample;
        if (elapsed < .004) return;
        var measured = (position - _lastPosition) / (float)elapsed;
        if (measured.LengthSquared() > 5000 * 5000) measured = Vector2.Normalize(measured) * 5000;
        _velocity = elapsed > .12 ? measured : Vector2.Lerp(_velocity, measured, (float)(1 - Math.Exp(-elapsed / .035)));
        _lastPosition = position;
        _lastSample = seconds;
    }

    public bool Start(double seconds)
    {
        IsRunning = seconds - _lastSample <= .10 && _velocity.LengthSquared() >= 50 * 50;
        return IsRunning;
    }

    public Vector2 Advance(double seconds)
    {
        if (!IsRunning || seconds <= 0) return Vector2.Zero;
        float decay = (float)Math.Exp(-7.5 * seconds);
        var distance = _velocity * ((1 - decay) / 7.5f);
        _velocity *= decay;
        if (_velocity.LengthSquared() < 12 * 12) Stop();
        return distance;
    }

    public void Constrain(Vector2 requested, Vector2 actual)
    {
        if (Math.Abs(requested.X - actual.X) > .01f) _velocity.X = 0;
        if (Math.Abs(requested.Y - actual.Y) > .01f) _velocity.Y = 0;
        if (_velocity.LengthSquared() < 12 * 12) Stop();
    }

    public void Stop() { IsRunning = false; _velocity = Vector2.Zero; }
}
