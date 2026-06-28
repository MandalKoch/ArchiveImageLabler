var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.FileZipPreview>("filezippreview")
    .WithEnvironment("Debug__StopApplicationOnLastBrowserClose", "true");

builder.Build().Run();
