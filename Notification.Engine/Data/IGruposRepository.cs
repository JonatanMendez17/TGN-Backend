namespace Notification.Engine.Data;

public interface IGruposRepository
{
    Task<bool> ExisteGrupoAsync(string chatId, CancellationToken ct = default);

    Task RegistrarGrupoAsync(string nombre, string chatId, long? ownerTgId, string? ownerNombre, string? ownerUsername, CancellationToken ct = default);

    Task<int?> ObtenerTggIdPorChatIdAsync(string chatId, CancellationToken ct = default);

    Task ActualizarChatIdAsync(string chatIdViejo, string chatIdNuevo, CancellationToken ct = default);

    Task<int> GuardarReceptorAsync(long tgUserId, string nombre, string apellido, CancellationToken ct = default);

    Task AsegurarGrupoReceptorAsync(int tggId, int treId, CancellationToken ct = default);
}
