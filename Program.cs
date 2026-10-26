using CrabSenseKiosk.Data;
using CrabSenseKiosk.Middleware;
using CrabSenseKiosk.Services;
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

    // The edge gateway does not need the legacy SQL Server store. Keep the
    // registration opt-in so Linux deployments do not try to reach LocalDB.
    var useLocalDatabase = builder.Configuration.GetValue<bool>("Kiosk:UseLocalDatabase");
    if (useLocalDatabase)
    {
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
    }

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
    builder.Services.AddSingleton<UpsTelemetryService>();
    builder.Services.AddSingleton<TelemetryOutbox>();
    builder.Services.AddSingleton<IControllerRegistry, ControllerRegistry>();
    builder.Services.AddHostedService<EdgeCommandWorker>();

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

    // Initialize the optional local database only when explicitly enabled.
    if (useLocalDatabase)
    {
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
