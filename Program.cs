using CrabSenseKiosk.Data;
using CrabSenseKiosk.Middleware;
using CrabSenseKiosk.Services;
using CrabSenseKiosk.Services.WaterBench;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/crabsense-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

try
{
    Log.Information("Starting CrabSense Kiosk API");

    // Add services to the container
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddHttpClient();
    // Do not forward a device credential through an HTTP redirect.
    builder.Services.AddHttpClient("controller-inventory")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.Configure<EdgeGatewayOptions>(
        builder.Configuration.GetSection(EdgeGatewayOptions.SectionName));
    var kioskSecretFile = Path.Combine(builder.Environment.ContentRootPath, "kiosk-secret.txt");
    if (File.Exists(kioskSecretFile))
    {
        var kioskSecret = File.ReadAllText(kioskSecretFile).Trim();
        builder.Services.PostConfigure<EdgeGatewayOptions>(options => options.KioskSecret = kioskSecret);
    }
    
    // Configure Swagger
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "CrabSense Kiosk API",
            Version = "v1",
            Description = "API để quản lý chất lượng nước và cảm biến trong hệ thống nuôi cua",
            Contact = new OpenApiContact
            {
                Name = "CrabSense Team",
                Email = "support@crabsense.com"
            }
        });
    });

    // Configure Database
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

    // Configure CORS
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll",
            policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
    });

    // Register Services
    builder.Services.AddScoped<IWaterQualityService, WaterQualityService>();
    builder.Services.AddScoped<ISensorService, SensorService>();
    builder.Services.AddScoped<IAlertService, AlertService>();
    builder.Services.AddSingleton<ITelemetryService, TelemetryService>();
    builder.Services.AddSingleton<IControllerRegistry, ControllerRegistry>();
    builder.Services.AddSingleton<ControllerEndpointRegistry>();
    builder.Services.AddHostedService<EdgeCommandWorker>();
    builder.Services.AddSingleton<KioskMonitoringMetrics>();
    builder.Services.AddHostedService<EdgeV2Worker>();
    builder.Services.AddHttpClient("kiosk-heartbeat").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddHostedService<KioskHeartbeatWorker>();
    builder.Services.AddHttpClient("camera-recording").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddHostedService<CameraRecordingWorker>();

    builder.Services.Configure<WaterBenchOptions>(builder.Configuration.GetSection(WaterBenchOptions.SectionName));
    builder.Services.AddSingleton<WaterBenchSession>(sp =>
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WaterBenchOptions>>().Value;
        return new WaterBenchSession(options,new EvidenceSpool(
            Path.Combine(builder.Environment.ContentRootPath,options.EvidenceDirectory),options.MaxSpoolBytes,options.MaxSpoolFiles));
    });
    builder.Services.AddSingleton<BenchSchedule>(sp =>
    {
        var options=sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WaterBenchOptions>>().Value;
        return new BenchSchedule(options,sp.GetRequiredService<WaterBenchSession>(),
            Path.Combine(builder.Environment.ContentRootPath,options.EvidenceDirectory,"schedule"),DateTimeOffset.UtcNow);
    });
    if (builder.Configuration.GetValue<bool>("WaterBench:Enabled"))
    {
        builder.Services.AddHostedService<WaterBenchWorker>();
        builder.Services.AddHostedService<BenchScheduleWorker>();
        if(builder.Configuration.GetValue<bool>("WaterBench:EnableBatchSync"))
        {
            builder.Services.AddHttpClient("water-bench-batch-sync",c=>
            {
                // Per-request deadlines are enforced by the sync worker (8s event / 60s media).
                c.Timeout=TimeSpan.FromSeconds(65);
                c.MaxResponseContentBufferSize=16384;
            }).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false});
            builder.Services.AddHostedService<BackendBenchBatchSyncWorker>();
        }
        if(builder.Configuration.GetValue<bool>("WaterBench:UseBackendSchedule"))
        {
            builder.Services.AddHttpClient("water-bench-config").ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
            builder.Services.AddHostedService<BackendBenchConfigurationWorker>();
        }
    }

    var app = builder.Build();

    // Use exception middleware
    app.UseMiddleware<ExceptionMiddleware>();

    // Configure the HTTP request pipeline
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "CrabSense Kiosk API v1");
            c.RoutePrefix = string.Empty; // Swagger UI tại root
        });
    }

    if (builder.Configuration.GetValue<bool>("Kiosk:UseHttpsRedirection"))
        app.UseHttpsRedirection();

    app.UseCors("AllowAll");

    app.UseAuthorization();

    app.MapControllers();

    // Initialize database
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            context.Database.Migrate();
            Log.Information("Database migration completed successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An error occurred while migrating the database");
        }
    }

    var backend = app.Configuration["EdgeGateway:BackendUrl"];
    var hasSecret = File.Exists(Path.Combine(app.Environment.ContentRootPath, "kiosk-secret.txt"));
    Log.Information(
        "Kiosk is ready. Backend {Backend}. Credential: {Credential}",
        backend,
        hasSecret ? "granted" : "missing");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application failed to start");
}
finally
{
    Log.CloseAndFlush();
}
