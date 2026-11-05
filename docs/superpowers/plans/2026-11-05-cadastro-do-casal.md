# Cadastro do casal — plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: use superpowers:subagent-driven-development (recomendado) ou superpowers:executing-plans para executar este plano tarefa a tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** numa instalação nova, os dois cônjuges preenchem juntos uma única tela e o sistema cria o casal, os dois usuários com CPF cifrado e vínculo de cônjuge, e três envelopes da DEK em `identidade.chaves`: a senha de cada um e um código de recuperação, mostrado uma única vez.

**Arquitetura:** o módulo Identidade ganha `Aplicacao/CadastroDoCasal` e dois endpoints em `/api/v1/casal`. A DEK continua no Host, e o módulo só pede envelopes por uma interface do SharedKernel (`IEnvelopesDaDek`), sem nunca ver a DEK. Como ainda não há login, o cadastro só funciona enquanto não existir casal e exige um código de instalação gerado por comando no servidor. O front ganha a rota `/cadastro`, fora da casca com barra lateral.

**Tecnologias:** .NET 10, EF Core 10 + Npgsql, Konscious Argon2id, xUnit 2.9, Testcontainers.PostgreSql 4.16.0, Microsoft.AspNetCore.Mvc.Testing 10.0.0, React 19 + TanStack Query + openapi-fetch.

**Especificação:** `docs/SPEC.md` (módulo Identidade, tarefa "Cadastro do casal"), `docs/adr/0005-envelope-encryption.md`, `docs/design/README.md` + `docs/design/telas/Main.dc.html` (base visual), `CLAUDE.md`, `.claude/rules/backend.md`, `.claude/rules/frontend.md`.

## Decisões já tomadas

1. **Formulário único** (escolha do usuário): uma requisição cria tudo, numa transação.
2. **Um único código de recuperação, gerado no cadastro** (escolha do usuário): o envelope `RECUPERACAO` vai para `identidade.chaves`, e o `--inicializar-chaves` deixa de gerar o `dek.recuperacao`. Arquivos `dek.recuperacao` já existentes não são apagados; só deixam de ser lidos e configurados.
3. **Código de instalação (aprovado):** o `POST` de cadastro não tem sessão. O comando `--codigo-instalacao` gera um código aleatório (mesmo formato do de recuperação), grava num arquivo 0400 só o SHA-256 e a validade, e imprime o código. Regras:
   - **Validade:** 24 h a partir da geração.
   - **Uso único:** o código é consumido depois do commit do cadastro.
   - **Código expirado ou errado:** a API responde `403`, com uma única mensagem para os dois casos.
   - **Hash:** SHA-256 serve aqui porque o código tem 160 bits aleatórios; a regra "nunca SHA-256 puro" do CLAUDE.md é sobre CPF.
4. **Depois que existir um casal, `POST /api/v1/casal` responde `404`** (aprovado), antes de olhar o código ou os dados. Para quem está de fora, a rota deixa de existir. Envios simultâneos que perdem a corrida também recebem `404`.
5. **Rate limit no Caddy** (aprovado): zona própria `cadastro` para `POST /api/v1/casal`, com 5 tentativas por 10 minutos por IP.
6. **A porta 443 não é aberta antes do login existir** (decisão do usuário). Fica registrado no ADR-006. O cadastro é feito pela rede de casa.
7. **A senha não é de login.** Login (passkey/TOTP, ADR-011) é a próxima tarefa do SPEC. Aqui a senha só embrulha a DEK. Depois do cadastro o casal ainda não entra no sistema.

## Restrições globais

- Nada de DELETE físico; auditoria só pelos triggers; dinheiro não aparece aqui.
- CPF em `cpf_cifrado` via `ICofre.Cifrar` com AAD `identidade.usuarios|cpf_cifrado|{id:N}` (já feito em `Usuario.Criar`); unicidade por `cpf_hmac` com propósito `"cpf"`.
- Nenhum CPF, nome, senha ou código em log, exceção ou corpo de erro. As respostas de erro trazem só a mensagem fixa do campo.
- Argon2id com `ParametrosArgon2()` padrão: 65 536 KiB, t=3, p=2 (ADR-005). `versao = 1` em `identidade.chaves` significa esses parâmetros.
- Tipos `public` só em `GestorFinanceiro.Modules.Identidade.Contracts` e em `IdentidadeModule`; o resto é `internal`.
- Erros em Problem Details; validação em `ValidationProblem` com chaves camelCase (`conjuge1.cpf`).
- Mudou contrato → `dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api` no mesmo commit.
- CPFs de teste: sintéticos, com dígitos verificadores válidos (`529.982.247-25`, `123.456.789-09`, ou gerados por `CpfSintetico.Gerar`).
- Commits curtos em pt-BR terminando com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Trabalhar na branch `fase-1/cadastro-casal`, criada a partir da `main`.
- Antes de cada commit: `dotnet test backend/GestorFinanceiro.slnx` verde e 0 avisos no build.

## Foco da revisão

1. **Dois envios simultâneos** (duplo clique, duas abas) → exatamente um `201` e um `404`; nunca dois casais. Teste: `Dois_cadastros_simultaneos_criam_um_so_casal` (Tarefa 5).
2. **O mesmo CPF digitado com e sem máscara para os dois cônjuges** (`529.982.247-25` e `52998224725`) → `400` em `conjuge2.cpf`. Teste: `Rejeita_mesmo_cpf_com_mascaras_diferentes` (Tarefa 3).
3. **Senha com acento digitada em teclados diferentes** (NFC vs. NFD) → a mesma senha desembrulha a DEK. Teste: `Senha_em_nfd_desembrulha_envelope_feito_em_nfc` (Tarefa 1).
4. **Erro de validação ou de duplicidade não ecoa o CPF nem a senha** → o corpo da resposta não contém os valores enviados. Teste: `Erro_de_validacao_nao_ecoa_cpf_nem_senha` (Tarefa 5).
5. **Recarregar ou fechar a aba na tela do código de recuperação** → o navegador avisa antes de sair, e "Concluir" só é liberado depois de confirmar que o código foi guardado. Verificação manual no navegador (Tarefa 6, passo 6), porque o front não tem runner de testes.

