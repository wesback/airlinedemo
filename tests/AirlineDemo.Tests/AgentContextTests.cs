using System.Text.RegularExpressions;

namespace AirlineDemo.Tests;

public sealed class AgentContextTests
{
    private static readonly string[] RequiredHeadings =
    [
        "# Repository-specific agent context",
        "## Domain invariants",
        "## Architecture and integration points",
        "## Verification and tooling",
        "## Operational workflows"
    ];

    [Fact]
    public void AgentContext_PreservesStructureAndExistingPathReferences()
    {
        var repositoryRoot = FindRepositoryRoot();
        var context = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "agent-context.md"));
        var headings = context
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("# ", StringComparison.Ordinal) ||
                line.StartsWith("## ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(RequiredHeadings, headings);

        for (var index = 1; index < RequiredHeadings.Length; index++)
        {
            var sectionStart = context.IndexOf(RequiredHeadings[index], StringComparison.Ordinal)
                + RequiredHeadings[index].Length;
            var sectionEnd = index + 1 < RequiredHeadings.Length
                ? context.IndexOf(RequiredHeadings[index + 1], sectionStart, StringComparison.Ordinal)
                : context.Length;
            var bullets = context[sectionStart..sectionEnd]
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .ToArray();

            Assert.InRange(bullets.Length, 3, 10);
            foreach (var bullet in bullets)
            {
                Assert.True(bullet.Length <= 300, $"Fact bullet exceeds 300 characters: {bullet}");
                var sentence = Regex.Replace(bullet, @"\s*\(`[^`]+`\)", string.Empty).Trim();
                Assert.True(
                    Regex.Matches(sentence, @"(?<=[a-z0-9])[.!?](?=\s|$)").Count == 1,
                    $"Fact bullet must contain one sentence: {bullet}");
            }
        }

        var verificationSectionStart = context.IndexOf(
            RequiredHeadings[3],
            StringComparison.Ordinal);
        var operationsSectionStart = context.IndexOf(
            RequiredHeadings[4],
            verificationSectionStart,
            StringComparison.Ordinal);
        var verificationSection = context[verificationSectionStart..operationsSectionStart];
        Assert.Contains("The exact test command is dotnet test", verificationSection, StringComparison.Ordinal);

        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "tests.yml"));
        Assert.Contains("- run: dotnet test", workflow, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)password|secret|token", context);
        Assert.DoesNotMatch(@"(?i)\bhttps?://[^\s/]*:[^\s/]*@", context);
        Assert.DoesNotMatch(@"(?i)\b(?:issue|pull request|pr)\s*#?\d+|#\d+", context);

        foreach (Match match in Regex.Matches(context, "`([^`]+)`"))
        {
            var referencedPath = Path.Combine(
                repositoryRoot,
                match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(
                File.Exists(referencedPath) || Directory.Exists(referencedPath),
                $"Referenced repository path does not exist: {match.Groups[1].Value}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AirlineDemo.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
