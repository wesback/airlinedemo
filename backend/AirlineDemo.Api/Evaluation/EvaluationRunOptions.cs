namespace AirlineDemo.Api.Evaluation;
public sealed record EvaluationRunOptions(string SuiteManifestPath, string CasesPath, string ArmBManifestPath, string ArmCManifestPath)
{ public const long MaximumReservedTokens = 3_000_000; }
