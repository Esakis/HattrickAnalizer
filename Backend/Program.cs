using HattrickAnalizer.Services;

var builder = WebApplication.CreateBuilder(args);

Console.WriteLine("========== APPLICATION STARTING ==========");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<HattrickApiService>();
builder.Services.AddHttpClient<OAuthService>();
builder.Services.AddScoped<LineupOptimizerService>();
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAngular");
app.UseAuthorization();
app.MapControllers();

app.Run();
