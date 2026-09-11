using System.Reflection;
using System.Text.Json;

namespace AirlineDemo.Generator;

public static class SharedWorkflowArtifacts
{
    private const string WorkflowSchemaResource =
        "AirlineDemo.Generator.Contracts.workflow.schema.json";
    private const string OpenApiResource = "AirlineDemo.Generator.Contracts.openapi.json";

    public static string WorkflowSchemaJson => ReadResource(WorkflowSchemaResource);

    public static string OpenApiJson => ReadResource(OpenApiResource);

    public static string WorkflowSchemaVersion
    {
        get
        {
            using var schema = JsonDocument.Parse(WorkflowSchemaJson);
            return schema.RootElement
                .GetProperty("$defs")
                .GetProperty("SchemaVersion")
                .GetProperty("enum")[0]
                .GetString()
                ?? throw new InvalidOperationException("The shared workflow schema has no version.");
        }
    }

    public static IReadOnlySet<string> EventTypes
    {
        get
        {
            using var schema = JsonDocument.Parse(WorkflowSchemaJson);
            return schema.RootElement
                .GetProperty("$defs")
                .GetProperty("EventEnvelope")
                .GetProperty("properties")
                .GetProperty("type")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => value is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    private static string ReadResource(string resourceName)
    {
        using var stream = typeof(SharedWorkflowArtifacts).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The shared workflow contract resource '{resourceName}' is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
