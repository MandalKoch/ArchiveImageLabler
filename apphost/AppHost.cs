var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.ArchiveImageLabler>("archiveimagelabler")
    .WithEnvironment("Debug__StopApplicationOnLastBrowserClose", "true");

builder.Build().Run();
