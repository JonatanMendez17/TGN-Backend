using Notification.Api.Channels;
using Notification.Api.Models.Request;
using Notification.Api.Models.Response;

namespace Notification.Api.Services;

public class MensajeriaService(IEnumerable<INotificationProvider> providers, ILogger<MensajeriaService> logger) : IMensajeriaService
{
    private readonly IEnumerable<INotificationProvider> _providers = providers;
    private readonly ILogger<MensajeriaService> _logger = logger;

    public async Task<EnviarMensajeResponse> EnviarAsync(EnviarMensajeRequest request)
    {
        var provider = _providers.FirstOrDefault(p => string.Equals(p.Canal, request.Canal, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            _logger.LogWarning("Canal no soportado: {Canal}. Sistema: {Sistema}", request.Canal, request.Sistema);
            return Respuesta(false, $"Canal '{request.Canal}' no soportado.", request.Canal);
        }

        var texto = ConstruirTexto(request);

        _logger.LogInformation("Enviando mensaje por {Canal} a {Destino}. Sistema: {Sistema}", provider.Canal, request.Destino, request.Sistema);

        var enviado = await provider.EnviarAsync(request.Destino, texto);

        return enviado
            ? Respuesta(true, "Mensaje enviado correctamente.", provider.Canal)
            : Respuesta(false, "Error al enviar el mensaje. Intente nuevamente.", provider.Canal);
    }

    private static string ConstruirTexto(EnviarMensajeRequest r) =>
        $"👤 De: {r.De}\n" +
        $"👥 Para: {r.Para}\n" +
        $"🏷️ {r.Titulo}\n\n" +
        $"📝 {r.Mensaje}";

    private static EnviarMensajeResponse Respuesta(bool exitoso, string mensaje, string canal) => new() { 
        Exitoso = exitoso, 
        Mensaje = mensaje, 
        Canal = canal, 
        Timestamp = DateTime.UtcNow 
    };
}
