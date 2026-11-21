using System.Collections;
using UnityEngine;

namespace AAVideoExport.Plugin;

// Kept outside the IL2CPP-registered MonoBehaviour. Returning a managed
// IEnumerator from a registered type makes Il2CppInterop reject the method;
// this ordinary helper is wrapped with CollectionExtensions.WrapToIl2Cpp.
internal static class CaptureLoopFactory
{
    public static IEnumerator Create(ExportHost host)
    {
        while (host.Capturing)
        {
            yield return new WaitForEndOfFrame();
            host.CaptureLoopFrame();
        }
        host.CaptureLoopFinished();
    }
}
