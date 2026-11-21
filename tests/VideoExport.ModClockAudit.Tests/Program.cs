using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AAVideoExport.Plugin;

var self = Assembly.GetExecutingAssembly().Location;
var report = ModClockAudit.Scan(new[] { self });
Check(report.Summary.AssembliesScanned == 1, "fixture assembly should be scanned");
Check(report.Summary.FailedAssemblies == 0, "fixture assembly should scan without errors");
var assembly = report.Assemblies.Single();
Check(assembly.ModuleName == Path.GetFileName(self), "module identity should use the file name");
Check(assembly.AssemblyVersion is not null, "assembly version should be reported");
Check(assembly.Mvid != Guid.Empty, "module MVID should be reported");
Check(assembly.Sha256.Length == 64 && assembly.Sha256.All(Uri.IsHexDigit), "SHA-256 should be reported");
Check(assembly.Sha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(self))), "SHA-256 should match the scanned file bytes");
Check(assembly.WallClock.CallSites.Any(site => site.DeclaringType.Contains("ClockFixture") && site.MethodName == "ReadWall"), "wall-clock call sites should be grouped");
Check(assembly.UnityClock.CallSites.Any(site => site.DeclaringType.Contains("UnityFixture") && site.MethodName == "ReadUnity"), "Unity clock call sites should be grouped");
Check(assembly.AudioClock.CallSites.Any(site => site.IsInvocation), "audio calls should be marked as invocations");
Check(assembly.References.Any(site => site.Category == ModClockCategory.Audio && !site.IsInvocation), "audio references should be distinguishable from calls");
Check(assembly.References.Any(site => site.Category == ModClockCategory.Audio && site.TargetDeclaringType.Contains("VideoPlayer") && site.TargetMemberName == "time"), "VideoPlayer time reference should include declaring type and member");
Check(assembly.AudioClock.CallSites.Any(site => site.TargetDeclaringType.Contains("AudioSource") && site.TargetMethodName == "PlayScheduled" && site.IlOpcode == "callvirt"), "AudioSource callsite should retain its target method and opcode");
var stopwatchCall = assembly.WallClock.CallSites.Single(site => site.DeclaringType == "ClockFixture"
    && site.MethodName == "ReadWall" && site.TargetDeclaringType == "System.Diagnostics.Stopwatch"
    && site.TargetMethodName == "GetTimestamp");
Check(stopwatchCall.IlOffset >= 0 && stopwatchCall.IlOpcode == "call" && stopwatchCall.Operand.Contains("GetTimestamp"), "call sites should carry exact IL offset, opcode, and target signature");
Check(assembly.WallClock.CallSites.Any(site => site.MethodName == "ReadWall" && site.CallerMethodSignature.Contains("ReadWall()")), "caller metadata should retain the exact method signature");
Check(assembly.References.Any(site => site.TargetDeclaringType.Contains("AudioSource") && site.TargetMethodName == "Play" && site.ReferenceKind == ModClockReferenceKind.MethodReference && site.IlOpcode == "ldftn"), "non-invoked audio method references should remain visible");
Check(assembly.References.Any(site => site.DeclaringType.StartsWith("AAVideoExport", StringComparison.Ordinal))
    && assembly.References.Any(site => site.DeclaringType.StartsWith("Azurite", StringComparison.Ordinal)), "AAVE/Azurite-named call sites should not be silently filtered");
Check(report.Limitations.Contains("cannot prove runtime calls", StringComparison.OrdinalIgnoreCase), "report should state the static-analysis limitation");

var excluded = ModClockAudit.Scan(new[] { self }, new ModClockAuditOptions
{
    ExcludedCallSites = new[]
    {
        new ModClockCallSiteSelector("ClockFixture", "ReadWall", ModClockCategory.Wall,
            "System.Diagnostics.Stopwatch", "GetTimestamp", self)
    }
});
Check(excluded.Assemblies.Single().ExcludedCallSites.Count == 1, "caller-selected exclusions should be visible in the report");
Check(excluded.Assemblies.Single().References.All(site => site.DeclaringType != "ClockFixture" || site.TargetMethodName != "GetTimestamp"), "caller-selected call sites should be excluded");

