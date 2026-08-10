using Notification.Engine.Common;
using Notification.Engine.Data;
using Notification.Engine.Services;
using Notification.Engine.Telegram;

namespace Notification.Engine.Jobs;

// Job 1 - Envio Diario
// Lógica de ejecución de recodatorios diarios
public class EnvioDiarioJob(IServiceScopeFactory scopeFactory, ILogger<EnvioDiarioJob> logger) : MinuteBackgroundService(logger)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected override async Task ProcessAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var hitosRepo = scope.ServiceProvider.GetRequiredService<IHitosRepository>();
        var gruposRepo = scope.ServiceProvider.GetRequiredService<IGruposRepository>();
        var filtro = scope.ServiceProvider.GetRequiredService<IEnvioDiarioFilterService>();
        var telegram = scope.ServiceProvider.GetRequiredService<TelegramBotClient>();

        // Si Telegram informa que el chat migró a supergrupo (chat_id viejo invalidado para
        // siempre), corrige Tg_Grupo con el chat_id nuevo y reintenta una vez. Devuelve el
        // chat_id vigente para que el resto del envío a ese grupo lo use también.
        async Task<(TelegramSendResult Envio, string ChatId)> EnviarConMigracionAsync(
            string chatId, string texto, IReadOnlyList<IReadOnlyList<InlineKeyboardButton>>? teclado = null)
        {
            var envio = await telegram.EnviarMensajeAsync(chatId, texto, teclado, ct);
            if (envio is { Success: false, MigrateToChatId: { } nuevoChatId })
            {
                var chatIdNuevo = nuevoChatId.ToString();
                Logger.LogWarning("Chat {ChatIdViejo} fue migrado a supergrupo; actualizando Tg_Grupo a {ChatIdNuevo} y reintentando.", chatId, chatIdNuevo);
                await gruposRepo.ActualizarChatIdAsync(chatId, chatIdNuevo, ct);
                chatId = chatIdNuevo;
                envio = await telegram.EnviarMensajeAsync(chatId, texto, teclado, ct);
            }

            return (envio, chatId);
        }

        var pendientes = await hitosRepo.ObtenerPendientesEnvioDiarioAsync(ct);
        if (pendientes.Count == 0)
        {
            Logger.LogInformation("EnvioDiarioJob: sin grupos/hitos para esta hora.");
            return;
        }

        var resultado = filtro.Filtrar(pendientes, DateTime.Now);
        Logger.LogInformation(
            "EnvioDiarioJob: {Total} hitos leídos — {Lunes} a reprogramar al lunes, {SinRecordatorio} chats sin recordatorios, {ConHitos} chats con hitos a enviar.",
            pendientes.Count, resultado.MarcarLunes.Count, resultado.ChatsSinRecordatorios.Count, resultado.HitosPorChat.Count);

        foreach (var (hito, lunes) in resultado.MarcarLunes)
        {
            await hitosRepo.MarcarReprogramarAsync(hito.Id, lunes, ct);
        }

        foreach (var chatId in resultado.ChatsSinRecordatorios)
        {
            await EnviarConMigracionAsync(chatId, "✅ No hay recordatorios para hoy");
        }

        foreach (var (chatId, hitosDelChat) in resultado.HitosPorChat)
        {
            var (_, chatIdActual) = await EnviarConMigracionAsync(chatId, $"📅 Recordatorio - {DateTime.Now:dd/MM/yyyy}");

            foreach (var hito in hitosDelChat)
            {
                List<IReadOnlyList<InlineKeyboardButton>> teclado =
                [
                    [
                        new InlineKeyboardButton("✅OK", $"ok|{hito.Id}"),
                        new InlineKeyboardButton("⏰+1", $"posponer|{hito.Id}"),
                        new InlineKeyboardButton("⏰+2", $"posponer2|{hito.Id}"),
                        new InlineKeyboardButton("⏰+3", $"posponer3|{hito.Id}"),
                        new InlineKeyboardButton("⏰+4", $"posponer4|{hito.Id}")
                    ]
                ];

                TelegramSendResult envio;
                (envio, chatIdActual) = await EnviarConMigracionAsync(chatIdActual, $"- {hito.HitoTexto}", teclado);

                if (envio is { Success: true, MessageId: { } messageId })
                {
                    await hitosRepo.GuardarEnvioAsync(hito.Id, messageId.ToString(), DateOnly.FromDateTime(DateTime.Now), ct);
                    Logger.LogInformation("EnvioDiarioJob: hito {HitoId} enviado a chat {ChatId} (mensaje {MessageId}).", hito.Id, chatIdActual, messageId);
                }
                else
                {
                    var error = envio.ErrorDescription ?? "Error desconocido al enviar el mensaje.";
                    await hitosRepo.GuardarErrorEnvioAsync(hito.Id, error, ct);
                    Logger.LogWarning("No se pudo enviar/guardar el hito {HitoId} al chat {ChatId}: {Error}", hito.Id, chatIdActual, error);
                }
            }
        }
    }
}
