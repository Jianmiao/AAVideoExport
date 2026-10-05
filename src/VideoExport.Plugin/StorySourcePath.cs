namespace AAVideoExport.Plugin;

internal static class StorySourcePath
{
    // Ask the same native path service used by LoadGenericScenario. In AA an
    // empty WorkspacePath means Application.persistentDataPath, not the CWD.
    // Compare complete filenames so a same-name file in another workspace
    // cannot be substituted for the selected catalog card.
    internal static bool TryGetScenarioName(string selectedPath, Func<string, string> resolveNativePath, out string scenarioName)
    {
        scenarioName = "";
        if (string.IsNullOrWhiteSpace(selectedPath)) return false;
        try
        {
            string selected = Path.GetFullPath(selectedPath);
            if (!string.Equals(Path.GetExtension(selected), ".aas", StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileNameWithoutExtension(selected);
            if (string.IsNullOrEmpty(name)) return false;
            string nativePath = resolveNativePath(name);
            if (string.IsNullOrWhiteSpace(nativePath)) return false;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(selected, Path.GetFullPath(nativePath), comparison)) return false;
            scenarioName = name;
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }
}
