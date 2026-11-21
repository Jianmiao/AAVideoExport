using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AAVideoExport.Plugin;

/// <summary>Clock API families reported by the read-only managed assembly audit.</summary>
public enum ModClockCategory
{
    Wall,
    Unity,
    Audio
}

/// <summary>How an IL instruction refers to a clock API.</summary>
public enum ModClockReferenceKind
{
    Invocation,
    FieldAccess,
    MethodReference,
    TypeReference
}

/// <summary>Explicit caller-selected filters. No assembly or call site is suppressed by default.</summary>
public sealed class ModClockAuditOptions
{
    public IReadOnlyCollection<string> ExcludedAssemblyNames { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> ExcludedAssemblyPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<ModClockCallSiteSelector> ExcludedCallSites { get; init; } = Array.Empty<ModClockCallSiteSelector>();
}

/// <summary>Identifies a caller method, with optional target refinements, for explicit exclusion.</summary>
public sealed record ModClockCallSiteSelector(
    string DeclaringType,
    string MethodName,
    ModClockCategory? Category = null,
    string? TargetDeclaringType = null,
    string? TargetMemberName = null,
    string? AssemblyPath = null);

/// <summary>A single static IL reference to a clock API.</summary>
public sealed class ModClockCallSite
{
    public string AssemblyPath { get; init; } = string.Empty;
    public ModClockCategory Category { get; init; }
    public ModClockReferenceKind ReferenceKind { get; init; }
    public bool IsInvocation => ReferenceKind == ModClockReferenceKind.Invocation;

