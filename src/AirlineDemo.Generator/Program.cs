using AirlineDemo.Generator;

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var index = 0; index < args.Length; index++)
{
    if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
    {
        throw new ArgumentException($"Expected an option followed by a value: {args[index]}");
    }

    options[args[index][2..]] = args[++index];
}

var output = GetOption("output", Path.Combine(Environment.CurrentDirectory, ".airlinedemo-fixtures"));
var runId = GetOption("run-id", "RUN-DEMO-001");
var profile = GetOption("profile", "baseline");
var date = DateOnly.Parse(GetOption("date", "2026-09-13"));
var seed = long.Parse(GetOption("seed", "117"));
var fixtureVersion = GetOption("fixture-version", "1.0.0");
var fixture = FixtureGenerator.Generate(
    new GeneratorConfiguration(
        seed,
        fixtureVersion,
        date,
        runId,
        profile,
        output,
        new Dictionary<string, string>
        {
            ["dotnet"] = "10.0",
            ["generator"] = fixtureVersion
        }),
    "template-baseline-1");

Console.WriteLine($"FIXTURE_GENERATION_SUCCESS output={Path.GetFullPath(output)}");
Console.WriteLine($"run_id={runId}");
Console.WriteLine($"files={fixture.GeneratedFiles.Count}");

string GetOption(string name, string fallback) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : fallback;
