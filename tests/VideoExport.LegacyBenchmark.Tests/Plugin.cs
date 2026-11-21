using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
namespace AAVideoExport.LegacyBenchmark.Tests;

// TEST ONLY. Install temporarily in a guarded AA child process, never ship.
[BepInPlugin("halocue.aa.videoexport.legacybenchmark", "AA Export Legacy Benchmark Tests", "0.1.0")]
[BepInDependency("halocue.aa.videoexport")]
public sealed class Plugin:BasePlugin
{
 internal static string Output="";
 internal static Action<string> LogLine=_=>{};
 public override void Load()
 {
  LogLine=message=>Log.LogInfo(message);
  if(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_LEGACY_BENCHMARK")!="1")return;
  string? output=Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_OUTPUT");
  if(string.IsNullOrWhiteSpace(output)||!Path.IsPathFullyQualified(output))throw new InvalidOperationException("Benchmark requires an absolute test output directory.");
  Output=Path.GetFullPath(output);Directory.CreateDirectory(Output);
  if(File.Exists(Path.Combine(Output,"legacy-benchmark.json")))throw new IOException("Refusing to overwrite an existing benchmark receipt.");
  AddComponent<LegacyBenchmarkRunner>();
 }
 internal static void Finish(bool passed)
 {
  LogLine("LEGACY-BENCHMARK RESULT "+(passed?"PASS":"FAIL"));
  if(Environment.GetEnvironmentVariable("AA_VIDEOEXPORT_TEST_QUIT")=="1")Application.Quit();
 }
}
