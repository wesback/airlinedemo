using System.Text.RegularExpressions;

// Covers FR-011, NFR-006 and AC-018: the container image is a multi-stage build with a pinned base image,
// a non-root runtime user, a single exposed port and a .dockerignore that keeps secrets and build output out.
// Reads the checked-out files only; the image itself is verified by review.
public class DockerfileTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && File.Exists(Path.Combine(dir.FullName, ".dockerignore")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"No Dockerfile and .dockerignore found above {AppContext.BaseDirectory}.");
    }

    private static List<string> Instructions() =>
        File.ReadAllLines(Path.Combine(RepoRoot, "Dockerfile"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    private static List<string> StageBody(string name)
    {
        var body = new List<string>();
        var inStage = false;
        foreach (var line in Instructions())
        {
            if (line.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            {
                inStage = Regex.IsMatch(line, $@"\sAS\s+{Regex.Escape(name)}$", RegexOptions.IgnoreCase);
            }
            if (inStage)
            {
                body.Add(line);
            }
        }
        Assert.NotEmpty(body);
        return body;
    }

    [Fact]
    public void DeclaresFrontendBuildAndRuntimeStagesInOrder()
    {
        var stages = Instructions()
            .Select(l => Regex.Match(l, @"^FROM\s+\S+\s+AS\s+(\S+)$", RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(new List<string> { "frontend", "build", "runtime" }, stages);
    }

    [Fact]
    public void FrontendStageInstallsAndBuildsTheViteClient()
    {
        var body = StageBody("frontend");

        Assert.Contains("RUN npm ci", body);
        Assert.Contains("RUN npm run build", body);
    }

    [Fact]
    public void BuildStageCopiesClientIntoWwwrootAndPublishesApi()
    {
        var body = StageBody("build");

        Assert.Contains("COPY --from=frontend /src/frontend/dist/ ./backend/AirlineDemo.Api/wwwroot/", body);
        Assert.Contains(body, l => l.StartsWith("RUN dotnet publish backend/AirlineDemo.Api/AirlineDemo.Api.csproj", StringComparison.Ordinal)
                                   && l.Contains("-c Release") && l.Contains("-o /out"));
    }

    [Fact]
    public void RuntimeStageCopiesPublishedOutputAndExposesOnlyPort8080()
    {
        var body = StageBody("runtime");

        Assert.Contains("COPY --from=build /out/ ./", body);
        var exposes = Instructions().Where(l => l.StartsWith("EXPOSE ", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(new List<string> { "EXPOSE 8080" }, exposes);
    }

    private static void AssertExactVersionTag(string image)
    {
        var lastSegment = image[(image.LastIndexOf('/') + 1)..];
        var colon = lastSegment.IndexOf(':');
        Assert.True(colon > 0, $"Base image '{image}' has no tag.");
        var tag = lastSegment[(colon + 1)..];
        Assert.NotEqual("latest", tag, StringComparer.OrdinalIgnoreCase);
        // Full major.minor.patch version required; floating tags such as "22" or "22.12" are rejected.
        Assert.Matches(@"^\d+\.\d+\.\d+(-[A-Za-z0-9.]+)*$", tag);
    }

    [Fact]
    public void EveryFromUsesAnExactVersionTagNotLatest()
    {
        var images = Instructions()
            .Where(l => l.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase))
            .Select(l => Regex.Match(l, @"^FROM\s+(?:--\S+\s+)?(\S+)", RegexOptions.IgnoreCase).Groups[1].Value)
            .ToList();

        Assert.NotEmpty(images);
        foreach (var image in images)
        {
            AssertExactVersionTag(image);
        }
    }

    [Theory]
    [InlineData("node:latest")]
    [InlineData("node")]
    [InlineData("node:22")]
    [InlineData("node:22.12")]
    [InlineData("mcr.microsoft.com/dotnet/sdk:10")]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:10.0")]
    public void ExactVersionTagCheckRejectsFloatingOrMissingTags(string image)
    {
        Assert.ThrowsAny<Exception>(() => AssertExactVersionTag(image));
    }

    [Theory]
    [InlineData("node:22.12.0-bookworm-slim")]
    [InlineData("mcr.microsoft.com/dotnet/sdk:10.0.100")]
    public void ExactVersionTagCheckAcceptsPinnedTags(string image)
    {
        AssertExactVersionTag(image);
    }

    [Fact]
    public void RuntimeStageRunsAsNonRootUserInAppWorkdir()
    {
        var body = StageBody("runtime");

        var user = body.LastOrDefault(l => l.StartsWith("USER ", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(user);
        var userValue = user!["USER ".Length..].Trim();
        var uid = userValue.Split(':')[0];
        Assert.False(string.IsNullOrWhiteSpace(uid));
        Assert.NotEqual("root", uid, StringComparer.OrdinalIgnoreCase);
        Assert.False(int.TryParse(uid, out var numericUid) && numericUid == 0, $"Runtime USER '{uid}' resolves to root.");

        var workdir = body.LastOrDefault(l => l.StartsWith("WORKDIR ", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("WORKDIR /app", workdir);
    }

    [Theory]
    [InlineData("**/.git")]
    [InlineData("**/node_modules")]
    [InlineData("**/bin")]
    [InlineData("**/obj")]
    [InlineData("**/TestResults")]
    [InlineData("frontend/dist")]
    [InlineData("**/.env")]
    [InlineData("**/.env.*")]
    public void DockerignoreExcludesRequiredPattern(string pattern)
    {
        var patterns = File.ReadAllLines(Path.Combine(RepoRoot, ".dockerignore"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet();

        Assert.Contains(pattern, patterns);
    }
}
