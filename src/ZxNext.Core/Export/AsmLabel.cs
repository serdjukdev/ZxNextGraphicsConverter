using System.Text;

namespace ZxNext.Core.Export;

/// <summary>
/// Turns arbitrary user names (tile/sprite/metatile/map names, which have no character restrictions in
/// the app) into valid Z80 assembler label bodies for the exported .asm files.
/// </summary>
public static class AsmLabel
{
    /// <summary>
    /// Every character outside <c>[A-Za-z0-9_]</c> (spaces, '.', '-', punctuation) becomes '_', runs of
    /// '_' collapse to one, leading/trailing '_' are trimmed, a leading digit gets a '_' in front, and an
    /// empty result falls back to "unnamed". Case is preserved. The caller adds its own category prefix
    /// (<c>t4_</c>, <c>s4_</c>, <c>m4_</c>, ...); '.' in particular MUST be replaced because sjasmplus
    /// treats it as a module separator.
    /// </summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        var lastWasUnderscore = false;
        foreach (var ch in name)
        {
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }

        var result = sb.ToString().Trim('_');
        if (result.Length == 0) return "unnamed";
        return char.IsDigit(result[0]) ? "_" + result : result;
    }

    /// <summary>
    /// <see cref="Sanitize"/> plus a de-collision pass against <paramref name="alreadyUsed"/> (which this
    /// mutates): sanitizing can turn two distinct names into the same string ("hero.a" and "hero-a" both
    /// become "hero_a"), so the second and later get a "_2", "_3", ... suffix. Pass the same set through
    /// every label in one file.
    /// </summary>
    public static string SanitizeUnique(string name, ISet<string> alreadyUsed)
    {
        var baseLabel = Sanitize(name);
        var candidate = baseLabel;
        var suffix = 2;
        while (!alreadyUsed.Add(candidate))
        {
            candidate = $"{baseLabel}_{suffix}";
            suffix++;
        }
        return candidate;
    }
}