var excludedAssembly = ModClockAudit.Scan(new[] { self }, new ModClockAuditOptions
{
    ExcludedAssemblyNames = new[] { Path.GetFileName(self) }
});
Check(excludedAssembly.Summary.ExcludedAssemblies == 1 && excludedAssembly.Assemblies.Single().Excluded, "assembly exclusions should require and report an explicit caller choice");

var missing = ModClockAudit.Scan(new[] { Path.Combine(Path.GetTempPath(), "missing-mod.dll") });
Check(missing.Summary.FailedAssemblies == 1 && missing.Assemblies.Single().Error is not null, "missing paths should be reported as scan errors");
var invalidPath = ModClockAudit.Scan(new[] { "bad\0path.dll" });
Check(invalidPath.Summary.FailedAssemblies == 1 && invalidPath.Assemblies.Single().Error?.StartsWith("Invalid assembly path", StringComparison.Ordinal) == true, "invalid paths should be isolated as scan errors");

if (args.Length > 0)
{
    bool showDetails = args.Contains("--details", StringComparer.Ordinal);
    var requestedPaths = args.Where(path => path != "--details").ToArray();
    var fixtureReport = ModClockAudit.Scan(requestedPaths);
    Console.WriteLine(fixtureReport.FormatSummary());
    foreach (var mod in fixtureReport.Assemblies)
    {
        Console.WriteLine($"{mod.ModuleName}: {mod.CallSites.Count} sites ({mod.Error ?? "ok"})");
        if (!showDetails) continue;
        Console.WriteLine($"  version={mod.AssemblyVersion ?? "unknown"}; mvid={mod.Mvid}; sha256={mod.Sha256}");
        foreach (var site in mod.References.OrderBy(site => site.IlOffset)
            .ThenBy(site => site.Category).ThenBy(site => site.TargetMemberName, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {site.Category}/{site.ReferenceKind} {site.Location} -> " +
                $"{site.TargetDeclaringType}::{site.TargetMethodName} [{site.IlOpcode} {site.Operand}]");
        }
    }
}

Console.WriteLine($"PASS ModClockAudit ({report.Summary.TotalCallSites} fixture sites; {report.Summary.AudioClockCount} audio)");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

internal static class ClockFixture
{
    public static void ReadWall()
    {
        _ = Stopwatch.GetTimestamp();
        _ = DateTime.UtcNow;
        _ = Environment.TickCount64;
        Thread.Sleep(0);
        _ = Task.Delay(1);
    }
}

internal static class UnityFixture
{
    public static void ReadUnity()
    {
        _ = UnityEngine.Time.realtimeSinceStartup;
        _ = UnityEngine.Time.unscaledDeltaTime;
        _ = new UnityEngine.WaitForSecondsRealtime(0.1f);
    }
}

internal static class AudioFixture
{
    public static void ReadAudio()
    {
        ClockAuditSink.UseType(typeof(UnityEngine.AudioSource));
        _ = UnityEngine.AudioSettings.dspTime;
        var source = new UnityEngine.AudioSource();
        source.PlayScheduled(1);
        Action play = source.Play;
        ClockAuditSink.Keep(play);
        _ = source.isPlaying;
        var player = new UnityEngine.Video.VideoPlayer();
        _ = player.time;
        _ = player.frame;
        player.Play();
    }
}

namespace AAVideoExport
{
    internal static class AaveNamedClockFixture
    {
        public static void ReadWall() => _ = Stopwatch.GetTimestamp();
    }
}

namespace Azurite
{
    internal static class AzuriteNamedClockFixture
    {
        public static void ReadWall() => _ = DateTime.UtcNow;
    }
}

internal static class ClockAuditSink
{
    public static void UseType(Type type) { _ = type.FullName; }
    public static void Keep(Delegate value) { _ = value.Method; }
}

namespace UnityEngine
{
    internal static class Time
    {
        public static float realtimeSinceStartup => 0;
        public static float unscaledDeltaTime => 0;
    }

    internal sealed class WaitForSecondsRealtime
    {
        public WaitForSecondsRealtime(float seconds) { }
    }

    internal static class AudioSettings
    {
        public static double dspTime => 0;
    }

    internal sealed class AudioSource
    {
        public bool isPlaying => false;
        public void Play() { }
        public void PlayScheduled(double time) { }
    }

    namespace Video
    {
        internal sealed class VideoPlayer
        {
            public double time => 0;
            public long frame => 0;
            public void Play() { }
        }
    }
}
