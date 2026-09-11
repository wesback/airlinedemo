using AirlineDemo.Api;

if (args.Length > 0 && args[0].Equals("--serve", StringComparison.OrdinalIgnoreCase))
{
    var stateDirectory = Environment.GetEnvironmentVariable("AIRLINEDEMO_STATE_DIRECTORY")
        ?? Path.Combine(AppContext.BaseDirectory, "state");
    var app = WorkflowApi.Create(stateDirectory);
    await app.RunAsync();
}
