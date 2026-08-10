using Notification.Engine.Models;

namespace Notification.Engine.Data;

public interface IHitosRepository
{
    // Envío diario
    Task<List<Hito>> ObtenerPendientesEnvioDiarioAsync(CancellationToken ct = default);

    Task MarcarReprogramarAsync(int hitoId, DateOnly fecha, CancellationToken ct = default);

    Task GuardarEnvioAsync(int hitoId, string messageId, DateOnly fecha, CancellationToken ct = default);

    Task GuardarErrorEnvioAsync(int hitoId, string error, CancellationToken ct = default);

    // Reprogramar
    Task<List<HitoParaReprogramar>> ObtenerCandidatosReprogramarAsync(CancellationToken ct = default);

    // Reset mensual
    Task<List<HitoParaReset>> ObtenerCandidatosResetAsync(CancellationToken ct = default);

    Task ResetearHitosAsync(IReadOnlyList<int> hitoIds, CancellationToken ct = default);

    // Actualizaciones en tiempo real
    Task<List<HitoParaActualizar>> ObtenerPendientesActualizarAsync(CancellationToken ct = default);

    Task DesmarcarActualizarAsync(int hitoId, CancellationToken ct = default);

    // Respuesta y registro. 0 filas afectadas indica un callback duplicado.
    Task<int> RegistrarHitoOkAsync(int hitoId, long tgUserId, string nombreCompleto, CancellationToken ct = default);

    Task<int> RegistrarHitoPospuestoAsync(int hitoId, DateOnly nuevaFecha, string accionTexto, long tgUserId, string nombreCompleto, CancellationToken ct = default);
}
