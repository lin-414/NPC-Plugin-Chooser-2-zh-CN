using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Removes the folders a deleted mugshot leaves behind. All three mugshot
/// sources share the layout <c>&lt;Root&gt;\&lt;Mod&gt;\&lt;Plugin&gt;\&lt;FormID&gt;.png</c>,
/// so deleting the last image for a plugin strands an empty plugin folder, and
/// deleting the last plugin strands an empty mod folder.
///
/// <para>Split out of the tile view model because it deletes directories on the
/// user's disk and its two safety properties — never delete the configured root,
/// never climb above it — are worth pinning by test rather than by reading. The
/// root guard is what keeps a mugshot that turns out to live outside its
/// configured folder (a hand-edited path, a moved drive, a symlink) from walking
/// the delete upward into unrelated directories.</para>
/// </summary>
public static class MugshotFolderPruner
{
    /// <summary>Deletes <paramref name="startDir"/> and each parent above it while
    /// they are empty, stopping below <paramref name="root"/>. Returns the folders
    /// actually deleted, outermost last, so the caller can unregister any of them
    /// that a mod was pointing at.
    /// <para>Stops silently — returning what it managed to delete — on the first
    /// non-empty folder, the root, a path outside the root, or an I/O failure. A
    /// folder that cannot be removed is not an error worth interrupting a delete
    /// for; it just stays.</para></summary>
    public static IReadOnlyList<string> Prune(string? startDir, string? root)
    {
        var deleted = new List<string>();
        if (string.IsNullOrWhiteSpace(startDir) || string.IsNullOrWhiteSpace(root)) return deleted;

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch
        {
            return deleted;
        }

        var current = startDir;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string normalized;
            try
            {
                normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
            }
            catch
            {
                return deleted;
            }

            if (!IsStrictlyUnder(normalized, normalizedRoot)) return deleted;

            try
            {
                if (!Directory.Exists(normalized)) return deleted;
                if (Directory.EnumerateFileSystemEntries(normalized).Any()) return deleted;
                Directory.Delete(normalized);
            }
            catch
            {
                return deleted;
            }

            deleted.Add(normalized);
            current = Path.GetDirectoryName(normalized);
        }

        return deleted;
    }

    /// <summary>True when <paramref name="candidate"/> sits inside
    /// <paramref name="folder"/>. Strict: the folder is not under itself, which is
    /// what stops the walk from deleting the configured root. Both arguments must
    /// already be absolute and free of a trailing separator.</summary>
    public static bool IsStrictlyUnder(string candidate, string folder)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(folder)) return false;
        return candidate.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Absolute-path convenience wrapper over
    /// <see cref="IsStrictlyUnder(string, string)"/> for raw, possibly relative or
    /// trailing-separator inputs. Returns false rather than throwing on a malformed
    /// path.</summary>
    public static bool Contains(string? folder, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(candidatePath)) return false;
        try
        {
            return IsStrictlyUnder(
                Path.GetFullPath(candidatePath),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        }
        catch
        {
            return false;
        }
    }
}
