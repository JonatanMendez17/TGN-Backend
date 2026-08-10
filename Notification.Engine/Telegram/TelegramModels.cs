using System.Text.Json.Serialization;

namespace Notification.Engine.Telegram;

public sealed record InlineKeyboardButton(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("callback_data")] string CallbackData);

public sealed class TelegramSendResult
{
    public bool Success { get; init; }
    public long? MessageId { get; init; }
    public long? MigrateToChatId { get; init; }
    public string? ErrorDescription { get; init; }
}

internal sealed class TelegramApiResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public TelegramApiMessage? Result { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; set; }

    [JsonPropertyName("parameters")]
    public TelegramResponseParameters? Parameters { get; set; }
}

internal sealed class TelegramResponseParameters
{
    [JsonPropertyName("retry_after")]
    public int? RetryAfter { get; set; }

    // Presente cuando un grupo básico se convirtió en supergrupo: Telegram invalida el chat_id
    // viejo para siempre y devuelve acá el nuevo.
    [JsonPropertyName("migrate_to_chat_id")]
    public long? MigrateToChatId { get; set; }
}

internal sealed class TelegramApiMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }
}
