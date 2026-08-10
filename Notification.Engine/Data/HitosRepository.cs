using System.Data;
using Microsoft.Data.SqlClient;
using Notification.Engine.Models;

namespace Notification.Engine.Data;

public class HitosRepository(ISqlDataAccess db) : IHitosRepository
{
    // Hora de ejecución: hito > grupo > configuración global (parametria).
    // Se compara HH:mm completo (no solo la hora) — el Job que consume esto revisa cada minuto.
    private const string SqlPendientesEnvioDiario = """
        SELECT h.id, h.dia_mensual, h.hito, h.estado, h.reprogramar, h.msg_id,
               t.Tgg_Chat_Id, h.Envia_Fin_Semana
        FROM dbo.Hitos_Mensuales h
        JOIN dbo.Tg_Grupo t ON t.Tgg_Id = h.Tgg_id
        WHERE t.Tgg_Estado = 1
          AND t.Tgg_Chat_Id IS NOT NULL
          AND LTRIM(RTRIM(CAST(t.Tgg_Chat_Id AS varchar))) != ''
          AND FORMAT(GETDATE(), 'HH:mm') = ISNULL(
                LEFT(h.Hora_Envio, 5),
                ISNULL(
                    LEFT(t.Tgg_Hora_Envio, 5),
                    (SELECT LEFT(par_valor, 5) FROM dbo.Parametria WHERE par_clave = 'hora_envio_diario' AND par_vigente = 1)))
        """;

    // Usa la hora de revisión; el filtrado se resuelve directamente en el Job
    private const string SqlCandidatosReprogramar = """
        SELECT h.id, h.hito, h.estado, h.reprogramar, h.msg_id, t.Tgg_Chat_Id
        FROM dbo.Hitos_Mensuales h
        JOIN dbo.Tg_Grupo t ON t.Tgg_Id = h.Tgg_Id
        WHERE FORMAT(GETDATE(), 'HH:mm') = (
            SELECT LEFT(par_valor, 5) FROM dbo.Parametria WHERE par_clave = 'hora_revision' AND par_vigente = 1)
        """;

    // Reset mensual: se ejecuta según hora_reset los días 1 y 15.
    private const string SqlCandidatosReset = """
        SELECT id, dia_mensual, estado
        FROM dbo.Hitos_Mensuales
        WHERE FORMAT(GETDATE(), 'HH:mm') = (
            SELECT LEFT(par_valor, 5) FROM dbo.Parametria WHERE par_clave = 'hora_reset' AND par_vigente = 1)
          AND (DAY(GETDATE()) = 1 OR DAY(GETDATE()) = 15)
        """;

    // Actualizaciones en tiempo real: correcciones hechas desde la app web que todavía no se reflejaron en Telegram.
    private const string SqlPendientesActualizar = """
        SELECT h.id, h.hito, h.estado, h.msg_id, t.Tgg_Chat_Id
        FROM dbo.Hitos_Mensuales h
        JOIN dbo.Tg_Grupo t ON t.Tgg_Id = h.Tgg_Id
        WHERE h.tg_actualizar = 1
          AND h.msg_id IS NOT NULL
          AND h.msg_id != ''
        """;

    private readonly ISqlDataAccess _db = db;

    public Task<List<Hito>> ObtenerPendientesEnvioDiarioAsync(CancellationToken ct = default) =>
        _db.ConsultarAsync(SqlPendientesEnvioDiario, Mapear, ct: ct);

    public Task MarcarReprogramarAsync(int hitoId, DateOnly fecha, CancellationToken ct = default) =>
        _db.EjecutarAsync(
            "UPDATE dbo.Hitos_Mensuales SET reprogramar = @Fecha WHERE id = @Id",
            [
                new SqlParameter("@Fecha", SqlDbType.Date) { Value = fecha.ToDateTime(TimeOnly.MinValue) },
                new SqlParameter("@Id", SqlDbType.Int) { Value = hitoId }
            ],
            ct);