---

### Tarefa 1: Envelopes da DEK sem expor a DEK ao módulo

**Arquivos:**
- Criar: `backend/src/SharedKernel/Seguranca/EnvelopesDaDek.cs`
- Modificar: `backend/src/SharedKernel/Seguranca/EnvelopeDeChaves.cs` (`DerivarKek` normaliza o segredo para NFC)
- Modificar: `backend/src/Host/GestorFinanceiro.Api/Infra/CofreDoServidor.cs`, `backend/src/Host/GestorFinanceiro.Api/Program.cs:46-47`
- Teste: `backend/tests/GestorFinanceiro.UnitTests/SharedKernelTests.cs` (nova classe `EnvelopesDaDekTests`)

**Interfaces:**
- Produz:
  - `public interface IEnvelopesDaDek { EnvelopeDaDek Embrulhar(string segredo); }`
  - `public sealed record EnvelopeDaDek(byte[] DekCifrada, byte[] Salt);`
  - `public sealed class EnvelopesDaDek(Func<byte[]> lerDek, ParametrosArgon2 parametros) : IEnvelopesDaDek`. Chama `lerDek()` a cada uso, gera um salt novo, deriva a KEK, embrulha e zera a DEK e a KEK com `CryptographicOperations.ZeroMemory` no `finally`.
  - `internal static byte[] CofreDoServidor.LerDek(IConfiguration, IHostEnvironment)`: desembrulha `dek.servico` com a chave de serviço e devolve uma cópia que quem chama deve zerar. `Carregar` passa a usá-lo.
  - `internal static string CofreDoServidor.CaminhoObrigatorio(IConfiguration, string chave, string raiz)`: o atual `Exigir`, renomeado e tornado `internal`.

- [ ] **Passo 1: Escrever os testes que falham**

```csharp
public class EnvelopesDaDekTests
{
    private static readonly ParametrosArgon2 Barato = new(MemoriaKiB: 1024, Iteracoes: 1, Paralelismo: 1);

    [Fact]
    public void Envelope_desembrulha_a_mesma_dek_com_o_mesmo_segredo()
    {
        var dek = EnvelopeDeChaves.GerarDek();
        var envelope = new EnvelopesDaDek(() => dek.ToArray(), Barato).Embrulhar("senha longa de teste");

        var kek = EnvelopeDeChaves.DerivarKek("senha longa de teste", envelope.Salt, Barato);
        Assert.Equal(dek, EnvelopeDeChaves.Desembrulhar(envelope.DekCifrada, kek));
    }

    [Fact]
    public void Segredo_errado_nao_desembrulha()
    {
        var envelope = new EnvelopesDaDek(EnvelopeDeChaves.GerarDek, Barato).Embrulhar("senha longa de teste");
        var kek = EnvelopeDeChaves.DerivarKek("outra senha qualquer", envelope.Salt, Barato);
        Assert.ThrowsAny<CryptographicException>(() => EnvelopeDeChaves.Desembrulhar(envelope.DekCifrada, kek));
    }

    [Fact]
    public void Cada_envelope_tem_salt_proprio()
    {
        var envelopes = new EnvelopesDaDek(EnvelopeDeChaves.GerarDek, Barato);
        Assert.NotEqual(envelopes.Embrulhar("a mesma senha!!").Salt, envelopes.Embrulhar("a mesma senha!!").Salt);
    }

    [Fact]
    public void Senha_em_nfd_desembrulha_envelope_feito_em_nfc()
    {
        var dek = EnvelopeDeChaves.GerarDek();
        var nfc = "coração de açúcar".Normalize(NormalizationForm.FormC);
        var envelope = new EnvelopesDaDek(() => dek.ToArray(), Barato).Embrulhar(nfc);

        var kek = EnvelopeDeChaves.DerivarKek(nfc.Normalize(NormalizationForm.FormD), envelope.Salt, Barato);
        Assert.Equal(dek, EnvelopeDeChaves.Desembrulhar(envelope.DekCifrada, kek));
    }
}
```

- [ ] **Passo 2: Rodar e ver falhar**

Rodar: `dotnet test backend/tests/GestorFinanceiro.UnitTests --filter EnvelopesDaDekTests`
Esperado: erro de compilação, porque `EnvelopesDaDek` ainda não existe.

- [ ] **Passo 3: Implementar**

Criar `EnvelopesDaDek.cs` com a interface, o record e a classe acima. Em `EnvelopeDeChaves.DerivarKek`, trocar `Encoding.UTF8.GetBytes(segredo)` por `Encoding.UTF8.GetBytes(segredo.Normalize(NormalizationForm.FormC))`. O código de recuperação é ASCII, então envelopes já existentes não mudam.

No Host:
- **Modo `GF_DEV_CHAVE_EFEMERA=1`:** gerar a DEK uma vez no startup e registrar `ICofre` como `new CofreAesGcm(dek)` e `IEnvelopesDaDek` como `new EnvelopesDaDek(() => dek.ToArray(), new ParametrosArgon2())`, com a mesma DEK.
- **Fora dele:** `ICofre` continua vindo de `Carregar`, e `IEnvelopesDaDek` usa `() => CofreDoServidor.LerDek(config, ambiente)`.

Nos dois casos, a DEK só é lida quando alguém resolve o serviço, nunca no build do OpenAPI.

- [ ] **Passo 4: Rodar e ver passar**

Rodar: `dotnet test backend/GestorFinanceiro.slnx`
Esperado: tudo verde, incluindo os testes atuais de `CofreTests` e `EnvelopeDeChavesTests`.

- [ ] **Passo 5: Commit** — `Envelopes da DEK por interface, senha normalizada em NFC`

---

### Tarefa 2: Código de instalação e fim do código de recuperação em arquivo

