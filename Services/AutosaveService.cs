using System.IO;
using YorteVegas.ViewModels;

namespace YorteVegas.Services;

/// <summary>
/// Crash insurance: periodically writes the current edit beside the settings so
/// an unexpected close does not lose the session. The file is removed after a
/// normal save or a clean close.
/// </summary>
public static class AutosaveService
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YorteVegas", "autosave");

    public static string ProjectPath => Path.Combine(Folder, "autosave.yveg");

    private static string InfoPath => Path.Combine(Folder, "autosave.info");

    /// <summary>Project file the autosave belongs to (null for an untitled edit).</summary>
    public static string? OriginalPath { get; private set; }

    public static bool HasRecovery => File.Exists(ProjectPath);

    public static DateTime? WrittenUtc =>
        File.Exists(ProjectPath) ? File.GetLastWriteTimeUtc(ProjectPath) : null;

    public static void Save(EditProject project, string? projectPath)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            // Keep media paths relative to the real project folder, not to the
            // autosave folder, so recovery resolves the same files.
            var relativeTo = string.IsNullOrWhiteSpace(projectPath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(projectPath));

            ProjectService.Save(project, ProjectPath, relativeTo);
            File.WriteAllText(InfoPath, projectPath ?? "");
            OriginalPath = projectPath;
        }
        catch
        {
            // Autosave is best-effort; never interrupt editing for it.
        }
    }

    public static void LoadOriginalPath()
    {
        try
        {
            var text = File.Exists(InfoPath) ? File.ReadAllText(InfoPath).Trim() : "";
            OriginalPath = string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            OriginalPath = null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(ProjectPath)) File.Delete(ProjectPath);
            if (File.Exists(ProjectPath + ".bak")) File.Delete(ProjectPath + ".bak");
            if (File.Exists(InfoPath)) File.Delete(InfoPath);
        }
        catch
        {
            // Ignore: a stale autosave is harmless.
        }

        OriginalPath = null;
    }
}