    /// <summary>The managed type and method containing the IL instruction.</summary>
    public string DeclaringType { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
    public string CallerType => DeclaringType;
    public string CallerMethod => MethodName;
    public string CallerMethodSignature { get; init; } = string.Empty;

    /// <summary>The referenced API type and exact metadata member name.</summary>
    public string TargetDeclaringType { get; init; } = string.Empty;
    public string TargetMethodName { get; init; } = string.Empty;
    public string TargetMemberName { get; init; } = string.Empty;
    public string ReferencedType => TargetDeclaringType;
    public string ReferencedMember => TargetMemberName;

    public int IlOffset { get; init; }
    public string IlOpcode { get; init; } = string.Empty;
    public string Operand { get; init; } = string.Empty;
    public string Location => $"{DeclaringType}.{MethodName}+IL_{IlOffset:x4}";
}

/// <summary>Identity and static clock references found in one managed assembly.</summary>
public sealed class ModClockAssemblyReport
{
    public string AssemblyPath { get; init; } = string.Empty;
    public string ModuleName { get; init; } = string.Empty;
    public string? AssemblyVersion { get; init; }
    public string? Version => AssemblyVersion;
    public Guid Mvid { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public IReadOnlyList<ModClockCallSite> References { get; init; } = Array.Empty<ModClockCallSite>();
    public IReadOnlyList<ModClockCallSite> CallSites => References.Where(site => site.IsInvocation).ToArray();
    public IReadOnlyList<ModClockCallSite> ExcludedCallSites { get; init; } = Array.Empty<ModClockCallSite>();
    public int ExcludedCallSiteCount => ExcludedCallSites.Count;
    public ModClockCategoryReport WallClock => ForCategory(ModClockCategory.Wall);
    public ModClockCategoryReport UnityClock => ForCategory(ModClockCategory.Unity);
    public ModClockCategoryReport AudioClock => ForCategory(ModClockCategory.Audio);
    public bool Excluded { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => Error is null;

    private ModClockCategoryReport ForCategory(ModClockCategory category)
    {
        var references = References.Where(site => site.Category == category).ToArray();
        return new ModClockCategoryReport
        {
            References = references,
            CallSites = references.Where(site => site.IsInvocation).ToArray()
        };
    }
}

/// <summary>References and direct IL invocations in one clock family.</summary>
public sealed class ModClockCategoryReport
{
    public IReadOnlyList<ModClockCallSite> References { get; init; } = Array.Empty<ModClockCallSite>();
    public IReadOnlyList<ModClockCallSite> CallSites { get; init; } = Array.Empty<ModClockCallSite>();
}

/// <summary>Count summary for a complete audit request.</summary>
public sealed class ModClockAuditSummary
{
    public int AssembliesRequested { get; init; }
    public int AssembliesScanned { get; init; }
    public int FailedAssemblies { get; init; }
    public int ExcludedAssemblies { get; init; }
    public int TotalCallSites { get; init; }
    public int TotalReferences { get; init; }
    public int WallClockCount { get; init; }
    public int UnityClockCount { get; init; }
    public int AudioClockCount { get; init; }
}

/// <summary>A read-only Cecil report. It never loads or executes a target Mod assembly.</summary>
public sealed class ModClockAuditReport
{
    public IReadOnlyList<ModClockAssemblyReport> Assemblies { get; init; } = Array.Empty<ModClockAssemblyReport>();
    public ModClockAuditSummary Summary { get; init; } = new();

    public string Limitations =>
        "Static reference scanning cannot prove runtime calls or IL2CPP/native time behavior.";

    public string FormatSummary()
    {
        return string.Join(Environment.NewLine,
            $"Assemblies requested: {Summary.AssembliesRequested}",
            $"Assemblies scanned: {Summary.AssembliesScanned}",
            $"Failed assemblies: {Summary.FailedAssemblies}",
            $"Excluded assemblies: {Summary.ExcludedAssemblies}",
            $"Direct IL invocation sites: {Summary.TotalCallSites} (wall {Summary.WallClockCount}, Unity {Summary.UnityClockCount}, audio {Summary.AudioClockCount})",
            $"Clock API reference operands: {Summary.TotalReferences}",
            Limitations);
    }
}

/// <summary>Static, read-only scanner for managed Mod clock references.</summary>
public static class ModClockAudit
{
    private static readonly HashSet<string> WallDateTimeMembers = new(StringComparer.Ordinal)
    {
        "Now", "UtcNow", "Today", "Ticks", "TimeOfDay",
        "get_Now", "get_UtcNow", "get_Today", "get_Ticks", "get_TimeOfDay"
    };

    private static readonly HashSet<string> UnityTimeMembers = new(StringComparer.Ordinal)
    {
        "time", "timeAsDouble", "deltaTime", "fixedDeltaTime", "fixedUnscaledDeltaTime",
        "unscaledTime", "unscaledTimeAsDouble", "unscaledDeltaTime", "realtimeSinceStartup",
        "realtimeSinceStartupAsDouble", "smoothDeltaTime", "maximumDeltaTime", "timeScale",
        "captureDeltaTime", "captureFramerate", "frameCount",
        "get_time", "get_timeAsDouble", "get_deltaTime", "get_fixedDeltaTime", "get_fixedUnscaledDeltaTime",
        "get_unscaledTime", "get_unscaledTimeAsDouble", "get_unscaledDeltaTime", "get_realtimeSinceStartup",
        "get_realtimeSinceStartupAsDouble", "get_smoothDeltaTime", "get_maximumDeltaTime", "get_timeScale",
        "get_captureDeltaTime", "get_captureFramerate", "get_frameCount",
        "set_timeScale", "set_captureDeltaTime", "set_captureFramerate"
    };

    private static readonly HashSet<string> AudioSourceMembers = new(StringComparer.Ordinal)
    {
        "Play", "PlayDelayed", "PlayScheduled", "SetScheduledStartTime", "SetScheduledEndTime",
        "Stop", "Pause", "UnPause", "isPlaying", "time", "timeSamples", "clip",
        "get_isPlaying", "get_time", "get_timeSamples", "get_clip", "set_time", "set_timeSamples"
    };

    private static readonly HashSet<string> VideoPlayerMembers = new(StringComparer.Ordinal)
    {
        "Prepare", "Play", "Pause", "Stop", "StepForward", "time", "frame", "frameCount",
        "frameRate", "length", "clockTime", "isPrepared", "canSetTime", "timeUpdateMode", "timeReference",
        "externalReferenceTime", "get_time", "set_time", "get_frame", "set_frame", "get_frameCount",
        "get_frameRate", "get_length", "get_clockTime", "get_isPrepared", "get_canSetTime",
        "get_timeUpdateMode", "get_timeReference", "get_externalReferenceTime", "set_timeUpdateMode",
        "set_timeReference", "set_externalReferenceTime"
    };

    private static readonly HashSet<string> ThreadMembers = new(StringComparer.Ordinal) { "Sleep" };
    private static readonly HashSet<string> TaskMembers = new(StringComparer.Ordinal) { "Delay" };
    private static readonly HashSet<string> EnvironmentMembers = new(StringComparer.Ordinal)
        { "TickCount", "TickCount64", "get_TickCount", "get_TickCount64" };

    public static ModClockAuditReport Scan(IEnumerable<string> assemblyPaths, ModClockAuditOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        options ??= new ModClockAuditOptions();

        var reports = new List<ModClockAssemblyReport>();
        foreach (var path in assemblyPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                reports.Add(new ModClockAssemblyReport { AssemblyPath = path ?? string.Empty, Error = "Assembly path is empty." });
                continue;
            }
            reports.Add(ScanOne(path, options));
        }

        var references = reports.SelectMany(report => report.References).ToArray();
        var callSites = references.Where(site => site.IsInvocation).ToArray();
        var summary = new ModClockAuditSummary
        {
            AssembliesRequested = reports.Count,
            AssembliesScanned = reports.Count(report => report.Succeeded),
            FailedAssemblies = reports.Count(report => !report.Succeeded),
            ExcludedAssemblies = reports.Count(report => report.Excluded),
            TotalCallSites = callSites.Length,
            TotalReferences = references.Length,
            WallClockCount = callSites.Count(site => site.Category == ModClockCategory.Wall),
            UnityClockCount = callSites.Count(site => site.Category == ModClockCategory.Unity),
            AudioClockCount = callSites.Count(site => site.Category == ModClockCategory.Audio)
        };
        return new ModClockAuditReport { Assemblies = reports, Summary = summary };
    }

    public static ModClockAuditReport Scan(string assemblyPath, ModClockAuditOptions? options = null)
        => Scan(new[] { assemblyPath }, options);

    private static ModClockAssemblyReport ScanOne(string requestedPath, ModClockAuditOptions options)
    {
        string path;
        try
        {
            path = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ModClockAssemblyReport
            {
                AssemblyPath = requestedPath,
                ModuleName = Path.GetFileName(requestedPath),
                Error = $"Invalid assembly path: {ex.Message}"
            };
        }
        string sha256;
        try
        {
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            sha256 = Convert.ToHexString(hash.ComputeHash(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ModClockAssemblyReport
            {
                AssemblyPath = path,
                ModuleName = Path.GetFileName(path),
                Sha256 = string.Empty,
                Error = $"Unable to hash assembly: {ex.Message}"
            };
        }

        try
        {
            using var definition = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                ReadSymbols = false,
                InMemory = false
            });
            var module = definition.MainModule;
            var assemblyName = definition.Name.Name ?? module.Name;
            var excluded = IsAssemblyExcluded(path, module.Name, assemblyName, options);
            var sites = new List<ModClockCallSite>();
            var excludedSites = new List<ModClockCallSite>();

            if (!excluded)
            {
                foreach (var type in EnumerateTypes(module.Types))
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (!TryClassify(instruction, out var category, out var referenceKind, out var targetType, out var targetMethod, out var targetMember))
                            continue;

                        var site = new ModClockCallSite
                        {
                            AssemblyPath = path,
                            Category = category,
                            ReferenceKind = referenceKind,
                            DeclaringType = type.FullName,
                            MethodName = method.Name,
                            CallerMethodSignature = method.FullName,
                            TargetDeclaringType = targetType,
                            TargetMethodName = targetMethod,
                            TargetMemberName = targetMember,
                            IlOffset = instruction.Offset,
                            IlOpcode = instruction.OpCode.Name,
                            Operand = FormatOperand(instruction.Operand)
                        };
                        if (options.ExcludedCallSites.Any(selector => selector.Matches(site)))
                            excludedSites.Add(site);
                        else
                            sites.Add(site);
                    }
                }
            }

            return new ModClockAssemblyReport
            {
                AssemblyPath = path,
                ModuleName = module.Name,
                AssemblyVersion = definition.Name.Version?.ToString(),
                Mvid = module.Mvid,
                Sha256 = sha256,
                References = sites,
                ExcludedCallSites = excludedSites,
                Excluded = excluded
            };
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ModClockAssemblyReport
            {
                AssemblyPath = path,
                ModuleName = Path.GetFileName(path),
                Sha256 = sha256,
                Error = $"Unable to read managed metadata: {ex.Message}"
            };
        }
    }