**Arquivos:**
- Criar: `backend/src/SharedKernel/Seguranca/CodigoDeInstalacao.cs`
- Modificar: `backend/src/Host/GestorFinanceiro.Api/Infra/CofreDoServidor.cs` (`InicializarAsync` sem recuperação), `backend/src/Host/GestorFinanceiro.Api/Program.cs` (flag `--codigo-instalacao` e registro do serviço), `backend/src/Host/GestorFinanceiro.Api/appsettings.json` e `appsettings.Development.json` (remover `ArquivoDekRecuperacao`, adicionar `ArquivoCodigoInstalacao`)
- Modificar: `docs/adr/0005-envelope-encryption.md` (código de recuperação gerado no cadastro e guardado em `identidade.chaves`; código de instalação de uso único, válido por 24 h, protege o cadastro)
- Modificar: `docs/adr/0006-duckdns.md`: em "Camadas de proteção", acrescentar `- A 443 só é encaminhada no roteador depois que o login (passkey/TOTP) existir; até lá o cadastro é feito pela rede de casa.` e o limite do cadastro (5 por 10 min por IP em `POST /api/v1/casal`)
- Modificar: `infra/caddy/Caddyfile`: zona `cadastro` (ver abaixo)
- Teste: `backend/tests/GestorFinanceiro.UnitTests/SharedKernelTests.cs` (nova classe `CodigoDeInstalacaoTests`)

**Interfaces:**
- Produz:
  - `public interface ICodigoDeInstalacao { bool Confere(string? codigo); void Consumir(); }`
  - `public sealed class CodigoDeInstalacaoEmArquivo(string caminho, TimeProvider relogio) : ICodigoDeInstalacao`.
    - **Formato do arquivo:** 40 bytes, `[validade: Unix segundos, int64 big-endian][SHA-256 do código normalizado: 32 bytes]`.
    - **`Confere`:** normaliza o código (maiúsculas, sem `-` nem espaços), calcula o SHA-256 e compara com `CryptographicOperations.FixedTimeEquals`. Também exige `relogio.GetUtcNow() < validade`. Devolve `false`, sem lançar exceção, se o arquivo não existir, tiver tamanho diferente de 40 ou o código for nulo ou vazio.
    - **`Consumir`:** regrava o arquivo com validade `0` e o mesmo hash. A partir daí nada confere, e o arquivo continua lá como registro. Não apaga o arquivo.
  - `public static readonly TimeSpan CodigoDeInstalacaoEmArquivo.Validade = TimeSpan.FromHours(24);`
  - `public static string CodigoDeInstalacaoEmArquivo.Gerar(string caminho, TimeProvider relogio)`:
    - usa `EnvelopeDeChaves.GerarCodigoRecuperacao()` e grava os 40 bytes com validade `agora + Validade`;
    - aplica `chmod 0400` (fora do Windows); se o arquivo já existir, passa o modo para `0600` antes de sobrescrever (o `Consumir` faz o mesmo);
    - devolve o código em claro.
- Teste: `RelogioFixo : TimeProvider` (em `backend/tests/GestorFinanceiro.UnitTests/RelogioFixo.cs`), com `DateTimeOffset Agora { get; set; }` e `GetUtcNow() => Agora`. Sem pacote novo.
- Caddyfile, antes do `rate_limit /api/*`:

```caddy
	# Cadastro do casal (sem sessão): no máximo 5 tentativas a cada 10 minutos por IP.
	@cadastro {
		method POST
		path /api/v1/casal
	}
	rate_limit @cadastro {
		zone cadastro {
			key {remote_host}
			events 5
			window 10m
		}
	}
```
- Configuração: `Seguranca:ArquivoCodigoInstalacao`. Em dev: `../../../../infra/secrets/codigo.instalacao`. Em produção: `/var/lib/gestor/chaves/codigo.instalacao`.

- [ ] **Passo 1: Escrever os testes que falham**

```csharp
public class CodigoDeInstalacaoTests
{
    private static string ArquivoTemporario() => Path.Combine(Path.GetTempPath(), $"gf-instalacao-{Guid.NewGuid():N}");
    private readonly RelogioFixo relogio = new() { Agora = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero) };

    [Fact]
    public void Codigo_vale_ate_24h_e_depois_expira()
    {
        var caminho = ArquivoTemporario();
        var codigo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        relogio.Agora += TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1);
        Assert.True(verificador.Confere(codigo));
        relogio.Agora += TimeSpan.FromSeconds(1);
        Assert.False(verificador.Confere(codigo));
    }

    [Fact]
    public void Codigo_consumido_nao_confere_mais()
    {
        var caminho = ArquivoTemporario();
        var codigo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        verificador.Consumir();

        Assert.False(verificador.Confere(codigo));
        Assert.True(File.Exists(caminho));
    }

    [Fact]
    public void Codigo_gerado_confere_com_e_sem_hifens_e_em_minusculas()
    {
        var caminho = ArquivoTemporario();
        var codigo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        Assert.True(verificador.Confere(codigo));
        Assert.True(verificador.Confere(codigo.Replace("-", "").ToLowerInvariant()));
        Assert.True(verificador.Confere($"  {codigo}  "));
    }

    [Fact]
    public void Codigo_diferente_ou_vazio_nao_confere()
    {
        var caminho = ArquivoTemporario();
        CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        Assert.False(verificador.Confere(EnvelopeDeChaves.GerarCodigoRecuperacao()));
        Assert.False(verificador.Confere(""));
        Assert.False(verificador.Confere(null));
    }

    [Fact]
    public void Sem_arquivo_nada_confere() =>
        Assert.False(new CodigoDeInstalacaoEmArquivo(ArquivoTemporario(), new RelogioFixo()).Confere("AAAA-BBBB"));

    [Fact]
    public void Gerar_de_novo_invalida_o_codigo_anterior()
    {
        var caminho = ArquivoTemporario();
        var antigo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var novo = CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        var verificador = new CodigoDeInstalacaoEmArquivo(caminho, relogio);

        Assert.False(verificador.Confere(antigo));
        Assert.True(verificador.Confere(novo));
    }

    [Fact]
    public void Arquivo_fica_somente_leitura_para_o_dono()
    {
        if (OperatingSystem.IsWindows()) return;
        var caminho = ArquivoTemporario();
        CodigoDeInstalacaoEmArquivo.Gerar(caminho, relogio);
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(caminho));
    }
}
```

