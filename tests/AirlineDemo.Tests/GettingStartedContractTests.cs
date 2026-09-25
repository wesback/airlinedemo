using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AirlineDemo.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Tests;

public sealed class GettingStartedContractTests
{
    [Fact]
    public void GuideNamesEveryRequiredDemoEnvironmentVariableAndAvoidsOutputPlaceholders()
    {
        var guide = ReadGuide();
        var root = FindRepositoryRoot();
        var requiredVariables = new[] { "bootstrap-demo.sh", "migrate-demo.sh", "load-demo-fixtures.sh" }
            .SelectMany(script =>
            {
                var content = File.ReadAllText(Path.Combine(root, "scripts", script));
                return Regex.Matches(content, @"(AIRLINEDEMO_[A-Z0-9_]+) is required")
                    .Select(match => match.Groups[1].Value);
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(requiredVariables);
        foreach (var variable in requiredVariables)
        {
            Assert.Contains(variable, guide, StringComparison.Ordinal);
            var isExported = Regex.IsMatch(
                guide,
                $@"^export {Regex.Escape(variable)}\s*=",
                RegexOptions.Multiline);
            if (!isExported)
            {
                var source = variable switch
                {
                    "AIRLINEDEMO_SQL_SERVER" => "sql_server_fully_qualified_domain_name",
                    "AIRLINEDEMO_SQL_DATABASE" => "sql_database_id",
                    _ => throw new Xunit.Sdk.XunitException(
                        $"Document {variable} as an export or name its value source.")
                };
                Assert.Contains(source, guide, StringComparison.Ordinal);
            }
        }

        var codeBlocks = ReadCodeBlocks(guide);
        Assert.DoesNotContain("<terraform-output-", codeBlocks, StringComparison.Ordinal);
        Assert.DoesNotContain("<approved-demo-token>", codeBlocks, StringComparison.Ordinal);
        Assert.Contains("AIRLINEDEMO_OPERATOR_VARS_FILE", guide, StringComparison.Ordinal);
        Assert.Contains("from the subscription owner", guide, StringComparison.Ordinal);
        Assert.Contains("Terraform outputs", guide, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapDoesNotNeedSqlOverridesBeforeApplyAndPassesDerivedOutputsToMigration()
    {
        var guide = ReadGuide();
        var invocation = guide.IndexOf("./scripts/bootstrap-demo.sh", StringComparison.Ordinal);
        Assert.True(invocation >= 0);
        Assert.DoesNotContain("export AIRLINEDEMO_SQL_SERVER", guide[..invocation],
            StringComparison.Ordinal);
        Assert.DoesNotContain("export AIRLINEDEMO_SQL_DATABASE", guide[..invocation],
            StringComparison.Ordinal);

        var dryRun = await RunScriptAsync(
            "scripts/bootstrap-demo.sh",
            ("AIRLINEDEMO_DRY_RUN", "1"));
        Assert.Equal(0, dryRun.ExitCode);
        Assert.Contains("DEMO_BOOTSTRAP_SUCCESS", dryRun.Output, StringComparison.Ordinal);

        var derived = await RunBootstrapWithSqlStubsAsync(null);
        Assert.NotEqual(0, derived.ExitCode);
        Assert.Contains("step 'fixtures' failed", derived.Output, StringComparison.Ordinal);
        AssertSqlArguments(derived.SqlArguments, "derived-sql.database.windows.net", "sqldb-from-resource-id");

        var overridden = await RunBootstrapWithSqlStubsAsync("approved-sql.database.windows.net");
        Assert.NotEqual(0, overridden.ExitCode);
        Assert.Contains("step 'fixtures' failed", overridden.Output, StringComparison.Ordinal);
        AssertSqlArguments(overridden.SqlArguments, "approved-sql.database.windows.net", "sqldb-from-resource-id");
    }

    [Fact]
    public async Task DocumentedCallerScopeIsAcceptedByPackagesEndpoint()
    {
        var guide = ReadGuide();
        var codeBlocks = ReadCodeBlocks(guide);
        foreach (var required in new[] { "run=", "airline=", "aircraft=", "lease=" })
        {
            Assert.Contains(required, codeBlocks, StringComparison.Ordinal);
        }

        Assert.Contains("export AIRLINEDEMO_AUTH_HEADER=\"Bearer run=$(", guide,
            StringComparison.Ordinal);
        foreach (var field in new[] { "runId", "airlineId", "aircraftId", "leaseId" })
        {
            Assert.Contains($"jq -er '.{field}' \"$MANIFEST\"", guide, StringComparison.Ordinal);
        }

        var exampleMatch = Regex.Match(
            codeBlocks,
            @"^run=RUN-DEMO-001;airline=AIRLINE-0001;aircraft=MOCK-AC-001;lease=LEASE-0001$",
            RegexOptions.Multiline);
        Assert.True(exampleMatch.Success, "The guide must include a concrete scoped caller example.");
        var authorization = "Bearer " + exampleMatch.Value;

        var stateDirectory = CreateTemporaryDirectory("airlinedemo-auth-");
        var app = WorkflowApi.Create(stateDirectory);
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);

            using var response = await client.PostAsJsonAsync(
                "/api/packages",
                new Dictionary<string, string>());

            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void NumberedSectionsAndPreflightCommandsAreInExecutionOrder()
    {
        var guide = ReadGuide();
        var headings = Regex.Matches(guide, @"^## \d+\. (.+)$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .ToArray();
        var requiredOrder = new[]
        {
            "prerequisites",
            "authenticate",
            "protected terraform state",
            "generate fixtures",
            "bootstrap",
            "validate",
            "tear down"
        };
        var positions = requiredOrder.Select(term =>
            Array.FindIndex(headings, heading => heading.Contains(term, StringComparison.Ordinal))).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);

        var prerequisites = GetSection(guide, "## 1.");
        foreach (var tool in new[] { "az", "terraform", "docker", "dotnet", "jq", "curl", "sqlcmd" })
        {
            Assert.Contains(tool, prerequisites, StringComparison.Ordinal);
        }

        var bootstrapSection = GetSection(guide, "## 5.");
        var bootstrapInvocation = bootstrapSection.IndexOf("./scripts/bootstrap-demo.sh",
            StringComparison.Ordinal);
        Assert.True(bootstrapInvocation > bootstrapSection.IndexOf(
            "npm run lint:demo-scripts", StringComparison.Ordinal));
        Assert.True(bootstrapInvocation > bootstrapSection.IndexOf(
            "npm run validate:terraform", StringComparison.Ordinal));
        var teardown = GetSection(guide, "## 7.");
        foreach (var variable in new[]
        {
            "AIRLINEDEMO_OPERATOR_VARS_FILE",
            "AIRLINEDEMO_SUBSCRIPTION_ID",
            "AIRLINEDEMO_TENANT_ID"
        })
        {
            Assert.Contains(variable, teardown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GuideDocumentsProtectedStateChecksBeforeWorkloadTerraformInitialization()
    {
        var root = FindRepositoryRoot();
        var guide = ReadGuide();
        var normalizedGuide = Regex.Replace(guide, @"\s+", " ");
        var backend = File.ReadAllText(Path.Combine(root, "terraform", "backend.tf"));
        var bootstrapScript = File.ReadAllText(Path.Combine(root, "scripts", "bootstrap-demo.sh"));
        var stateBootstrap = Path.Combine(root, "terraform", "bootstrap", "README.md");

        Assert.True(File.Exists(stateBootstrap));
        Assert.Contains(
            "[`terraform/bootstrap/README.md`](terraform/bootstrap/README.md)",
            guide,
            StringComparison.Ordinal);
        foreach (var (name, pattern) in new[]
        {
            ("resource group", @"resource_group_name\s*=\s*""([^""]+)"""),
            ("storage account", @"storage_account_name\s*=\s*""([^""]+)"""),
            ("container", @"container_name\s*=\s*""([^""]+)""")
        })
        {
            var match = Regex.Match(backend, pattern);
            Assert.True(match.Success, $"Terraform backend must define its {name}.");
            Assert.Contains(match.Groups[1].Value, guide, StringComparison.Ordinal);
        }

        Assert.Contains("Storage Blob Data Contributor", guide, StringComparison.Ordinal);
        Assert.Contains("role assigned at the state container scope", normalizedGuide,
            StringComparison.Ordinal);
        Assert.Contains("separate, owner-approved", normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("demo workload bootstrap does not provision or repair this backend",
            normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("demo teardown does not destroy it", normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("Azure Resource Manager", normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("az storage account show", guide, StringComparison.Ordinal);
        Assert.Contains("--resource-group rg-airlinedemo-state", guide, StringComparison.Ordinal);
        Assert.Contains("--name stairlinedemostate", guide, StringComparison.Ordinal);
        Assert.Contains("az storage container show", guide, StringComparison.Ordinal);
        Assert.Contains(
            "az storage container show \\\n  --account-name stairlinedemostate \\\n  --name tfstate \\\n  --auth-mode login",
            guide,
            StringComparison.Ordinal);
        Assert.Contains("--auth-mode login", guide, StringComparison.Ordinal);
        Assert.Contains("account is not found in the selected subscription", normalizedGuide,
            StringComparison.Ordinal);
        Assert.Contains("container check explicitly reports that `tfstate` is absent", normalizedGuide,
            StringComparison.Ordinal);
        Assert.Contains("stop here and ask the platform/state owner to follow the approved bootstrap procedure",
            normalizedGuide, StringComparison.Ordinal);
        Assert.Contains(
            "A DNS-resolution failure, connection timeout, blocked network, or authorization error is not proof that the account is absent",
            normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("lacks Azure Resource Manager read permission for the storage account",
            normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("Reader role at the account or a parent scope", normalizedGuide,
            StringComparison.Ordinal);
        Assert.Contains("Storage Blob Data Contributor is a data-plane role and does not grant this ARM read access",
            normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("verify the signed-in identity's container-scoped data role",
            normalizedGuide, StringComparison.Ordinal);

        var subscriptionSelection = guide.IndexOf("az account set --subscription", StringComparison.Ordinal);
        var accountCheck = guide.IndexOf("az storage account show", StringComparison.Ordinal);
        var containerCheck = guide.IndexOf("az storage container show", StringComparison.Ordinal);
        var workloadBootstrap = guide.IndexOf("./scripts/bootstrap-demo.sh", StringComparison.Ordinal);
        Assert.True(subscriptionSelection >= 0 && subscriptionSelection < accountCheck);
        Assert.True(accountCheck < containerCheck && containerCheck < workloadBootstrap);
        Assert.Contains("Complete both checks before running", normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("`terraform init` for the workload root", normalizedGuide, StringComparison.Ordinal);
        Assert.Contains("run_step terraform-init terraform -chdir=", bootstrapScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuideExplainsOperatorIngressAndCognitiveRanges()
    {
        var guide = ReadGuide();
        var example = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "terraform", "examples", "demo.tfvars"));

        Assert.Contains("container_allowed_source_ranges", guide, StringComparison.Ordinal);
        Assert.Contains("203.0.113.10/32", example, StringComparison.Ordinal);
        Assert.Contains("203.0.113.10/32", guide, StringComparison.Ordinal);
        Assert.Contains("replace the documentation-only", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operator public IP", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cognitive_allowed_ip_ranges", guide, StringComparison.Ordinal);
        Assert.Contains("sqlcmd -G", guide, StringComparison.Ordinal);
        Assert.Contains("SQL Entra administrator", guide, StringComparison.Ordinal);
        Assert.Contains("SQL firewall", guide, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixtureAndImageRunIdsMatchAndCanDefaultToTheGeneratorContract()
    {
        var guide = ReadGuide();
        var runIds = Regex.Matches(
                guide,
                "export AIRLINEDEMO_RUN_ID=\"([^\"]+)\"",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.True(runIds.Length >= 2);
        Assert.Single(runIds.Distinct(StringComparer.Ordinal));
        Assert.Equal("RUN-DEMO-001", runIds[0]);
        var generation = GetSection(guide, "## 4.");
        var bootstrap = GetSection(guide, "## 5.");
        Assert.Contains("--run-id \"$AIRLINEDEMO_RUN_ID\"", generation, StringComparison.Ordinal);
        Assert.Contains("export AIRLINEDEMO_RUN_ID=\"RUN-DEMO-001\"", bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("demo-RUN-DEMO-001", guide, StringComparison.Ordinal);

        var temporaryRoot = CreateTemporaryDirectory("airlinedemo-contract-");
        var fixtures = Path.Combine(temporaryRoot, "fixtures");
        var stubs = Path.Combine(temporaryRoot, "stubs");
        Directory.CreateDirectory(fixtures);
        Directory.CreateDirectory(stubs);
        await File.WriteAllTextAsync(
            Path.Combine(fixtures, "generator-contract.json"),
            """{"configuration":{"runId":"RUN-TEST-042"}}""");
        await WriteExecutableAsync(
            Path.Combine(stubs, "jq"),
            """
            #!/usr/bin/env bash
            [[ "$2" == ".configuration.runId" ]] &&
              grep -q "RUN-TEST-042" "$3" &&
              printf '%s\n' "RUN-TEST-042"
            """);

        try
        {
            var result = await RunScriptAsync(
                "scripts/bootstrap-demo.sh",
                ("AIRLINEDEMO_DRY_RUN", "1"),
                ("AIRLINEDEMO_RUN_ID", ""),
                ("AIRLINEDEMO_FIXTURE_ROOT", fixtures),
                ("PATH", $"{stubs}:{Environment.GetEnvironmentVariable("PATH")}"));
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("demo-RUN-TEST-042", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void GuideDescribesTheUnauthenticatedSmokeCheckAndPostDeploymentVerification()
    {
        var guide = ReadGuide();
        Assert.DoesNotContain("authenticated application smoke request", guide,
            StringComparison.OrdinalIgnoreCase);
        var verifySection = guide.IndexOf("### Verify the deployment", StringComparison.Ordinal);
        Assert.True(verifySection >= 0);
        var troubleshootingSection = guide.IndexOf("### Troubleshooting and re-running", verifySection,
            StringComparison.Ordinal);
        Assert.True(troubleshootingSection > verifySection);
        var verify = guide[verifySection..troubleshootingSection];
        Assert.Contains("app_endpoint", verify, StringComparison.Ordinal);
        Assert.Contains("DEMO_BOOTSTRAP_SUCCESS", verify, StringComparison.Ordinal);
        Assert.Contains("DEMO_FIXTURES_SUCCESS", verify, StringComparison.Ordinal);
        Assert.Contains("HTTP 200", verify, StringComparison.Ordinal);
        Assert.Contains("unauthenticated", guide, StringComparison.Ordinal);
        Assert.Contains("curl --fail --silent --show-error --output /dev/null", verify,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PrerequisitesNameVersionsAndEveryTerraformResourceProvider()
    {
        var root = FindRepositoryRoot();
        var guide = ReadGuide();
        var versions = File.ReadAllText(Path.Combine(root, "terraform", "versions.tf"));
        var requiredVersion = Regex.Match(versions, @"required_version\s*=\s*""([^""]+)""");
        Assert.True(requiredVersion.Success);
        Assert.Contains(requiredVersion.Groups[1].Value, guide, StringComparison.Ordinal);
        Assert.Contains("Node.js and npm", guide, StringComparison.Ordinal);
        Assert.Contains("shellcheck", guide, StringComparison.Ordinal);
        Assert.Contains("sqlcmd", guide, StringComparison.Ordinal);
        Assert.Contains("1.9.8", guide, StringComparison.Ordinal);

        var prerequisites = GetSection(guide, "## 1.");
        var resourceProviderPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["azurerm_application_insights"] = "Microsoft.Insights",
            ["azurerm_cognitive_"] = "Microsoft.CognitiveServices",
            ["azurerm_container_app"] = "Microsoft.App",
            ["azurerm_container_registry"] = "Microsoft.ContainerRegistry",
            ["azurerm_log_analytics_"] = "Microsoft.OperationalInsights",
            ["azurerm_mssql_"] = "Microsoft.Sql",
            ["azurerm_resource_group"] = "Microsoft.Resources",
            ["azurerm_role_assignment"] = "Microsoft.Authorization",
            ["azurerm_storage_"] = "Microsoft.Storage",
            ["azurerm_user_assigned_identity"] = "Microsoft.ManagedIdentity"
        };
        var terraformResourceTypes = Directory.GetFiles(
                Path.Combine(root, "terraform"),
                "*.tf",
                SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    @"resource\s+""(azurerm_[^""]+)""")
                .Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(terraformResourceTypes);
        var resourceProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resourceType in terraformResourceTypes)
        {
            var matchingPrefix = resourceProviderPrefixes.Keys
                .Where(prefix => resourceType.StartsWith(prefix, StringComparison.Ordinal))
                .OrderByDescending(prefix => prefix.Length)
                .FirstOrDefault();
            Assert.True(
                matchingPrefix is not null,
                $"Add an Azure provider mapping for Terraform resource {resourceType}.");
            resourceProviders.Add(resourceProviderPrefixes[matchingPrefix!]);
        }

        foreach (var provider in resourceProviders)
        {
            Assert.Contains(provider, prerequisites, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TroubleshootingProvidesExactCommandsForEachRecoverableFailure()
    {
        var guide = ReadGuide();
        var troubleshooting = guide[guide.IndexOf("### Troubleshooting and re-running",
            StringComparison.Ordinal)..];
        foreach (var failure in new[] { "Terraform apply failed", "Migration failed", "Fixture load failed" })
        {
            Assert.Contains(failure, troubleshooting, StringComparison.Ordinal);
        }

        Assert.Contains("then rerun `./scripts/bootstrap-demo.sh`", troubleshooting, StringComparison.Ordinal);
        Assert.Contains("rerun `./scripts/load-demo-fixtures.sh`", troubleshooting,
            StringComparison.Ordinal);
        Assert.Contains("./scripts/load-demo-fixtures.sh", troubleshooting, StringComparison.Ordinal);
        Assert.Contains("az sql server firewall-rule create", troubleshooting, StringComparison.Ordinal);

        var applyFailure = troubleshooting.IndexOf("- **Terraform apply failed:", StringComparison.Ordinal);
        var migrationFailure = troubleshooting.IndexOf("- **Migration failed:", StringComparison.Ordinal);
        var fixtureFailure = troubleshooting.IndexOf("- **Fixture load failed:", StringComparison.Ordinal);
        Assert.True(applyFailure >= 0 && migrationFailure > applyFailure && fixtureFailure > migrationFailure);
        Assert.Contains("./scripts/bootstrap-demo.sh",
            troubleshooting[applyFailure..migrationFailure], StringComparison.Ordinal);
        Assert.Contains("./scripts/bootstrap-demo.sh",
            troubleshooting[migrationFailure..fixtureFailure], StringComparison.Ordinal);
        Assert.Contains("./scripts/load-demo-fixtures.sh",
            troubleshooting[fixtureFailure..], StringComparison.Ordinal);
    }

    private static async Task<(string SqlArguments, int ExitCode, string Output)> RunBootstrapWithSqlStubsAsync(
        string? sqlServerOverride)
    {
        var temporaryRoot = CreateTemporaryDirectory("airlinedemo-bootstrap-stubs-");
        var stubs = Path.Combine(temporaryRoot, "bin");
        Directory.CreateDirectory(stubs);
        var sqlLog = Path.Combine(temporaryRoot, "sqlcmd.args");
        var terraformLog = Path.Combine(temporaryRoot, "terraform.args");
        await WriteExecutableAsync(
            Path.Combine(stubs, "terraform"),
            """
            #!/usr/bin/env bash
            printf '%s\n' "$*" >> "$STUB_TERRAFORM_LOG"
            if [[ "$*" == *"output -raw sql_server_fully_qualified_domain_name"* ]]; then
              printf '%s\n' "derived-sql.database.windows.net"
            elif [[ "$*" == *"output -raw sql_database_id"* ]]; then
              printf '%s\n' "/subscriptions/test/resourceGroups/test/providers/Microsoft.Sql/servers/sql-test/databases/sqldb-from-resource-id"
            fi
            """);
        await WriteExecutableAsync(
            Path.Combine(stubs, "az"),
            """
            #!/usr/bin/env bash
            if [[ "$*" == *"containerapp show"* ]]; then
              printf '%s\n' "airlinedemo-test.example.net"
            fi
            """);
        await WriteExecutableAsync(
            Path.Combine(stubs, "sqlcmd"),
            """
            #!/usr/bin/env bash
            printf '%s\n' "$*" >> "$STUB_SQL_LOG"
            """);
        foreach (var command in new[] { "docker", "dotnet", "jq", "curl" })
        {
            await WriteExecutableAsync(
                Path.Combine(stubs, command),
                "#!/usr/bin/env bash\nexit 0\n");
        }

        var config = Path.Combine(temporaryRoot, "demo.tfvars");
        var operatorVars = Path.Combine(temporaryRoot, "private.tfvars");
        await File.WriteAllTextAsync(config, string.Empty);
        await File.WriteAllTextAsync(operatorVars, string.Empty);
        try
        {
            var result = await RunScriptAsync(
                "scripts/bootstrap-demo.sh",
                ("AIRLINEDEMO_DRY_RUN", "0"),
                ("AIRLINEDEMO_FAIL_STEP", "fixtures"),
                ("AIRLINEDEMO_RUN_ID", "RUN-TEST-042"),
                ("AIRLINEDEMO_SUBSCRIPTION_ID", "00000000-0000-0000-0000-000000000000"),
                ("AIRLINEDEMO_TENANT_ID", "00000000-0000-0000-0000-000000000000"),
                ("AIRLINEDEMO_OPERATOR_VARS_FILE", operatorVars),
                ("AIRLINEDEMO_AUTH_HEADER", "Bearer " +
                    "run=RUN-TEST-042;airline=AIRLINE-0001;aircraft=MOCK-AC-001;lease=LEASE-0001"),
                ("AIRLINEDEMO_CONFIG_FILE", config),
                ("AIRLINEDEMO_TERRAFORM_ROOT", Path.Combine(temporaryRoot, "terraform")),
                ("AIRLINEDEMO_FIXTURE_ROOT", Path.Combine(temporaryRoot, "fixtures")),
                ("AIRLINEDEMO_SQL_SERVER", sqlServerOverride ?? ""),
                ("AIRLINEDEMO_SQL_DATABASE", ""),
                ("STUB_SQL_LOG", sqlLog),
                ("STUB_TERRAFORM_LOG", terraformLog),
                ("PATH", $"{stubs}:{Environment.GetEnvironmentVariable("PATH")}"));
            var sqlArguments = File.Exists(sqlLog) ? await File.ReadAllTextAsync(sqlLog) : string.Empty;
            var terraformArguments = await File.ReadAllTextAsync(terraformLog);
            var fullApply = terraformArguments.LastIndexOf("apply", StringComparison.Ordinal);
            var firstOutput = terraformArguments.IndexOf("output -raw", StringComparison.Ordinal);
            Assert.True(fullApply >= 0 && firstOutput > fullApply,
                "SQL Terraform outputs must be read after the full apply.");
            return (sqlArguments, result.ExitCode, result.Output);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void AssertSqlArguments(string arguments, string server, string database) =>
        Assert.Contains($"-S {server} -d {database}", arguments, StringComparison.Ordinal);

    private static async Task<ProcessResult> RunScriptAsync(
        string relativePath,
        params (string Name, string Value)[] variables)
    {
        var root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment.Remove("AIRLINEDEMO_SQL_SERVER");
        startInfo.Environment.Remove("AIRLINEDEMO_SQL_DATABASE");
        startInfo.ArgumentList.Add(Path.Combine(root, relativePath));
        foreach (var (name, value) in variables)
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the demo lifecycle script.");
        var output = await process.StandardOutput.ReadToEndAsync();
        output += await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output);
    }

    private static async Task WriteExecutableAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static string ReadCodeBlocks(string guide) =>
        string.Join(
            Environment.NewLine,
            Regex.Matches(guide, "```[^\\r\\n]*\\r?\\n(.*?)```", RegexOptions.Singleline)
                .Select(match => match.Groups[1].Value));

    private static string GetSection(string guide, string heading)
    {
        var start = guide.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected section heading {heading}.");
        var next = guide.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? guide[start..] : guide[start..next];
    }

    private static string ReadGuide() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "GETTING-STARTED.md"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "AirlineDemo.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
