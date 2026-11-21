namespace AAVideoExport.Core;

// Native sliders invoke callbacks both for pointer edits and for activation.
// Compare against the last native position, not the options value: a text edit
// can update options just before the slider's throttled refresh or page hiding.
public sealed class SliderEditGuard
{
    private float _lastPosition;
    public SliderEditGuard(float initialPosition) => Synchronize(initialPosition);

    public void Synchronize(float position)
    {
        if (float.IsFinite(position)) _lastPosition = position;
    }

    public bool ObserveChange(float position)
    {
        if (!float.IsFinite(position)) return false;
        bool changed = Math.Abs(position - _lastPosition) > .00001f;
        _lastPosition = position;
        return changed;
    }
}