- [ ] **Passo 2: Rodar e ver falhar**

Rodar: `dotnet test backend/tests/GestorFinanceiro.UnitTests --filter CodigoDeInstalacaoTests`
Esperado: erro de compilação, porque `CodigoDeInstalacaoEmArquivo` ainda não existe.

- [ ] **Passo 3: Implementar**

Fazer a classe acima. Em `Program.cs`:
- Adicionar o bloco `if (args.Contains("--codigo-instalacao"))`. Ele resolve o caminho com `CofreDoServidor.CaminhoObrigatorio`, chama `Gerar(caminho, TimeProvider.System)`, imprime `"Código de instalação (uso único, vale 24 h):"` e o código, e encerra com `return`.
- Registrar `ICodigoDeInstalacao` como singleton com o mesmo caminho e o `TimeProvider` já registrado.
- Acrescentar a zona `cadastro` no Caddyfile e as notas nos ADRs 005 e 006.

Em `InicializarAsync`:
- Remover a geração e a gravação do código de recuperação e a chave `ArquivoDekRecuperacao`.
- Trocar a mensagem final por `"Chaves criadas. O código de recuperação é gerado no cadastro do casal."`.

- [ ] **Passo 4: Rodar e ver passar**

Rodar: `dotnet test backend/GestorFinanceiro.slnx`
Esperado: tudo verde e 0 avisos.

- [ ] **Passo 5: Validar o Caddyfile** — `docker run --rm -v "$PWD/infra/caddy/Caddyfile:/etc/caddy/Caddyfile:ro" <imagem do caddy em infra/docker-compose.yml> caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile`. Esperado: `Valid configuration`. Se a imagem exigir `DOMINIO`/`DUCKDNS_TOKEN`, passar valores falsos com `-e`.

- [ ] **Passo 6: Commit** — `Código de instalação de uso único e 24 h; rate limit do cadastro`

---

### Tarefa 3: Contrato e validação do cadastro

**Arquivos:**
- Criar: `backend/src/Modules/Identidade/Contracts/CadastroDoCasal.cs` (DTOs)
- Criar: `backend/src/Modules/Identidade/Aplicacao/ValidadorDeCadastro.cs`
- Teste: `backend/tests/GestorFinanceiro.UnitTests/CadastroTests.cs`

**Interfaces:**
- Produz (todos em `GestorFinanceiro.Modules.Identidade.Contracts`, `public`):
  - `sealed record CadastroDoCasalDto(string CodigoInstalacao, string NomeFamilia, ConjugeNovoDto Conjuge1, ConjugeNovoDto Conjuge2)`
  - `sealed record ConjugeNovoDto(string NomeCompleto, string Cpf, string Senha)`
  - `sealed record CasalCadastradoDto(Guid CasalId, string CodigoRecuperacao)`
  - `sealed record EstadoDoCadastroDto(bool Disponivel)`
  - Os DTOs que trazem segredo (`ConjugeNovoDto`, `CadastroDoCasalDto`, `CasalCadastradoDto`) sobrescrevem `ToString()` e devolvem só o nome do tipo, para nunca vazar em log.
- Produz (`internal`): `static IReadOnlyDictionary<string, string[]> ValidadorDeCadastro.Validar(CadastroDoCasalDto pedido)`. Um dicionário vazio significa válido.

Mensagens fixas (cópia exata) e chaves:

| Chave | Condição | Mensagem |
| --- | --- | --- |
| `nomeFamilia` | vazio/brancos ou > 100 após `Trim` | `Informe o nome da família (até 100 caracteres).` |
| `conjugeN.nomeCompleto` | vazio/brancos ou > 150 após `Trim` | `Informe o nome completo (até 150 caracteres).` |
| `conjugeN.cpf` | `Cpf.TentarCriar` falha | `CPF inválido.` |
| `conjuge2.cpf` | dígitos iguais aos do cônjuge 1 | `Os dois cônjuges não podem ter o mesmo CPF.` |
| `conjugeN.senha` | < 12 ou > 128 caracteres (contados em NFC) | `A senha precisa ter de 12 a 128 caracteres.` |

`N` é `1` ou `2`. O código de instalação não passa pelo validador, é conferido antes (Tarefa 5).

- [ ] **Passo 1: Escrever os testes que falham**

Em `CadastroTests.cs`, com o helper de teste `CpfSintetico.Gerar(int semente) -> string`. Ele gera 9 dígitos a partir da semente, calcula os dois dígitos verificadores e evita sequências repetidas.

