using AirlineDemo.Api;

if (args.Length > 0 &&
    (args[0].Equals("--serve", StringComparison.OrdinalIgnoreCase) ||
     args[0].Equals("--serve-browser", StringComparison.OrdinalIgnoreCase)))
{
    var stateDirectory = Environment.GetEnvironmentVariable("AIRLINEDEMO_STATE_DIRECTORY")
        ?? Path.Combine(AppContext.BaseDirectory, "state");
    var urls = Environment.GetEnvironmentVariable("AIRLINEDEMO_URLS");
    var app = WorkflowApi.Create(stateDirectory, urls: urls);
    await app.RunAsync();
}
