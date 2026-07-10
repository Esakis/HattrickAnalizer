using System.Text.Json;
using HattrickAnalizer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection();
builder.Services.AddHttpClient<HattrickApiService>();
builder.Services.AddHttpClient<OAuthService>();
builder.Services.AddScoped<AdvancedLineupOptimizer>();
builder.Services.AddScoped<CalibrationService>();
builder.Services.AddScoped<OpponentScoutService>();
builder.Services.AddScoped<LeagueSimulationService>();
builder.Services.AddScoped<TrainingService>();
builder.Services.AddScoped<MatchOrdersService>();
builder.Services.AddSingleton<OAuthService>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<PlayerHistoryService>();

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.Logger.LogInformation("Dozwolone originy CORS: {Origins}", string.Join(", ", allowedOrigins));
if (app.Configuration.GetValue<bool>("UseMockData"))
{
    app.Logger.LogWarning("UseMockData=true — aplikacja serwuje dane przykładowe, nie dane z CHPP!");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAngular");

// Jednolite mapowanie błędów domenowych: brak autoryzacji → 401, błąd CHPP → 502.
// Dzięki temu frontend zawsze wie, czy ma przekierować do logowania, czy pokazać błąd.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException ex)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
    }
    catch (ChppApiException ex)
    {
        app.Logger.LogError(ex, "Błąd CHPP API");
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
    }
});

app.UseAuthorization();
app.MapControllers();

app.Run();