```csharp
public class ValidadorDeCadastroTests
{
    private static CadastroDoCasalDto Valido() => new(
        "CODIGO", "Família Teste",
        new ConjugeNovoDto("Ana Teste", "529.982.247-25", "senha-da-ana-123"),
        new ConjugeNovoDto("Bruno Teste", "123.456.789-09", "senha-do-bruno-1"));

    [Fact]
    public void Pedido_valido_nao_tem_erros() => Assert.Empty(ValidadorDeCadastro.Validar(Valido()));

    [Fact]
    public void Rejeita_mesmo_cpf_com_mascaras_diferentes()
    {
        var pedido = Valido() with { Conjuge2 = Valido().Conjuge2 with { Cpf = "52998224725" } };
        Assert.Equal(["Os dois cônjuges não podem ter o mesmo CPF."], ValidadorDeCadastro.Validar(pedido)["conjuge2.cpf"]);
    }

    [Fact]
    public void Cpf_invalido_aponta_o_campo()
    {
        var pedido = Valido() with { Conjuge1 = Valido().Conjuge1 with { Cpf = "123.456.789-00" } };
        Assert.Equal(["CPF inválido."], ValidadorDeCadastro.Validar(pedido)["conjuge1.cpf"]);
    }

    [Theory]
    [InlineData("curta")]
    [InlineData("12345678901")]
    public void Senha_com_menos_de_12_caracteres_e_rejeitada(string senha)
    {
        var pedido = Valido() with { Conjuge2 = Valido().Conjuge2 with { Senha = senha } };
        Assert.True(ValidadorDeCadastro.Validar(pedido).ContainsKey("conjuge2.senha"));
    }

    [Fact]
    public void Nomes_em_branco_sao_rejeitados()
    {
        var pedido = Valido() with { NomeFamilia = "   ", Conjuge1 = Valido().Conjuge1 with { NomeCompleto = "" } };
        var erros = ValidadorDeCadastro.Validar(pedido);
        Assert.Contains("nomeFamilia", erros.Keys);
        Assert.Contains("conjuge1.nomeCompleto", erros.Keys);
    }

    [Fact]
    public void ToString_dos_dtos_nao_mostra_cpf_nem_senha()
    {
        var texto = Valido().ToString() + Valido().Conjuge1;
        Assert.DoesNotContain("529", texto);
        Assert.DoesNotContain("senha-da-ana", texto);
    }

    [Fact]
    public void CpfSintetico_gera_cpfs_validos_e_distintos()
    {
        var cpfs = Enumerable.Range(1, 50).Select(CpfSintetico.Gerar).ToList();
        Assert.All(cpfs, c => Assert.True(Cpf.TentarCriar(c, out _)));
        Assert.Equal(50, cpfs.Distinct().Count());
    }
}
```

- [ ] **Passo 2: Rodar e ver falhar**

Rodar: `dotnet test backend/tests/GestorFinanceiro.UnitTests --filter ValidadorDeCadastroTests`
Esperado: erro de compilação, porque os tipos ainda não existem.

- [ ] **Passo 3: Implementar** os DTOs, o `ValidadorDeCadastro` (com a tabela acima) e o `CpfSintetico` (em `backend/tests/GestorFinanceiro.UnitTests/CpfSintetico.cs`; a Tarefa 4 o vincula como arquivo compartilhado).

- [ ] **Passo 4: Rodar e ver passar**

Rodar: `dotnet test backend/GestorFinanceiro.slnx`
Esperado: tudo verde. O teste de arquitetura confirma que só `Contracts` ficou `public`.

- [ ] **Passo 5: Commit** — `Contrato e validação do cadastro do casal`

---

### Tarefa 4: Projeto de testes de integração e a cadeia de auditoria

**Arquivos:**
- Criar: `backend/tests/GestorFinanceiro.IntegrationTests/GestorFinanceiro.IntegrationTests.csproj` (referencia o projeto da API; `<Compile Include="..\GestorFinanceiro.UnitTests\CpfSintetico.cs" Link="CpfSintetico.cs" />`)
- Criar: `backend/tests/GestorFinanceiro.IntegrationTests/Infra/PostgresFixture.cs`, `Infra/ApiDeTeste.cs`
- Criar: `backend/tests/GestorFinanceiro.IntegrationTests/CadeiaDeAuditoriaTests.cs`
- Modificar: `backend/GestorFinanceiro.slnx` (novo projeto), `backend/Directory.Packages.props` (`Testcontainers.PostgreSql` 4.16.0, `Microsoft.AspNetCore.Mvc.Testing` 10.0.0), `backend/Directory.Build.props` (`<InternalsVisibleTo Include="GestorFinanceiro.IntegrationTests" />`, para acessar `Program` e `MigradorSql` sem torná-los públicos)
- Modificar: `CLAUDE.md`, seção Comandos: `- Integração (precisa de Docker): dotnet test backend/tests/GestorFinanceiro.IntegrationTests`

**Interfaces:**
- Produz:
  - `PostgresFixture : IAsyncLifetime`, compartilhada pela `[CollectionDefinition("postgres")]`. Sobe o `postgres:17-alpine` e cria os papéis como em `infra/postgres/init/00-papeis.sh` (`gf_migrator` e `gf_app`, senha `teste`). Cria o banco `gestor_modelo` com dono `gf_migrator` e aplica `MigradorSql.AplicarAsync` com a pasta `db/migrations`, achada subindo a partir de `AppContext.BaseDirectory`.
  - `Task<BancoDeTeste> PostgresFixture.NovoBancoAsync()`: `CREATE DATABASE gestor_<guid N> TEMPLATE gestor_modelo OWNER gf_migrator`. O cadastro só acontece uma vez por instalação, então cada teste usa um banco novo.
  - `sealed record BancoDeTeste(string ConexaoApp, string ConexaoSuperusuario, string ConexaoMigrador)`
  - `sealed class ApiDeTeste(BancoDeTeste banco, TimeSpan idadeDoCodigo = default) : WebApplicationFactory<Program>`. Usa ambiente `Development`, `ConnectionStrings:App = banco.ConexaoApp` e `Seguranca:ArquivoCodigoInstalacao` num arquivo temporário gerado com `CodigoDeInstalacaoEmArquivo.Gerar(caminho, relógio em agora - idadeDoCodigo)`. Expõe `string CodigoInstalacao` e `string CaminhoCodigoInstalacao`. O construtor da fixture define `GF_DEV_CHAVE_EFEMERA=1`.

- [ ] **Passo 1: Escrever os testes que falham** (SQL direto, sem passar pelo cadastro, para testar `audit.verificar_cadeia` isolada)

