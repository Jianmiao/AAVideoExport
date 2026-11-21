using System.Reflection;
using AAVideoExport.Core;
namespace AAVideoExport.Plugin;
internal static class ControlledBackgroundVideoAdapter { internal static bool TryClassify(Assembly a,out StrictCapabilityLevel level,out IReadOnlyList<string> evidence){level=StrictCapabilityLevel.Rejected;evidence=Array.Empty<string>();return false;} }
internal static class MultilingualCaptureAdapter { internal static bool TryClassify(Assembly a,out StrictCapabilityLevel level,out IReadOnlyList<string> evidence){level=StrictCapabilityLevel.Rejected;evidence=Array.Empty<string>();return false;} }
internal static class RuntimeModBoundaries { internal static bool TryClassify(Assembly a,out StrictCapabilityLevel level,out IReadOnlyList<string> evidence){level=StrictCapabilityLevel.Rejected;evidence=Array.Empty<string>();return false;} }
internal static class MoreEffectsCompatibility { internal static bool FullClockCoverage=>false;internal static string Status=>"absent";internal static string FailureReason=>"not exercised"; }
internal static class FlandrePortraitClock { internal static bool IsReady=>false;internal static string Status=>"absent";internal static string FailureReason=>"not exercised"; }
internal static class AASkipIntroExportBoundary { internal static string CaptureActiveProof()=>throw new InvalidOperationException("not exercised"); }
internal static class RetainedMenuPlaybackBoundary { internal static bool TryClassify(Assembly a,out StrictCapabilityLevel level,out IReadOnlyList<string> evidence){level=StrictCapabilityLevel.Rejected;evidence=Array.Empty<string>();return false;} }
internal static class GenericStaticModPolicy { internal static bool TryClassify(Assembly a,out IReadOnlyList<string> evidence){evidence=Array.Empty<string>();return false;} }
internal sealed class CharacterVoiceSessionAdapter { internal const string ControllerEvidenceMarker="CharacterVoiceSessionAdapter.held-controller.v1";internal void ValidateController()=>throw new InvalidOperationException("not exercised"); }
internal static class CharacterVoiceDelayClock { internal static void ValidateBinding()=>throw new InvalidOperationException("not exercised"); }
