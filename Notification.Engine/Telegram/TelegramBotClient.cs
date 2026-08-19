using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Notification.Engine.Telegram;

// Cliente directo de Telegram Bot API para soportar funciones específicas.
public class TelegramBotClient(IHttpClientFactory httpClientFactory, TelegramTokenProvider tokenProvider, ILogger<TelegramBotClient> logger)
{
    private const string ApiBase = "https://api.telegram.org";
    private const int MaxIntentosPorDefecto = 3;

    private static readonly TimeSpan[] EsperaEntreIntentos = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan TopeEsperaRateLimit = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly TelegramTokenProvider _tokenProvider = tokenProvider;
    private readonly ILogger<TelegramBotClient> _logger = logger;

    public async Task<TelegramSendResult> EnviarMensajeAsync(string chatId, string text, IReadOnlyList<IReadOnlyList<InlineKeyboardButton>>? inlineKeyboard = null, CancellationToken ct = default)
    {
        var payload = new SendMessagePayload
        {
            ChatId = chatId,
            Text = text,
            ReplyMarkup = inlineKeyboard is { Count: > 0 }
                ? new ReplyMarkup { InlineKeyboard = inlineKeyboard }
                : null
        };

        var (exito, body) = await EnviarPeticionAsync("sendMessage", payload, $"chat {chatId}", ct);
        return new TelegramSendResult
        {
            Success = exito,
            MessageId = body?.ResultMessageId,
            MigrateToChatId = body?.Parameters?.MigrateToChatId,
            ErrorDescription = exito ? null : body?.Description
        };
    }

    // Actualiza mensajes enviados; si no pueden editarse, continúa sin interrumpir el proceso.
    public async Task<bool> EditarMensajeAsync(string chatId, string messageId, string text, CancellationToken ct = default)
    {
        var payload = new EditMessagePayload
        {
            ChatId = chatId,
            MessageId = messageId,
            Text = text,
            ReplyMarkup = new ReplyMarkup { InlineKeyboard = [] }
        };

        var (exito, _) = await EnviarPeticionAsync("editMessageText", payload, $"chat {chatId}, mensaje {messageId}", ct);
        return exito;
    }

    // Confirma visualmente la acción del botón; si falla, no interrumpe el procesamiento.
    // Sin reintentos: es solo cosmético (le saca el relojito al botón), el estado real se resuelve aparte.
    public async Task<bool> ResponderCallbackAsync(string callbackQueryId, CancellationToken ct = default)
    {
        var payload = new AnswerCallbackQueryPayload { CallbackQueryId = callbackQueryId };
        var (exito, _) = await EnviarPeticionAsync("answerCallbackQuery", payload, $"callback_query {callbackQueryId}", ct, maxIntentos: 1);
        return exito;
    }

    // Centraliza el POST a la Bot API: arma la URL, deserializa la respuesta, reintenta errores
    // transitorios (excepcion/5xx/429) y loguea warning/error de forma uniforme. Los errores
    // permanentes (ej. 400 "message can't be edited") no se reintentan, se devuelven al toque.
    private async Task<(bool Success, TelegramApiResponse? Body)> EnviarPeticionAsync<TPayload>(
        string metodo, TPayload payload, string contexto, CancellationToken ct, int maxIntentos = MaxIntentosPorDefecto)
    {
        for (var intento = 1; intento <= maxIntentos; intento++)
        {
            HttpStatusCode? statusCode = null;
            TelegramApiResponse? body = null;

            try
            {
                var client = _httpClientFactory.CreateClient();
                var token = await _tokenProvider.ObtenerTokenAsync(ct);
                var url = $"{ApiBase}/bot{token}/{metodo}";

                using var response = await client.PostAsJsonAsync(url, payload, ct);
                statusCode = response.StatusCode;
                body = await response.Content.ReadFromJsonAsync<TelegramApiResponse>(cancellationToken: ct);

                if (response.IsSuccessStatusCode && body is { Ok: true })
                {
                    return (true, body);
                }

                _logger.LogWarning("Telegram {Metodo} falló para {Contexto}: {StatusCode} {Descripcion}", metodo, contexto, statusCode, body?.Description);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al llamar a Telegram {Metodo} para {Contexto}.", metodo, contexto);
            }

            var esTransitorio = statusCode is null or HttpStatusCode.TooManyRequests || (int)statusCode.GetValueOrDefault() >= 500;
            if (intento == maxIntentos || !esTransitorio)
            {
                return (false, body);
            }

            var espera = statusCode == HttpStatusCode.TooManyRequests && body?.Parameters?.RetryAfter is { } retryAfter
                ? TimeSpan.FromSeconds(Math.Min(retryAfter, TopeEsperaRateLimit.TotalSeconds))
                : EsperaEntreIntentos[Math.Min(intento - 1, EsperaEntreIntentos.Length - 1)];

            _logger.LogWarning("Reintentando Telegram {Metodo} para {Contexto} en {EsperaSegundos}s (intento {Intento}/{Max}).", metodo, contexto, espera.TotalSeconds, intento + 1, maxIntentos);
            await Task.Delay(espera, ct);
        }

        return (false, null);
    }

    private sealed class AnswerCallbackQueryPayload
    {
        [JsonPropertyName("callback_query_id")]
        public string CallbackQueryId { get; set; } = string.Empty;
    }

    private sealed class EditMessagePayload
    {
        [JsonPropertyName("chat_id")]
        public string ChatId { get; set; } = string.Empty;

        [JsonPropertyName("message_id")]
        public string MessageId { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("reply_markup")]
        public ReplyMarkup? ReplyMarkup { get; set; }
    }

    private sealed class SendMessagePayload
    {
        [JsonPropertyName("chat_id")]
        public string ChatId { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("reply_markup")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ReplyMarkup? ReplyMarkup { get; set; }
    }

    private sealed class ReplyMarkup
    {
        [JsonPropertyName("inline_keyboard")]
        public IReadOnlyList<IReadOnlyList<InlineKeyboardButton>> InlineKeyboard { get; set; } = [];
    }
}