```csharp
[Collection("postgres")]
public class CadeiaDeAuditoriaTests(PostgresFixture pg)
{
    [Fact]
    public async Task Cadeia_de_um_casal_novo_esta_integra()
    {
        var banco = await pg.NovoBancoAsync();
        var casal = await InserirCasalComAlteracaoAsync(banco);   // INSERT + UPDATE como gf_app
        Assert.Equal(2, await ContarAsync(banco, $"SELECT count(*) FROM audit.eventos WHERE casal_id = '{casal}'"));
        Assert.Equal(0, await ContarAsync(banco, $"SELECT count(*) FROM audit.verificar_cadeia('{casal}')"));
    }

    [Fact]
    public async Task Adulterar_um_evento_quebra_a_cadeia_naquele_ponto()
    {
        var banco = await pg.NovoBancoAsync();
        var casal = await InserirCasalComAlteracaoAsync(banco);
        var adulterado = await AdulterarPrimeiroEventoAsync(banco, casal); // superusuário: DISABLE TRIGGER trg_eventos_imutavel, UPDATE alteracoes, ENABLE

        var (seq, motivo) = await PrimeiraQuebraAsync(banco, casal);
        Assert.Equal(adulterado, seq);
        Assert.Equal("hash não confere", motivo);
    }

    [Fact]
    public async Task Elo_trocado_no_meio_da_cadeia_e_detectado()
    {
        // Três eventos; o superusuário desliga trg_eventos_imutavel e troca o hash_anterior do terceiro
        // para '\x00' (UPDATE, nunca DELETE — regra 1 vale até em teste).
        var banco = await pg.NovoBancoAsync();
        var casal = await InserirCasalComAlteracaoAsync(banco, alteracoes: 2);
        var adulterado = await TrocarHashAnteriorDoUltimoAsync(banco, casal);

        var (seq, motivo) = await PrimeiraQuebraAsync(banco, casal);
        Assert.Equal(adulterado, seq);
        Assert.Equal("elo quebrado", motivo);
    }

    [Fact]
    public async Task Cadeias_de_casais_diferentes_sao_independentes()
    {
        var banco = await pg.NovoBancoAsync();
        var a = await InserirCasalComAlteracaoAsync(banco);
        var b = await InserirCasalComAlteracaoAsync(banco);
        await AdulterarPrimeiroEventoAsync(banco, a);
        Assert.Equal(0, await ContarAsync(banco, $"SELECT count(*) FROM audit.verificar_cadeia('{b}')"));
    }
}
```

Os helpers ficam privados na própria classe e usam `NpgsqlConnection`. Os dados são sintéticos (`nome_familia = 'Família Teste'`). As adulterações usam só `UPDATE`, sempre religando o trigger num `finally`.

- [ ] **Passo 2: Rodar e ver falhar**

Rodar: `dotnet test backend/tests/GestorFinanceiro.IntegrationTests`
Esperado: erro de compilação enquanto a fixture e os helpers não existem.

- [ ] **Passo 3: Implementar** a fixture, o `ApiDeTeste` (que ainda não é usado aqui) e os helpers.

- [ ] **Passo 4: Rodar e ver passar**

Rodar: `dotnet test backend/GestorFinanceiro.slnx`
Esperado: os três projetos de teste verdes. A primeira execução baixa a imagem `postgres:17-alpine`.

- [ ] **Passo 5: Commit** — `Testes de integração com Postgres cobrindo audit.verificar_cadeia`

---

### Tarefa 5: Cadastro do casal na API

**Arquivos:**
- Criar: `backend/src/Modules/Identidade/Aplicacao/CadastroDoCasal.cs` (serviço + `ResultadoDoCadastro`)
- Criar: `backend/src/Modules/Identidade/Api/EndpointsDeCadastro.cs`
- Modificar: `backend/src/Modules/Identidade/IdentidadeModule.cs` (registrar `CadastroDoCasal` scoped; `MapIdentidade` chama `grupo.MapCadastro()`)
- Modificar: `contracts/openapi.json`, `frontend/src/api/schema.d.ts` (regerados)
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/CadastroDoCasalTests.cs`

**Interfaces:**
- Consome: `IEnvelopesDaDek`, `EnvelopeDaDek` (Tarefa 1); `ICodigoDeInstalacao` (Tarefa 2); DTOs e `ValidadorDeCadastro` (Tarefa 3); `PostgresFixture`, `ApiDeTeste`, `CpfSintetico` (Tarefa 4); `Casal.Criar`, `Usuario.Criar`, `Usuario.Vincular`, `ChaveDoCasal.Criar`, `TipoChave` (já existentes em `Dominio/Casal.cs`).
- Produz:
  - `internal abstract record ResultadoDoCadastro` com os casos `Criado(Guid CasalId, string CodigoRecuperacao)`, `Invalido(IReadOnlyDictionary<string, string[]> Erros)`, `CodigoInstalacaoIncorreto` e `JaCadastrado`.
  - `internal sealed class CadastroDoCasal(IdentidadeDbContext db, ICofre cofre, IEnvelopesDaDek envelopes, ICodigoDeInstalacao instalacao)` com:
    - `Task<bool> EstaDisponivelAsync(CancellationToken ct)`: `!await db.Casais.AnyAsync(ct)`.
    - `Task<ResultadoDoCadastro> CadastrarAsync(CadastroDoCasalDto pedido, CancellationToken ct)`.
  - Endpoints (tag `Identidade`):
    - `GET /api/v1/casal/cadastro`, operationId `ObterEstadoDoCadastro` → `200 EstadoDoCadastroDto`.
    - `POST /api/v1/casal`, operationId `CadastrarCasal` → `201 CasalCadastradoDto` (Location `/api/v1/casal`, `Cache-Control: no-store`), `400 ValidationProblem`, `403 Problem` (`title = "Código de instalação incorreto ou expirado."`), `404` (o mesmo `NotFound` de rota inexistente, sem `title` próprio, convertido em Problem Details pelo `UseStatusCodePages`).

**Ordem dentro de `CadastrarAsync`** (o que os testes não determinam sozinhos):
0. `db.Casais.AnyAsync()`. Se já houver casal, `JaCadastrado` (vira `404`), antes de tudo.
1. `instalacao.Confere(pedido.CodigoInstalacao)`. Se falhar, `CodigoInstalacaoIncorreto`, antes de validar qualquer dado pessoal.
2. `ValidadorDeCadastro.Validar`. Se houver erros, `Invalido`.
3. Gerar o código de recuperação e os três `envelopes.Embrulhar(...)` **fora** da transação, porque o Argon2 leva tempo e não deve segurar o lock.
4. `BeginTransactionAsync`, depois `SELECT pg_advisory_xact_lock(hashtextextended('identidade.cadastro', 0))`, depois `Casais.AnyAsync()`. Se já houver casal, `JaCadastrado` (com rollback).
5. Criar o casal e os dois usuários (`Cpf.TentarCriar` já validado) e chamar `SaveChangesAsync`. O `conjuge_id` é uma FK para a própria tabela, então os dois usuários são inseridos sem vínculo primeiro.
6. Chamar `Usuario.Vincular(u1, u2)` e criar as três `ChaveDoCasal`: `Usuario` com `u1.Id`, `Usuario` com `u2.Id` e `Recuperacao` com `null`. Depois `SaveChangesAsync` e `CommitAsync`.
6a. Depois do commit, `instalacao.Consumir()`. Se isso falhar, o casal já existe e a rota já responde `404`; só logar o tipo da exceção.
7. Uma `DbUpdateException` com `PostgresException.SqlState == "23505"` vira `JaCadastrado`. A exceção não é relançada nem logada com valores.

- [ ] **Passo 1: Escrever os testes que falham**

```csharp
[Collection("postgres")]
public class CadastroDoCasalTests(PostgresFixture pg)
{
    private static CadastroDoCasalDto Pedido(string codigo) => new(
        codigo, "Família Teste",
        new ConjugeNovoDto("Ana Teste", CpfSintetico.Gerar(1), "senha-da-ana-123"),
        new ConjugeNovoDto("Bruno Teste", CpfSintetico.Gerar(2), "senha-do-bruno-1"));

