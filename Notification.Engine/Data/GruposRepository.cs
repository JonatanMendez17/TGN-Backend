using System.Data;
using Microsoft.Data.SqlClient;

namespace Notification.Engine.Data;

public class GruposRepository(ISqlDataAccess db) : IGruposRepository
{
    private readonly ISqlDataAccess _db = db;

    public async Task<bool> ExisteGrupoAsync(string chatId, CancellationToken ct = default)
    {
        var count = await _db.ObtenerValorAsync<int>(
            "SELECT COUNT(*) FROM dbo.Tg_Grupo WHERE Tgg_Chat_Id = @ChatId",
            [new SqlParameter("@ChatId", SqlDbType.VarChar, 50) { Value = chatId }],
            ct);

        return count > 0;
    }

    public Task RegistrarGrupoAsync(string nombre, string chatId, long? ownerTgId, string? ownerNombre, string? ownerUsername, CancellationToken ct = default) =>
        _db.EjecutarAsync(
            """
            INSERT INTO dbo.Tg_Grupo (Tgg_Nombre, Tgg_Chat_Id, Tgg_Estado, Tgg_Fecha_Creado, Tgg_Owner_Tg_Id, Tgg_Owner_Nombre, Tgg_Owner_Username)
            VALUES (@Nombre, @ChatId, 1, GETDATE(), @OwnerTgId, @OwnerNombre, @OwnerUsername)
            """,
            [
                new SqlParameter("@Nombre", SqlDbType.VarChar, 50) { Value = nombre },
                new SqlParameter("@ChatId", SqlDbType.VarChar, 50) { Value = chatId },
                new SqlParameter("@OwnerTgId", SqlDbType.BigInt) { Value = (object?)ownerTgId ?? DBNull.Value },
                new SqlParameter("@OwnerNombre", SqlDbType.NVarChar, 200) { Value = (object?)ownerNombre ?? DBNull.Value },
                new SqlParameter("@OwnerUsername", SqlDbType.NVarChar, 100) { Value = (object?)ownerUsername ?? DBNull.Value }
            ],
            ct);

    public Task<int?> ObtenerTggIdPorChatIdAsync(string chatId, CancellationToken ct = default) =>
        _db.ObtenerValorAsync<int?>(
            "SELECT Tgg_Id FROM dbo.Tg_Grupo WHERE Tgg_Chat_Id = @ChatId",
            [new SqlParameter("@ChatId", SqlDbType.VarChar, 50) { Value = chatId }],
            ct);

    public Task ActualizarChatIdAsync(string chatIdViejo, string chatIdNuevo, CancellationToken ct = default) =>
        _db.EjecutarAsync(
            "UPDATE dbo.Tg_Grupo SET Tgg_Chat_Id = @Nuevo WHERE Tgg_Chat_Id = @Viejo",
            [
                new SqlParameter("@Nuevo", SqlDbType.VarChar, 50) { Value = chatIdNuevo },
                new SqlParameter("@Viejo", SqlDbType.VarChar, 50) { Value = chatIdViejo }
            ],
            ct);

    public async Task<int> GuardarReceptorAsync(long tgUserId, string nombre, string apellido, CancellationToken ct = default)
    {
        var existenteId = await _db.ObtenerValorAsync<int?>(
            "SELECT Tre_Id FROM dbo.Tg_Receptor WHERE Tre_Tg_Id = @TgId",
            [new SqlParameter("@TgId", SqlDbType.BigInt) { Value = tgUserId }],
            ct);

        if (existenteId is { } treId)
        {
            await _db.EjecutarAsync(
                "UPDATE dbo.Tg_Receptor SET Tre_Nombre = @Nombre, Tre_Apellido = @Apellido WHERE Tre_Id = @Id",
                [
                    new SqlParameter("@Nombre", SqlDbType.VarChar, 50) { Value = nombre },
                    new SqlParameter("@Apellido", SqlDbType.VarChar, 200) { Value = apellido },
                    new SqlParameter("@Id", SqlDbType.Int) { Value = treId }
                ],
                ct);

            return treId;
        }

        var nuevoId = await _db.ObtenerValorAsync<int>(
            """
            INSERT INTO dbo.Tg_Receptor (Tre_Nombre, Tre_Apellido, Tre_Tg_Id, Tre_Vigente)
            OUTPUT INSERTED.Tre_Id
            VALUES (@Nombre, @Apellido, @TgId, 1)
            """,
            [
                new SqlParameter("@Nombre", SqlDbType.VarChar, 50) { Value = nombre },
                new SqlParameter("@Apellido", SqlDbType.VarChar, 200) { Value = apellido },
                new SqlParameter("@TgId", SqlDbType.BigInt) { Value = tgUserId }
            ],
            ct);

        return nuevoId;
    }

    public async Task AsegurarGrupoReceptorAsync(int tggId, int treId, CancellationToken ct = default)
    {
        var yaExiste = await _db.ObtenerValorAsync<int?>(
            "SELECT 1 FROM dbo.Tg_Grupo_Receptor WHERE Tgg_Id = @TggId AND Tre_Id = @TreId",
            [
                new SqlParameter("@TggId", SqlDbType.Int) { Value = tggId },
                new SqlParameter("@TreId", SqlDbType.Int) { Value = treId }
            ],
            ct);

        if (yaExiste is null)
        {
            await _db.EjecutarAsync(
                "INSERT INTO dbo.Tg_Grupo_Receptor (Tgg_Id, Tre_Id) VALUES (@TggId, @TreId)",
                [
                    new SqlParameter("@TggId", SqlDbType.Int) { Value = tggId },
                    new SqlParameter("@TreId", SqlDbType.Int) { Value = treId }
                ],
                ct);
        }
    }
}