    private static bool IsAssemblyExcluded(string path, string moduleName, string assemblyName, ModClockAuditOptions options)
    {
        var fullPath = Path.GetFullPath(path);
        return options.ExcludedAssemblyPaths.Any(candidate =>
                   string.Equals(Path.GetFullPath(candidate), fullPath, StringComparison.OrdinalIgnoreCase))
            || options.ExcludedAssemblyNames.Any(candidate =>
                   string.Equals(candidate, moduleName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, assemblyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in EnumerateTypes(type.NestedTypes))
                yield return nested;
        }
    }

    private static bool TryClassify(
        Instruction instruction,
        out ModClockCategory category,
        out ModClockReferenceKind referenceKind,
        out string targetType,
        out string targetMethod,
        out string targetMember)
    {
        category = default;
        referenceKind = default;
        targetType = string.Empty;
        targetMethod = string.Empty;
        targetMember = string.Empty;

        switch (instruction.Operand)
        {
            case MethodReference method:
                targetType = method.DeclaringType.FullName;
                targetMethod = method.Name;
                targetMember = NormalizeMemberName(method.Name);
                if (!TryClassifyMember(targetType, targetMethod, targetMember, out category))
                    return false;
                referenceKind = IsInvocation(instruction.OpCode.Code)
                    ? ModClockReferenceKind.Invocation
                    : ModClockReferenceKind.MethodReference;
                return true;

            case FieldReference field:
                targetType = field.DeclaringType.FullName;
                targetMethod = field.Name;
                targetMember = field.Name;
                if (!TryClassifyMember(targetType, field.Name, targetMember, out category))
                    return false;
                referenceKind = ModClockReferenceKind.FieldAccess;
                return true;

            case TypeReference type when IsWaitForSecondsRealtime(type.FullName):
                category = ModClockCategory.Unity;
                referenceKind = ModClockReferenceKind.TypeReference;
                targetType = type.FullName;
                targetMethod = string.Empty;
                targetMember = type.Name;
                return true;

            case TypeReference type when IsAudioClockType(type.FullName):
                category = ModClockCategory.Audio;
                referenceKind = ModClockReferenceKind.TypeReference;
                targetType = type.FullName;
                targetMethod = string.Empty;
                targetMember = type.Name;
                return true;

            default:
                return false;
        }
    }