    [Fact] public async Task Instalacao_nova_esta_disponivel_para_cadastro() { /* GET → 200 { disponivel: true } */ }

    [Fact]
    public async Task Cadastro_cria_casal_conjuges_vinculados_e_tres_envelopes()
    {
        // POST → 201, Cache-Control no-store, codigoRecuperacao com 8 grupos de 4.
        // Como superusuário:
        // - 1 casal e 2 usuários com conjuge_id cruzado;
        // - cpf_cifrado não contém os 11 dígitos em ASCII;
        // - chaves: 2 USUARIO (usuario_id de cada um) e 1 RECUPERACAO (usuario_id NULL), todas com salt de 16 bytes e versao 1.
        // GET /cadastro → { disponivel: false }.
    }

    [Fact]
    public async Task Senha_de_cada_conjuge_e_codigo_de_recuperacao_abrem_a_mesma_dek()
    {
        // Para cada envelope: DerivarKek(segredo, salt, new ParametrosArgon2()) e Desembrulhar.
        // As três DEKs são iguais, e new CofreAesGcm(dek).Decifrar(cpf_cifrado, Cofre.Aad("identidade.usuarios", "cpf_cifrado", id)) devolve os 11 dígitos.
    }

    [Fact]
    public async Task Cadeia_de_auditoria_fica_integra_apos_o_cadastro()
    {
        // audit.verificar_cadeia(casal) sem linhas.
        // audit.eventos do casal = 8: 1 casais INSERT, 2 usuarios INSERT, 2 usuarios UPDATE (vínculo), 3 chaves INSERT.
        // Nenhum alteracoes::text contém os dígitos do CPF; cpf_cifrado, cpf_hmac e dek_cifrada aparecem como "alterado".
    }

    [Fact] public async Task Segundo_cadastro_responde_404_mesmo_com_o_codigo_certo() { /* e o arquivo do código ficou consumido (validade 0) */ }

    [Fact] public async Task Codigo_com_mais_de_24h_responde_403() { /* new ApiDeTeste(banco, idadeDoCodigo: TimeSpan.FromHours(25)) */ }

    [Fact]
    public async Task Dois_cadastros_simultaneos_criam_um_so_casal()
    {
        // Task.WhenAll de dois POSTs com o mesmo código → status ordenados [201, 404]; count(*) de casais = 1.
    }

    [Fact] public async Task Codigo_de_instalacao_errado_responde_403_e_nao_grava_nada() { /* audit.eventos vazio */ }

