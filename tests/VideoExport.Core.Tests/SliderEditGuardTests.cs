using AAVideoExport.Core;

namespace AAVideoExport.Core.Tests;

internal static partial class Program
{
    private static async Task TestSliderEditGuard()
    {
        await Test("unchanged native slider activation cannot overwrite a text draft", () => { TestSliderActivationGuard(); return Task.CompletedTask; });
        await Test("slider activation before throttled text synchronization is not a user edit", () => { TestSliderStaleTextSynchronization(); return Task.CompletedTask; });
        await Test("programmatic synchronization establishes the actual quantized slider baseline", () => { TestSliderProgrammaticSynchronization(); return Task.CompletedTask; });
        await Test("every bitrate and RCAS slider step remains editable in both directions", () => { TestSliderQuantizedEdits(); return Task.CompletedTask; });
        await Test("nonfinite native slider events cannot poison later activation or pointer edits", () => { TestSliderNonfiniteEvents(); return Task.CompletedTask; });
        await Test("native slider float noise is ignored without suppressing real movement", () => { TestSliderPositionNoise(); return Task.CompletedTask; });
    }

    private static void TestSliderActivationGuard()
    {
        foreach (float nativePosition in new[] { 0f, VideoBitrate.SliderPosition(4000), .87f, 1f })
        {
            var edits = new SliderEditGuard(nativePosition);
            for (int reopen = 0; reopen < 100; reopen++)
                Check(!edits.ObserveChange(nativePosition), "repeated OnEnable callbacks must not reach the draft-writing branch");
            float moved = nativePosition < .5f ? .75f : .25f;
            Check(edits.ObserveChange(moved), "pointer movement is still accepted after repeated activations");
            Check(!edits.ObserveChange(moved), "activation after a pointer edit is not a second edit");
            Check(edits.ObserveChange(nativePosition), "returning the pointer to its original position is a genuine edit");
        }
        // The production panel returns before writing bitrate/sharpness drafts
        // when ObserveChange is false. No unchanged activation may enter that
        // branch, regardless of whether the caller's draft is valid, blank, ".",
        // or otherwise incomplete; the guard deliberately does not parse it.
    }

    private static void TestSliderStaleTextSynchronization()
    {
        foreach (var (oldNativePosition, textTarget) in new[]
        {
            (VideoBitrate.SliderPosition(4000), VideoBitrate.SliderPosition(16000)),
            (VideoBitrate.SliderPosition(4000), VideoBitrate.SliderPosition(500000)),
            (.87f, .43f)
        })
        {
            var edits = new SliderEditGuard(oldNativePosition);
            Check(oldNativePosition != textTarget, "fixture must have a pending options-to-slider update");
            // A text edit changes caller options, then Back hides the page before
            // its 100 ms presentation refresh. Reopening fires the old position.
            Check(!edits.ObserveChange(oldNativePosition), "stale activation cannot replace the newly typed option");
            Check(!edits.ObserveChange(oldNativePosition), "multiple activation callbacks remain harmless");
            edits.Synchronize(textTarget);
            Check(!edits.ObserveChange(textTarget), "the delayed programmatic refresh is not another user edit");
            float pointerPosition = textTarget > .5f ? textTarget - .02f : textTarget + .02f;
            Check(edits.ObserveChange(pointerPosition), "the next intentional slider adjustment is accepted");
            Check(!edits.ObserveChange(pointerPosition), "Back and reopen after that adjustment preserve it");
        }
    }

    private static void TestSliderProgrammaticSynchronization()
    {
        var edits = new SliderEditGuard(VideoBitrate.SliderPosition(4000));
        // NGUI quantizes 500 steps into positions 0..499. Synchronize must use
        // the value read back from the widget, not the unquantized text target.
        float desired = VideoBitrate.SliderPosition(8123);
        float actual = (float)Math.Round(desired * 499) / 499;
        Check(desired != actual, "fixture exercises a real quantization difference");
        edits.Synchronize(actual);
        Check(!edits.ObserveChange(actual), "quantized programmatic result does not trigger draft replacement");
        Check(edits.ObserveChange(actual + 1f / 499), "one adjacent pointer step is not lost after synchronization");
        edits.Synchronize(1);
        Check(!edits.ObserveChange(1), "text above the slider's range leaves its clamped maximum unchanged on activation");
        Check(edits.ObserveChange(498f / 499), "a deliberate move off maximum is accepted even after high manual bitrate");
        edits.Synchronize(0);
        Check(!edits.ObserveChange(0), "repeated programmatic writes can reset the baseline in either direction");
        Check(edits.ObserveChange(1f / 499), "the first bitrate step remains selectable");
    }

    private static void TestSliderQuantizedEdits()
    {
        foreach (int intervals in new[] { 499, 100 })
        {
            var edits = new SliderEditGuard(0);
            for (int step = 1; step <= intervals; step++)
            {
                float position = step / (float)intervals;
                Check(edits.ObserveChange(position), $"forward step {step}/{intervals} must reach the edit callback");
                Check(!edits.ObserveChange(position), "repeated activation does not duplicate a quantized edit");
            }
            for (int step = intervals - 1; step >= 0; step--)
            {
                float position = step / (float)intervals;
                Check(edits.ObserveChange(position), $"reverse step {step}/{intervals} must reach the edit callback");
                Check(!edits.ObserveChange(position), "unchanged reverse position remains idempotent");
            }
        }
    }

    private static void TestSliderNonfiniteEvents()
    {
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var edits = new SliderEditGuard(.87f);
            Check(!edits.ObserveChange(invalid), "nonfinite callback is not a user edit");
            Check(!edits.ObserveChange(.87f), "invalid callback does not destroy the previous native baseline");
            edits.Synchronize(invalid);
            Check(!edits.ObserveChange(.87f), "invalid programmatic value does not destroy the previous native baseline");
            Check(edits.ObserveChange(.88f), "a valid pointer edit remains usable after invalid values");
            var invalidInitial = new SliderEditGuard(invalid);
            invalidInitial.Synchronize(.5f);
            Check(!invalidInitial.ObserveChange(.5f), "a finite synchronization recovers from nonfinite initialization");
            Check(invalidInitial.ObserveChange(.51f), "recovered slider accepts normal movement");
        }
    }

    private static void TestSliderPositionNoise()
    {
        var edits = new SliderEditGuard(.5f);
        Check(!edits.ObserveChange(.500001f), "substep native precision noise cannot overwrite a draft");
        Check(!edits.ObserveChange(.5f), "return from native precision noise is not a slider edit");
        Check(edits.ObserveChange(.51f), "one RCAS step is much larger than native noise tolerance");
        Check(!edits.ObserveChange(.510001f), "post-edit precision noise remains harmless");
        Check(edits.ObserveChange(.5f), "reverse user adjustment still works");
    }
}
