using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoCaseGenerator;
using UglyToad.PdfPig;

namespace AirlineDemo.Api.Tests;

public class DemoCaseTests
{
    private static readonly string[] RequiredSituations =
    [
        "baseline-complete",
        "missing-required-history",
        "ambiguous-identity",
        "corrupt-file",
        "truncated-file",
        "contradictory-later-document",
        "partner-linked-response",
    ];

    private static readonly string[] DeniedAirlineNames =
    [
        "Lufthansa", "Ryanair", "British Airways", "Emirates", "Qantas", "Air France", "KLM",
        "Delta Air Lines", "United Airlines", "American Airlines",
    ];

    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant),
        new(@"\+\d[\d\s().-]{7,}\d", RegexOptions.CultureInvariant),
        new(@"\(\d{3}\)\s?\d{3}[-\s]\d{4}", RegexOptions.CultureInvariant),
    ];

    private static string FixtureDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Fixtures", "DemoCase")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!, "Fixtures", "DemoCase");
    }

    private static List<string> FindViolations(string text)
    {
        var found = new List<string>();
        foreach (var name in DeniedAirlineNames)
        {
            if (Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b"))
            {
                found.Add(name);
            }
        }

        foreach (var pattern in ForbiddenPatterns)
        {
            found.AddRange(pattern.Matches(text).Select(m => m.Value));
        }

        return found;
    }

    /// <summary>Raw bytes as Latin-1 plus extracted PDF text. Damaged files that PdfPig cannot open are scanned as raw bytes only.</summary>
    private static string ReadText(string path)
    {
        var raw = File.ReadAllText(path, Encoding.Latin1);
        if (!path.EndsWith(".pdf", StringComparison.Ordinal))
        {
            return raw;
        }

        try
        {
            using var pdf = PdfDocument.Open(path);
            return raw + "\n" + string.Join("\n", pdf.GetPages().Select(p => p.Text));
        }
        catch (Exception)
        {
            return raw;
        }
    }

    [Fact]
    public void RegeneratedCaseIsByteIdenticalToCommittedFiles()
    {
        var temp = Path.Combine(Path.GetTempPath(), "demo-case-" + Guid.NewGuid().ToString("N"));
        try
        {
            Generator.Generate(temp);

            var committed = Directory.GetFiles(FixtureDir())
                .Select(Path.GetFileName)
                .Where(n => n != "README.md")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var generated = Directory.GetFiles(temp).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(committed, generated);

            foreach (var name in generated)
            {
                Assert.True(
                    File.ReadAllBytes(Path.Combine(FixtureDir(), name!)).AsSpan()
                        .SequenceEqual(File.ReadAllBytes(Path.Combine(temp, name!))),
                    $"{name} differs from the regenerated output; regenerate and review the change.");
            }
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }
    }

    [Fact]
    public void ExpectedJsonCoversEveryRequiredSituation()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir(), "expected.json")));
        var documents = json.RootElement.GetProperty("documents").EnumerateArray().ToList();
        var situations = documents.Select(d => d.GetProperty("situation").GetString()).ToHashSet();

        foreach (var required in RequiredSituations)
        {
            Assert.Contains(required, situations);
        }

        Assert.InRange(documents.Count, 8, 12);
        foreach (var document in documents)
        {
            var file = document.GetProperty("file").GetString()!;
            Assert.True(File.Exists(Path.Combine(FixtureDir(), file)), $"{file} is listed but not committed.");
        }
    }

    [Fact]
    public void CommittedFilesContainNoForbiddenPatterns()
    {
        var violations = new List<string>();
        foreach (var path in Directory.GetFiles(FixtureDir()))
        {
            foreach (var hit in FindViolations(ReadText(path)))
            {
                violations.Add($"{Path.GetFileName(path)}: {hit}");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ForbiddenPatternScanDetectsKnownBadSamples()
    {
        Assert.Contains("jane.doe@corp.com", FindViolations("contact jane.doe@corp.com today"));
        Assert.Contains("+44 20 7946 0958", FindViolations("call +44 20 7946 0958"));
        Assert.Contains("Lufthansa", FindViolations("released by Lufthansa technics"));
    }
}