    public Task GuardarEnvioAsync(int hitoId, string messageId, DateOnly fecha, CancellationToken ct = default) =>
        _db.EjecutarAsync(
            """
            UPDATE dbo.Hitos_Mensuales
            SET msg_id = @MsgId, reprogramar = @Fecha, Envio_Error = NULL, Envio_Error_Fecha = NULL
            WHERE id = @Id
            """,
            [
                new SqlParameter("@MsgId", SqlDbType.NVarChar, 50) { Value = messageId },
                new SqlParameter("@Fecha", SqlDbType.Date) { Value = fecha.ToDateTime(TimeOnly.MinValue) },
                new SqlParameter("@Id", SqlDbType.Int) { Value = hitoId }
            ],
            ct);

    // Deja registro visible desde la app de por qué no se pudo mandar un hito (ej. chat inválido,
    // bot expulsado, texto rechazado) — antes solo quedaba en el log del servidor.
    public Task GuardarErrorEnvioAsync(int hitoId, string error, CancellationToken ct = default) =>
        _db.EjecutarAsync(
            "UPDATE dbo.Hitos_Mensuales SET Envio_Error = @Error, Envio_Error_Fecha = GETDATE() WHERE id = @Id",
            [
                new SqlParameter("@Error", SqlDbType.NVarChar, 300) { Value = error },
                new SqlParameter("@Id", SqlDbType.Int) { Value = hitoId }
            ],
            ct);

    public Task<List<HitoParaReprogramar>> ObtenerCandidatosReprogramarAsync(CancellationToken ct = default) =>
        _db.ConsultarAsync(SqlCandidatosReprogramar, MapearReprogramar, ct: ct);

    public Task<List<HitoParaReset>> ObtenerCandidatosResetAsync(CancellationToken ct = default) =>
        _db.ConsultarAsync(SqlCandidatosReset, MapearReset, ct: ct);

    public Task ResetearHitosAsync(IReadOnlyList<int> hitoIds, CancellationToken ct = default)
    {
        if (hitoIds.Count == 0) return Task.CompletedTask;

        var nombresParametros = hitoIds.Select((_, i) => $"@Id{i}").ToArray();
        var parametros = hitoIds.Select((id, i) => new SqlParameter($"@Id{i}", SqlDbType.Int) { Value = id });

        var sql = $"""
            UPDATE dbo.Hitos_Mensuales
            SET estado = 'Pendiente', reprogramar = NULL,
                Ultima_Respuesta_Tg_Id = NULL, Ultima_Respuesta_Nombre = NULL,
                Ultima_Respuesta_Accion = NULL, Ultima_Respuesta_Fecha = NULL
            WHERE id IN ({string.Join(",", nombresParametros)})
            """;

        return _db.EjecutarAsync(sql, parametros, ct);
    }

    public Task<List<HitoParaActualizar>> ObtenerPendientesActualizarAsync(CancellationToken ct = default) =>
        _db.ConsultarAsync(SqlPendientesActualizar, MapearActualizar, ct: ct);

    public Task DesmarcarActualizarAsync(int hitoId, CancellationToken ct = default) =>
        _db.EjecutarAsync(
            "UPDATE dbo.Hitos_Mensuales SET tg_actualizar = 0 WHERE id = @Id",
            [new SqlParameter("@Id", SqlDbType.Int) { Value = hitoId }],
            ct);

    // Evita sobrescribir la auditoría; si ya estaba OK, indica callback duplicado.
    public Task<int> RegistrarHitoOkAsync(int hitoId, long tgUserId, string nombreCompleto, CancellationToken ct = default) =>
        _db.EjecutarAsync(
            """
            UPDATE dbo.Hitos_Mensuales
            SET estado = 'OK', Ultima_Respuesta_Tg_Id = @TgId, Ultima_Respuesta_Nombre = @Nombre,
                Ultima_Respuesta_Accion = 'OK', Ultima_Respuesta_Fecha = GETDATE()
            WHERE id = @Id AND estado <> 'OK'
            """,
            [
                new SqlParameter("@TgId", SqlDbType.BigInt) { Value = tgUserId },
                new SqlParameter("@Nombre", SqlDbType.NVarChar, 200) { Value = nombreCompleto },
                new SqlParameter("@Id", SqlDbType.Int) { Value = hitoId }
            ],
            ct);

