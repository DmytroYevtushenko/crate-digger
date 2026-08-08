namespace Crate.Api;

/// <summary>
/// A private scratch folder per download attempt.
///
/// Both downloaders (sldl and yt-dlp) figure out what they got by diffing the destination folder
/// before and after the run — the tool itself won't tell us the path. With everything writing into
/// the same inbox that diff is a lie the moment two downloads overlap: a manual YouTube grab that
/// finishes mid-run looks like "the file sldl just produced", so it gets attributed to the wrong
/// track (and the loser's leftover-cleanup would happily delete it). Staging inside a per-attempt
/// folder makes the diff exact: everything in there belongs to this attempt and nothing else.
///
/// The folder lives under the destination so the final move is a rename on the same filesystem.
/// </summary>
public static class Staging
{
    private const string Root = ".crate-staging";

    public static string Create(string destDir)
    {
        var dir = Path.Combine(destDir, Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Moves a staged file into destDir, side-stepping a name that is already taken.</summary>
    public static string MoveOut(string file, string destDir)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        var target = Path.Combine(destDir, name + ext);
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(destDir, $"{name} ({n}){ext}");
        File.Move(file, target);
        return target;
    }

    /// <summary>True for a path inside someone's scratch folder — a half-written file, not library content.</summary>
    public static bool IsStaged(string path) =>
        path.Contains(Path.DirectorySeparatorChar + Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public static void Discard(string stageDir)
    {
        try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>Every file the attempt produced, deepest first — sldl may nest them in album folders.</summary>
    public static List<string> Files(string stageDir, string[] extensions) =>
        Directory.Exists(stageDir)
            ? Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList()
            : [];
}
