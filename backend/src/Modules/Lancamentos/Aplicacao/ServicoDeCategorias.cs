using GestorFinanceiro.Modules.Lancamentos.Contracts;
using GestorFinanceiro.Modules.Lancamentos.Dominio;
using GestorFinanceiro.Modules.Lancamentos.Infra;
using GestorFinanceiro.SharedKernel.Aplicacao;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GestorFinanceiro.Modules.Lancamentos.Aplicacao;

internal abstract record ResultadoDeCategoria
{
    public sealed record Ok(Guid Id) : ResultadoDeCategoria;
    public sealed record Invalido(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDeCategoria;
    public sealed record NaoEncontrada : ResultadoDeCategoria;
    public sealed record NomeRepetido : ResultadoDeCategoria;
    public sealed record EmUso : ResultadoDeCategoria;
}

/// <summary>Categorias do casal. O casal vem sempre da sessão; as padrão nascem na primeira consulta.</summary>
internal sealed class ServicoDeCategorias(LancamentosDbContext db, IUsuarioAtual atual)
{
    internal const string TituloNomeRepetido = "Já existe uma categoria com esse nome.";
    internal const string TituloEmUso = "Categoria com lançamentos: arquive em vez de apagar.";
    private const string IndiceDeNome = "ux_categorias_nome";

    private static readonly string[] DespesasPadrao =
    [
        "Mercado", "Restaurantes", "Transporte", "Casa e contas", "Saúde", "Lazer", "Educação",
        "Investimento", "Dívidas e financiamentos", "Outros",
    ];

    private static readonly string[] ReceitasPadrao = ["Salário", "Outras receitas"];

    public async Task<IReadOnlyList<CategoriaDto>?> ListarAsync(CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return null;
        await GarantirPadraoAsync(casalId, ct);
        var categorias = await db.Categorias.AsNoTracking().OrderBy(c => c.Tipo).ThenBy(c => c.Nome).ToListAsync(ct);
        return categorias.Select(ParaDto).ToList();
    }

    public async Task<ResultadoDeCategoria> CriarAsync(NovaCategoriaDto pedido, CancellationToken ct)
    {
        if (atual.CasalId is not { } casalId) return new ResultadoDeCategoria.NaoEncontrada();
        var erros = Validar(pedido);
        if (erros.Count > 0) return new ResultadoDeCategoria.Invalido(erros);

        var categoria = Categoria.Criar(casalId, pedido.Nome.Trim(), LerTipo(pedido.Tipo));
        db.Categorias.Add(categoria);
        return await SalvarAsync(categoria.Id, ct);
    }

    public async Task<ResultadoDeCategoria> AtualizarAsync(Guid id, NovaCategoriaDto pedido, CancellationToken ct)
    {
        var categoria = await db.Categorias.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (categoria is null) return new ResultadoDeCategoria.NaoEncontrada();
        var erros = Validar(pedido);
        if (erros.Count > 0) return new ResultadoDeCategoria.Invalido(erros);

        var tipo = LerTipo(pedido.Tipo);
        if (tipo != categoria.Tipo && await TemLancamentosAsync(id, ct))
            return new ResultadoDeCategoria.Invalido(new Dictionary<string, string[]> { ["tipo"] = ["Categoria com lançamentos não muda de tipo."] });

        categoria.Definir(pedido.Nome.Trim(), tipo, pedido.Arquivada ?? categoria.Arquivada);
        return await SalvarAsync(id, ct);
    }

    public async Task<ResultadoDeCategoria> ApagarAsync(Guid id, CancellationToken ct)
    {
        var categoria = await db.Categorias.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (categoria is null) return new ResultadoDeCategoria.NaoEncontrada();
        if (await TemLancamentosAsync(id, ct)) return new ResultadoDeCategoria.EmUso();

        db.Categorias.Remove(categoria); // SoftDeleteInterceptor converte em is_deleted = true.
        await db.SaveChangesAsync(ct);
        return new ResultadoDeCategoria.Ok(id);
    }

    private Task<bool> TemLancamentosAsync(Guid categoriaId, CancellationToken ct) =>
        db.Transacoes.AnyAsync(t => t.CategoriaId == categoriaId, ct);

    /// <summary>Cria as categorias padrão se o casal não tem nenhuma; o lock por casal evita duplicar em chamadas simultâneas.</summary>
    private async Task GarantirPadraoAsync(Guid casalId, CancellationToken ct)
    {
        if (await db.Categorias.AnyAsync(ct)) return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"categorias-padrao|" + casalId:N}, 0))", ct);
        // Depois do lock: outra chamada pode ter criado as padrão enquanto esperávamos.
        if (!await db.Categorias.AnyAsync(ct))
        {
            foreach (var nome in DespesasPadrao) db.Categorias.Add(Categoria.Criar(casalId, nome, TipoDeCategoria.Despesa));
            foreach (var nome in ReceitasPadrao) db.Categorias.Add(Categoria.Criar(casalId, nome, TipoDeCategoria.Receita));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task<ResultadoDeCategoria> SalvarAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return new ResultadoDeCategoria.Ok(id);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IndiceDeNome })
        {
            return new ResultadoDeCategoria.NomeRepetido();
        }
    }

    private static Dictionary<string, string[]> Validar(NovaCategoriaDto p)
    {
        var erros = new Dictionary<string, string[]>();
        if ((p.Nome?.Trim().Length ?? 0) is < 1 or > 40) erros["nome"] = ["O nome deve ter de 1 a 40 caracteres."];
        if (p.Tipo is not ("DESPESA" or "RECEITA")) erros["tipo"] = ["Tipo inválido: use DESPESA ou RECEITA."];
        return erros;
    }

    private static TipoDeCategoria LerTipo(string tipo) => tipo == "RECEITA" ? TipoDeCategoria.Receita : TipoDeCategoria.Despesa;

    private static CategoriaDto ParaDto(Categoria c) => new(c.Id, c.Nome, c.Tipo.ToString().ToUpperInvariant(), c.Arquivada);
}
