using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace GestorFinanceiro.Modules.Integracoes.Pluggy;

internal sealed class OpcoesDaPluggy
{
    /// <summary>Base da API; os testes apontam para uma Pluggy falsa.</summary>
    public string Url { get; set; } = "https://api.pluggy.ai";

    /// <summary>Onde estão os segredos <c>pluggy_&lt;slot&gt;_client_id</c> e <c>_client_secret</c> (no compose, /run/secrets).</summary>
    public string PastaDeSegredos { get; set; } = "/run/secrets";
}

/// <summary>
/// Lê as chaves de um slot (D23): <c>pluggy_1_*</c> ou <c>pluggy_2_*</c>. Arquivo ausente ou vazio = slot não configurado.
/// O conteúdo nunca vai para log, exceção ou resposta.
/// </summary>
internal sealed class SegredosDaPluggy(IOptions<OpcoesDaPluggy> opcoes)
{
    public CredenciaisPluggy? Ler(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 2);

        var id = LerArquivo($"pluggy_{slot}_client_id");
        var segredo = LerArquivo($"pluggy_{slot}_client_secret");
        return id is null || segredo is null ? null : new CredenciaisPluggy(slot, id, segredo);
    }

    private string? LerArquivo(string nome)
    {
        var caminho = Path.Combine(opcoes.Value.PastaDeSegredos, nome);
        if (!File.Exists(caminho)) return null;
        var texto = File.ReadAllText(caminho).Trim();
        return texto.Length == 0 ? null : texto;
    }
}

/// <summary>
/// Chave temporária da Pluggy (vale 2 h), por slot, só em memória. Guardada por 1 h 50 min. Sem propriedades públicas.
/// </summary>
internal sealed class CacheDeChavesDaPluggy
{
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(110);
    private readonly ConcurrentDictionary<int, (string Chave, DateTimeOffset Expira)> _chaves = new();

    public string? Obter(int slot, DateTimeOffset agora) =>
        _chaves.TryGetValue(slot, out var c) && c.Expira > agora ? c.Chave : null;

    public void Guardar(int slot, string chave, DateTimeOffset agora) => _chaves[slot] = (chave, agora + Validade);

    public void Esquecer(int slot) => _chaves.TryRemove(slot, out _);
}