    private static bool TryClassifyMember(string type, string method, string member, out ModClockCategory category)
    {
        if (type == "System.Diagnostics.Stopwatch"
            || type == "System.DateTime"
            && WallDateTimeMembers.Contains(method)
            || type == "System.DateTimeOffset"
            && WallDateTimeMembers.Contains(method)
            || type == "System.Environment" && EnvironmentMembers.Contains(method)
            || type == "System.Threading.Thread" && ThreadMembers.Contains(method)
            || type == "System.Threading.Tasks.Task" && TaskMembers.Contains(method))
        {
            category = ModClockCategory.Wall;
            return true;
        }

        if ((type is "UnityEngine.Time" or "UnityEngine.RealTime") && UnityTimeMembers.Contains(method)
            || IsWaitForSecondsRealtime(type))
        {
            category = ModClockCategory.Unity;
            return true;
        }

        if (type == "UnityEngine.AudioSettings" && (member == "dspTime" || method == "get_dspTime")
            || type == "UnityEngine.AudioSource" && IsAudioSourceMember(method, member)
            || type == "UnityEngine.Video.VideoPlayer" && VideoPlayerMembers.Contains(method))
        {
            category = ModClockCategory.Audio;
            return true;
        }

        category = default;
        return false;
    }

    private static bool IsAudioSourceMember(string method, string member)
    {
        if (AudioSourceMembers.Contains(method) || AudioSourceMembers.Contains(member)) return true;
        return method.StartsWith("Play", StringComparison.Ordinal)
            || method.StartsWith("SetScheduled", StringComparison.Ordinal);
    }

    private static bool IsWaitForSecondsRealtime(string type)
        => type == "UnityEngine.WaitForSecondsRealtime" || type.EndsWith("/WaitForSecondsRealtime", StringComparison.Ordinal);

    private static bool IsAudioClockType(string type)
        => type is "UnityEngine.AudioSettings" or "UnityEngine.AudioSource" or "UnityEngine.Video.VideoPlayer"
            || type.EndsWith("/AudioSettings", StringComparison.Ordinal)
            || type.EndsWith("/AudioSource", StringComparison.Ordinal)
            || type.EndsWith("/VideoPlayer", StringComparison.Ordinal);

    private static string NormalizeMemberName(string method)
    {
        if (method.StartsWith("get_", StringComparison.Ordinal) || method.StartsWith("set_", StringComparison.Ordinal))
            return method[4..];
        return method;
    }

    private static bool IsInvocation(Code code)
        => code is Code.Call or Code.Callvirt or Code.Newobj;

    private static string FormatOperand(object? operand)
        => operand switch
        {
            MethodReference method => method.FullName,
            FieldReference field => field.FullName,
            TypeReference type => type.FullName,
            _ => operand?.ToString() ?? string.Empty
        };
}

public static class ModClockCallSiteSelectorExtensions
{
    public static bool Matches(this ModClockCallSiteSelector selector, ModClockCallSite site)
    {
        if (!string.Equals(site.DeclaringType, selector.DeclaringType, StringComparison.Ordinal)
            && !site.DeclaringType.EndsWith("." + selector.DeclaringType, StringComparison.Ordinal))
            return false;
        if (!string.Equals(site.MethodName, selector.MethodName, StringComparison.Ordinal))
            return false;
        if (selector.AssemblyPath is not null
            && !string.Equals(Path.GetFullPath(site.AssemblyPath), Path.GetFullPath(selector.AssemblyPath), StringComparison.OrdinalIgnoreCase))
            return false;
        if (selector.Category is not null && selector.Category != site.Category) return false;
        if (selector.TargetDeclaringType is not null
            && !site.TargetDeclaringType.EndsWith(selector.TargetDeclaringType, StringComparison.Ordinal))
            return false;
        if (selector.TargetMemberName is not null
            && !string.Equals(site.TargetMemberName, selector.TargetMemberName, StringComparison.Ordinal))
            return false;
        return true;
    }
}