    [Fact]
    public async Task Erro_de_validacao_nao_ecoa_cpf_nem_senha()
    {
        // CPF inválido no cônjuge 1 + senha curta no 2 → 400.
        // errors["conjuge1.cpf"] == ["CPF inválido."], errors["conjuge2.senha"] presente.
        // O corpo não contém o CPF enviado nem a senha enviada.
    }
}
```

Os corpos ficam a cargo de quem implementa, seguindo os comentários. Os valores esperados são exatamente os listados acima.

- [ ] **Passo 2: Rodar e ver falhar**

Rodar: `dotnet test backend/tests/GestorFinanceiro.IntegrationTests --filter CadastroDoCasalTests`
Esperado: os testes falham com 404 nas rotas, ou com erro de compilação nos DTOs ainda não usados.

- [ ] **Passo 3: Implementar** o serviço e os endpoints. O endpoint mapeia cada `ResultadoDoCadastro` para `TypedResults` e põe `Cache-Control: no-store` no 201.

- [ ] **Passo 4: Rodar e ver passar**

Rodar: `dotnet test backend/GestorFinanceiro.slnx`
Esperado: tudo verde e 0 avisos.

- [ ] **Passo 5: Regerar o contrato**

Rodar: `dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api && npm --prefix frontend run check`
Esperado: `contracts/openapi.json` com `/api/v1/casal` (POST) e `/api/v1/casal/cadastro`; check com saída 0.

- [ ] **Passo 6: Commit** — `Cadastro do casal: endpoints, envelopes da DEK e testes de integração`

---

### Tarefa 6: Tela de cadastro

**Arquivos:**
- Criar: `frontend/src/api/cadastro.ts`
- Criar: `frontend/src/paginas/Cadastro.tsx`, `frontend/src/paginas/CodigoDeRecuperacao.tsx`
- Modificar: `frontend/src/App.tsx` (rota `/cadastro` fora do `<Layout />`), `frontend/src/layout/Layout.tsx` (redireciona para `/cadastro` quando `disponivel`), `frontend/src/styles/app.css` (classes novas)
- Modificar: `docs/SPEC.md` (marcar `[x]` em "Cadastro do casal")

**Interfaces:**
- Consome: `paths["/api/v1/casal"]["post"]` e `paths["/api/v1/casal/cadastro"]["get"]` de `src/api/schema.d.ts` (Tarefa 5).
- Produz:
  - `useEstadoDoCadastro()`: `useQuery` com chave `["cadastro"]`, devolve `EstadoDoCadastroDto`.
  - `useCadastrarCasal()`: `useMutation` que devolve `CasalCadastradoDto`. Em erro 400, expõe `errors` do Problem Details como `Record<string, string[]>`.
  - Classes CSS: `.pagina-centro` (coluna centralizada, `max-width: 880px`, `padding: 40px 24px`), `.grade-form` (`repeat(auto-fit, minmax(300px, 1fr))`, `gap: 16px`), `.campo`, `.campo-rotulo`, `.campo-erro` (cor `var(--negativo)`, 12px), `.entrada` (altura 44px, raio 12px, borda `var(--borda)`, fundo `var(--superficie)`, foco com contorno `var(--destaque)`), `.botao-primario` (fundo `var(--destaque)`, texto `var(--destaque-texto)`, altura 44px, raio 12px, peso 600), `.codigo-recuperacao` (`num`, 22px, `letter-spacing: 0.08em`, grupos com quebra de linha).

**Visual** (base: `docs/design/telas/Main.dc.html` e o padrão do `docs/design/README.md`):
- **Topo e título:** sem barra lateral, porque ainda não há casal. Marca "GF" (`.marca`) no topo e `h1.titulo` "Cadastro do casal", com `.subtitulo` "Os dois juntos, uma vez só. Depois disso esta tela some."
- **Cartão "Família":** campo "Nome da família" e campo "Código de instalação", com nota "Impresso no servidor por `--codigo-instalacao`."
- **Cartões "Cônjuge 1" e "Cônjuge 2" lado a lado (`.grade-form`):** campos Nome completo, CPF (`inputMode="numeric"`, máscara `000.000.000-00` só na digitação), Senha e Confirmar senha.
  - Senha: `type="password"`, `autoComplete="new-password"`, com a nota "Mínimo de 12 caracteres. Ela protege a chave dos dados — não é recuperável."
  - A confirmação é checada só no front e não é enviada.
- **Botão:** um único destaque verde-água, `.botao-primario` "Criar cadastro", desabilitado enquanto a mutação roda (texto "Protegendo as chaves…").
- **Erros:** cada `errors[chave]` aparece em `.campo-erro` sob o campo, ligado por `aria-describedby`. 403 e 404 aparecem num `role="alert"` no topo do formulário com o `title` do Problem Details.
- **Tela do código de recuperação (`CodigoDeRecuperacao`, mesmo componente de página e mesmo estado da mutação):**
  - Cartão `.cartao.destaque` com o título "Guarde este código em papel" e o código em `.codigo-recuperacao`.
  - Texto: "É a única forma de abrir os dados se os dois esquecerem a senha. Ele não será mostrado de novo."
  - Botão secundário "Imprimir" (`window.print()`).
  - Checkbox com `label` "Imprimi e guardei o código", que habilita o `.botao-primario` "Concluir".
  - Enquanto o checkbox não estiver marcado, há um `beforeunload` registrado.
  - "Concluir" chama `mutation.reset()`, invalida `["cadastro"]` e navega para `/`.
- **Nada em `localStorage` ou `sessionStorage`.** O código vive só no estado da mutação.

- [ ] **Passo 1: Implementar** `api/cadastro.ts`, as duas páginas, as rotas, o redirecionamento no `Layout` e o CSS. `/cadastro` redireciona para `/` quando `disponivel` é `false` e não há código sendo exibido.

- [ ] **Passo 2: Verificar os tipos e o build**

Rodar: `npm --prefix frontend run check`
Esperado: saída 0.

- [ ] **Passo 3: Subir o sistema para teste manual**

Postgres local migrado e `GF_DEV_CHAVE_EFEMERA=1`. Gerar o código com `dotnet run --project backend/src/Host/GestorFinanceiro.Api -- --codigo-instalacao`, depois subir a API (`dotnet run`) e o front (`npm --prefix frontend run dev`).

- [ ] **Passo 4: Conferir o fluxo feliz no navegador** (Playwright ou à mão)

- Abrir `/`: o app redireciona para `/cadastro`.
- Preencher com CPFs sintéticos e enviar: aparece o código de recuperação.

- [ ] **Passo 5: Conferir os erros**

- CPF inválido mostra "CPF inválido." sob o campo certo.
- Senhas diferentes da confirmação bloqueiam o envio no front.
- Código de instalação errado mostra o alerta 403.

- [ ] **Passo 6: Conferir o item 5 do foco da revisão**

- Na tela do código, recarregar dispara o aviso do navegador.
- "Concluir" fica desabilitado até marcar o checkbox.
- Depois de concluir, `/cadastro` volta para `/`.
- No DevTools, Application mostra `localStorage` e `sessionStorage` vazios.

- [ ] **Passo 7: Comparar com o canvas**

Abrir `docs/design/telas/Main.dc.html` ao lado da tela e conferir:
- cartões `#111720`, borda `#1F2935`, raio 16;
- título de 28px;
- um único destaque verde-água por tela;
- contraste dos textos de erro (`#FB7185` sobre `#111720` ≥ 4.5:1).

- [ ] **Passo 8: Commit** — `Tela de cadastro do casal`

---

## Fora do escopo (próximas tarefas do SPEC)

- Login com sessão em cookie, passkey e TOTP, e troca de senha, que regrava o envelope `USUARIO` com `versao` + 1.
- Tela para usar o código de recuperação.
- Telefone dos cônjuges, que o bot usa na Fase 3.
