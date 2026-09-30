using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetRumble.PlatformSpike;

/// <summary>
/// Asserts that every character the UI writes has a glyph in every UI font.
/// </summary>
/// <remarks>
/// A SpriteFont only rasterises the characters listed in its CharacterRegions. Anything
/// else is silently swapped for DefaultCharacter at draw time - no exception, no warning,
/// nothing in a log. The port shipped six fonts covering ASCII 32..126 while the UI wrote
/// an ellipsis in every progress string and an em dash in the weapon label and the final
/// standings, so all of those rendered a literal "?" and 334 passing checks had no way to
/// know, because the defect lives between the content pipeline and the source, in neither.
/// It took capturing a frame and looking at it.
///
/// This check reads both sides as text: the declared regions out of the .spritefont XML,
/// and the characters out of the string literals in NetRumble.Game. It needs no graphics
/// device, so it belongs here rather than in the game. Scanning literals is deliberately
/// broad - a literal that is never drawn costs at most one unnecessary glyph in an atlas,
/// whereas a missed one is invisible until someone looks at a screenshot.
/// </remarks>
internal static class FontCoverageChecks
{
    /// <summary>Matches a string literal, skipping over any escaped characters inside it.</summary>
    private static readonly Regex StringLiteral = new(
        "\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"",
        RegexOptions.Compiled);

    /// <summary>Matches a line comment, so a stray quote in prose cannot open a literal.</summary>
    private static readonly Regex Comment = new(
        @"^\s*(//|///|\*|/\*)",
        RegexOptions.Compiled);

    private static readonly Regex UnicodeEscape = new(
        @"\\u([0-9A-Fa-f]{4})",
        RegexOptions.Compiled);

    public static void Run()
    {
        Console.WriteLine("[16] Font glyph coverage");

        var root = FindRepoRoot();
        if (root is null)
        {
            Console.WriteLine("    SKIP could not locate NetRumble.slnx from the test binary");
            Console.WriteLine();
            return;
        }

        var fontDir = Path.Combine(root, "src", "NetRumble.Game", "Content", "Fonts");
        var sourceDir = Path.Combine(root, "src", "NetRumble.Game");

        var fonts = Directory.Exists(fontDir)
            ? Directory.GetFiles(fontDir, "*.spritefont")
            : [];

        Check($"found the UI fonts (got {fonts.Length})", fonts.Length > 0);
        if (fonts.Length == 0)
        {
            Console.WriteLine();
            return;
        }

        var used = CollectNonAsciiFromLiterals(sourceDir);

        // If this ever finds nothing the scan has broken, not the UI: the ellipsis and em
        // dash below are load-bearing parity with the GDScript and are not going away.
        Check(
            $"found non-ASCII characters in UI strings (got {Describe(used)})",
            used.Count > 0);

        Check("the ellipsis the progress strings use was found", used.Contains('\u2026'));
        Check("the em dash the weapon label uses was found", used.Contains('\u2014'));

        // Scanning literals can only ever find text the port chose. Display names are
        // chosen by players, arrive over the wire as UTF-8, and are drawn into the roster,
        // the scoreboard and the standings, so they need a declared repertoire instead.
        var required = new SortedSet<char>(used);
        foreach (var c in DisplayNameRepertoire())
        {
            required.Add(c);
        }

        foreach (var font in fonts.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(font);
            var covered = ReadCharacterRegions(font);

            if (covered is null)
            {
                Check($"{name} declares CharacterRegions", false);
                continue;
            }

            var missing = required.Where(c => !covered.Contains(c)).ToList();

            Check(
                missing.Count == 0
                    ? $"{name} covers every character the UI writes and the display-name repertoire"
                    : $"{name} is missing {Describe(missing.Take(8))}{(missing.Count > 8 ? $" and {missing.Count - 8} more" : string.Empty)}, which would render as DefaultCharacter",
                missing.Count == 0);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// The characters a remote player's display name is guaranteed to be able to contain.
    /// </summary>
    /// <remarks>
    /// Nothing sanitises display names - the codec carries whatever UTF-8 the platform
    /// hands over, and <c>PartyTransportChecks</c> deliberately round-trips an accented
    /// one to prove it. Latin-1 Supplement and Latin Extended-A cover Western and Central
    /// European names, which is as far as a fixed glyph atlas can reasonably go. Godot
    /// rasterised on demand and had no such ceiling; a name outside this range still
    /// degrades to DefaultCharacter here, which is a known and documented limitation
    /// rather than a defect this check can catch.
    /// </remarks>
    private static IEnumerable<char> DisplayNameRepertoire()
    {
        for (var c = '\u00a0'; c <= '\u017f'; c++)
        {
            yield return c;
        }
    }

    /// <summary>
    /// Collects every non-ASCII character appearing inside a string literal, resolving
    /// <c>\uXXXX</c> escapes, which is how the port writes most of them.
    /// </summary>
    private static SortedSet<char> CollectNonAsciiFromLiterals(string sourceDir)
    {
        var found = new SortedSet<char>();
        if (!Directory.Exists(sourceDir))
        {
            return found;
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            // Generated output mirrors the sources and would only double-count.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                if (Comment.IsMatch(line))
                {
                    continue;
                }

                foreach (Match literal in StringLiteral.Matches(line))
                {
                    Absorb(found, literal.Value);
                }
            }
        }

        return found;
    }

    private static void Absorb(SortedSet<char> found, string literal)
    {
        foreach (Match escape in UnicodeEscape.Matches(literal))
        {
            var value = (char)Convert.ToInt32(escape.Groups[1].Value, 16);
            if (value > 126)
            {
                found.Add(value);
            }
        }

        foreach (var c in UnicodeEscape.Replace(literal, string.Empty))
        {
            if (c > 126)
            {
                found.Add(c);
            }
        }
    }

    /// <summary>
    /// Reads the inclusive Start/End pairs out of a .spritefont. XML character references
    /// are resolved by the parser, so both <c>&amp;#32;</c> and a literal glyph work.
    /// Whitespace must be preserved or the space at <c>&amp;#32;</c> - the first character
    /// of the ASCII region - is discarded as an insignificant text node and the region
    /// reads as having no start at all.
    /// </summary>
    private static HashSet<char>? ReadCharacterRegions(string path)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var covered = new HashSet<char>();
        var regions = document.Descendants()
            .Where(e => e.Name.LocalName == "CharacterRegion")
            .ToList();

        if (regions.Count == 0)
        {
            return null;
        }

        foreach (var region in regions)
        {
            var start = Endpoint(region, "Start");
            var end = Endpoint(region, "End");

            if (start is null || end is null || end < start)
            {
                return null;
            }

            for (var c = start.Value; c <= end.Value; c++)
            {
                covered.Add(c);
            }
        }

        return covered;
    }

    private static char? Endpoint(XElement region, string name)
    {
        var text = region.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        return string.IsNullOrEmpty(text) ? null : text[0];
    }

    private static string Describe(IEnumerable<char> characters)
        => string.Join(", ", characters.Select(c => $"U+{(int)c:X4} '{c}'"));

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution, so the check
    /// works from <c>dotnet run</c> and from a published binary alike.
    /// </summary>
    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetRumble.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
}
