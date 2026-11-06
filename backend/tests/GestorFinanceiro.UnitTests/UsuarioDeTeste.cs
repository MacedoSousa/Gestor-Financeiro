using GestorFinanceiro.Modules.Identidade.Dominio;
using GestorFinanceiro.SharedKernel.Seguranca;

namespace GestorFinanceiro.UnitTests;

internal static class UsuarioDeTeste
{
    public static Usuario Criar()
    {
        Assert.True(Cpf.TentarCriar(CpfSintetico.Gerar(1), out var cpf));
        return Usuario.Criar(Guid.CreateVersion7(), "Ana Teste", cpf, new CofreAesGcm(EnvelopeDeChaves.GerarDek()));
    }
}
