using GestorFinanceiro.Modules.Identidade.Contracts;
using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.EntityFrameworkCore;

namespace GestorFinanceiro.Modules.Identidade.Infra;

internal sealed class ConsultaDeUsuarios(IdentidadeDbContext db, ICofre cofre) : IConsultaDeUsuarios
{
    public async Task<UsuarioResumo?> ObterPorTelefoneAsync(string telefoneE164, CancellationToken ct = default)
    {
        var hmac = cofre.IndiceCego(telefoneE164, Usuario.PropositoTelefone);

        // A mensagem chega antes de sabermos o casal: só o filtro de soft delete continua ativo.
        var usuario = await db.Usuarios
            .IgnoreQueryFilters([Filtros.Casal])
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.TelefoneHmac == hmac, ct);

        return usuario is null ? null : ParaResumo(usuario);
    }

    public async Task<IReadOnlyList<UsuarioResumo>> ListarDoCasalAsync(Guid casalId, CancellationToken ct = default) =>
        (await db.Usuarios.AsNoTracking().Where(u => u.CasalId == casalId).ToListAsync(ct))
            .Select(ParaResumo)
            .ToList();

    private static UsuarioResumo ParaResumo(Usuario u) =>
        new(u.Id, u.CasalId, u.NomeCompleto.Split(' ', 2)[0], u.ConjugeId);
}