    // Similar a RegistrarHitoOkAsync Si no hay cambios reales, se considera un callback duplicado.
    public Task<int> RegistrarHitoPospuestoAsync(int hitoId, DateOnly nuevaFecha, string accionTexto, long tgUserId, string nombreCompleto, CancellationToken ct = default) =>
        _db.EjecutarAsync(
            """
            UPDATE dbo.Hitos_Mensuales
            SET reprogramar = @Fecha, estado = 'Pendiente', Ultima_Respuesta_Tg_Id = @TgId,
                Ultima_Respuesta_Nombre = @Nombre, Ultima_Respuesta_Accion = @Accion, Ultima_Respuesta_Fecha = GETDATE()
            WHERE id = @Id
              AND NOT (reprogramar = @Fecha AND Ultima_Respuesta_Accion = @Accion)
            """,
            [
                new SqlParameter("@Fecha", SqlDbType.Date) { Value = nuevaFecha.ToDateTime(TimeOnly.MinValue) },
                new SqlParameter("@TgId", SqlDbType.BigInt) { Value = tgUserId },
                new SqlParameter("@Nombre", SqlDbType.NVarChar, 200) { Value = nombreCompleto },
                new SqlParameter("@Accion", SqlDbType.VarChar, 20) { Value = accionTexto },
                new SqlParameter("@Id", SqlDbType.Int) { Value = hitoId }
            ],
            ct);

    private static HitoParaReprogramar MapearReprogramar(SqlDataReader reader)
    {
        var ordReprogramar = reader.GetOrdinal("reprogramar");
        var ordMsgId = reader.GetOrdinal("msg_id");

        return new HitoParaReprogramar(
            Id: reader.GetInt32(reader.GetOrdinal("id")),
            HitoTexto: reader.GetString(reader.GetOrdinal("hito")),
            Estado: reader.GetString(reader.GetOrdinal("estado")),
            Reprogramar: reader.IsDBNull(ordReprogramar) ? null : reader.GetDateTime(ordReprogramar),
            MsgId: reader.IsDBNull(ordMsgId) ? null : reader.GetString(ordMsgId),
            TggChatId: reader.GetString(reader.GetOrdinal("Tgg_Chat_Id")));
    }

    private static HitoParaReset MapearReset(SqlDataReader reader) => new(
        Id: reader.GetInt32(reader.GetOrdinal("id")),
        DiaMensual: reader.GetInt32(reader.GetOrdinal("dia_mensual")),
        Estado: reader.GetString(reader.GetOrdinal("estado")));

    private static HitoParaActualizar MapearActualizar(SqlDataReader reader) => new(
        Id: reader.GetInt32(reader.GetOrdinal("id")),
        HitoTexto: reader.GetString(reader.GetOrdinal("hito")),
        Estado: reader.GetString(reader.GetOrdinal("estado")),
        MsgId: reader.GetString(reader.GetOrdinal("msg_id")),
        TggChatId: reader.GetString(reader.GetOrdinal("Tgg_Chat_Id")));

    private static Hito Mapear(SqlDataReader reader)
    {
        var ordEstado = reader.GetOrdinal("estado");
        var ordReprogramar = reader.GetOrdinal("reprogramar");
        var ordMsgId = reader.GetOrdinal("msg_id");
        var ordEnviaFinSemana = reader.GetOrdinal("Envia_Fin_Semana");

        return new Hito(
            Id: reader.GetInt32(reader.GetOrdinal("id")),
            DiaMensual: reader.GetInt32(reader.GetOrdinal("dia_mensual")),
            HitoTexto: reader.GetString(reader.GetOrdinal("hito")),
            Estado: reader.IsDBNull(ordEstado) ? string.Empty : reader.GetString(ordEstado),
            Reprogramar: reader.IsDBNull(ordReprogramar) ? null : reader.GetDateTime(ordReprogramar),
            MsgId: reader.IsDBNull(ordMsgId) ? null : reader.GetString(ordMsgId),
            TggChatId: reader.GetString(reader.GetOrdinal("Tgg_Chat_Id")),
            EnviaFinDeSemana: !reader.IsDBNull(ordEnviaFinSemana) && reader.GetBoolean(ordEnviaFinSemana));
    }
}
