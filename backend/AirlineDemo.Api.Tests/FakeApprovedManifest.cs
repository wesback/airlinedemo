using AirlineDemo.Api.Investigation;

namespace AirlineDemo.Api.Tests;

internal static class FakeApprovedManifest
{
    private const string ApprovedJson = """{"manifestVersion":"2026.10.1","approved":true,"arm":"investigation-live","deploymentName":"fixture-deployment","model":"fixture-model","apiVersion":"2025-01-01","region":"swedencentral","contentFilter":"enabled"}""";

    public static ApprovedModelManifest Approved() => ApprovedModelManifest.Parse(ApprovedJson);
}
