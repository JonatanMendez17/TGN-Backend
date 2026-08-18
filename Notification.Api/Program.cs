using Notification.Api.Channels;
using Notification.Api.Channels.Telegram;
using Notification.Api.Infrastructure.Authentication;
using Notification.Api.Infrastructure.Logging;
using Notification.Api.Infrastructure.Settings;
using Notification.Api.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

SerilogSetup.Configure(builder.Configuration, "Notification.Api", "api");
builder.Services.AddSerilog();

// Settings
builder.Services.Configure<ApiSettings>(builder.Configuration.GetSection("ApiSettings"));
builder.Services.Configure<SqlSettings>(builder.Configuration.GetSection("Sql"));

// HTTP
builder.Services.AddHttpClient();

// Telegram — token único
builder.Services.AddSingleton<TelegramTokenProvider>();

// Providers — se resuelven por Canal en MensajeriaService.
// Agregar futuros canales (WhatsApp, Email, etc.) solo requiere otro AddScoped acá.
builder.Services.AddScoped<INotificationProvider, TelegramProvider>();

// Services
builder.Services.AddScoped<IMensajeriaService, MensajeriaService>();

// Autenticación por token — se exige en todos los endpoints de controllers.
builder.Services.AddAuthentication(AuthOptions.SchemeName).AddScheme<AuthOptions, AuthHandler>(AuthOptions.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddControllers();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireAuthorization();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
