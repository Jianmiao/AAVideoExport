using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace AAVideoExport.Plugin;

internal sealed class RetainedPatchGuard
{
    private readonly Harmony harmony;
    private readonly List<Entry> entries = new();
    internal RetainedPatchGuard(Harmony harmony) => this.harmony = harmony;
    internal string FailureReason { get; private set; } = "";
    internal int Count => entries.Count;
    internal void Add(MethodInfo target, MethodInfo prefix, MethodInfo? postfix = null, MethodInfo? finalizer = null)
    {
        if (target.GetMethodBody() == null) throw new NotSupportedException("Guard target has no inspected managed body: " + target.Name);
        var info = Harmony.GetPatchInfo(target);
        if (info != null && All(info).Any()) throw new NotSupportedException("Guard target already has a patch: " + target.Name);
        var entry = new Entry(target, prefix, postfix, finalizer); entries.Add(entry);
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: postfix == null ? null : new HarmonyMethod(postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(finalizer));
            Validate(entry);
        }
        catch
        {
            try { Remove(entry); entries.Remove(entry); } catch (Exception error) { FailureReason = error.Message; }
            throw;
        }
    }
    internal void Validate() { foreach (var entry in entries) Validate(entry); }
    private void Validate(Entry entry)
    {
        var info = Harmony.GetPatchInfo(entry.Target) ?? throw new NotSupportedException("Retained callback guard was removed.");
        bool Exact(IEnumerable<Patch> patches, MethodInfo? expected) => expected == null ? !patches.Any()
            : patches.Count() == 1 && patches.Single().owner == harmony.Id && patches.Single().PatchMethod == expected;
        if (!Exact(info.Prefixes, entry.Prefix) || !Exact(info.Postfixes, entry.Postfix) || !Exact(info.Finalizers, entry.Finalizer)
            || info.Transpilers.Any() || info.ILManipulators.Any())
            throw new NotSupportedException("Retained callback patch kind or owner changed: " + entry.Target.Name);
    }
    private void Remove(Entry entry)
    {
        harmony.Unpatch(entry.Target, HarmonyPatchType.All, harmony.Id);
        if (Harmony.GetPatchInfo(entry.Target) is { } info && All(info).Any(p => p.owner == harmony.Id))
            throw new InvalidOperationException("Retained observer remains installed: " + entry.Target.Name);
    }
    internal bool RemoveAll()
    {
        var errors = new List<Exception>();
        foreach (var entry in entries.ToArray())
        {
            try { Remove(entry); entries.Remove(entry); }
            catch (Exception error) { errors.Add(error); }
        }
        FailureReason = string.Join("; ", errors.Select(error => error.Message));
        return errors.Count == 0;
    }
    internal bool RollbackFrom(int first)
    {
        var errors = new List<Exception>();
        foreach (var entry in entries.Skip(first).Reverse().ToArray())
        {
            try { Remove(entry); entries.Remove(entry); }
            catch (Exception error) { errors.Add(error); }
        }
        FailureReason = string.Join("; ", errors.Select(error => error.Message));
        return errors.Count == 0;
    }
    internal static IEnumerable<Patch> All(Patches info) => info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators);
    internal static string Graph(IEnumerable<MethodBase> targets)
    {
        var rows = new List<string>();
        foreach (var target in targets.Distinct().OrderBy(Key, StringComparer.Ordinal))
        {
            rows.Add("target|" + Key(target));
            var info = Harmony.GetPatchInfo(target);
            if (info == null) { rows.Add("missing"); continue; }
            void Add(string kind, IEnumerable<Patch> patches)
            {
                foreach (var patch in patches)
                    rows.Add(kind + "|" + patch.owner + "|" + Key(patch.PatchMethod) + "|" + patch.priority + "|" + patch.index
                        + "|before=" + string.Join(",", patch.before) + "|after=" + string.Join(",", patch.after));
            }
            Add("prefix", info.Prefixes); Add("postfix", info.Postfixes); Add("transpiler", info.Transpilers);
            Add("finalizer", info.Finalizers); Add("ilmanipulator", info.ILManipulators);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))));
    }
    internal static string Key(MethodBase method) => method.Module.ModuleVersionId + "|" + method.MetadataToken
        + "|" + method.DeclaringType?.FullName + "." + method.Name;
    private sealed record Entry(MethodInfo Target, MethodInfo Prefix, MethodInfo? Postfix, MethodInfo? Finalizer);
}
