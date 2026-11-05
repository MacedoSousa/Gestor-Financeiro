using GestorFinanceiro.Api.Contracts;
using GestorFinanceiro.Api.Infra;
using GestorFinanceiro.Modules.Assistente;
using GestorFinanceiro.Modules.Auditoria;
using GestorFinanceiro.Modules.Boletos;
using GestorFinanceiro.Modules.Contas;
using GestorFinanceiro.Modules.Identidade;
using GestorFinanceiro.Modules.Integracoes;
using GestorFinanceiro.Modules.Investimentos;
using GestorFinanceiro.Modules.Lancamentos;
using GestorFinanceiro.Modules.Orcamento;
using GestorFinanceiro.SharedKernel.Aplicacao;
using GestorFinanceiro.SharedKernel.Persistencia;
using GestorFinanceiro.SharedKernel.Seguranca;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// O modo --migrar aplica db/migrations com o papel gf_migrator e sai.
if (args.Contains("--migrar"))
{
    var conexaoMigrador = builder.Configuration.GetConnectionString("Migrador")
        ?? throw new InvalidOperationException("ConnectionStrings:Migrador não configurada.");
    await MigradorSql.AplicarAsync(conexaoMigrador, Path.Combine(AppContext.BaseDirectory, "migrations"), Console.Out);
    return;
}

// Primeira instalação: cria a DEK e imprime o código de recuperação uma única vez.
if (args.Contains("--inicializar-chaves"))
{
    await CofreDoServidor.InicializarAsync(builder.Configuration, builder.Environment.ContentRootPath, Console.Out);
    return;
}

var conexao = builder.Configuration.GetConnectionString("App") is { Length: > 0 } cs
    ? cs
    : "Host=localhost;Database=gestor;Username=gf_app";

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IUsuarioAtual, UsuarioAtualHttp>();
builder.Services.AddScoped<SoftDeleteInterceptor>();
builder.Services.AddScoped<ContextoAuditoriaInterceptor>();

// A DEK só é carregada quando algo precisa dela (nunca no build do OpenAPI).
if (builder.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("GF_DEV_CHAVE_EFEMERA") == "1")
{
    // Modo efêmero: uma DEK por processo, a mesma para o cofre e para os envelopes.
    var dekEfemera = EnvelopeDeChaves.GerarDek();
    builder.Services.AddSingleton<ICofre>(_ => new CofreAesGcm(dekEfemera));
    builder.Services.AddSingleton<IEnvelopesDaDek>(_ => new EnvelopesDaDek(() => dekEfemera.ToArray(), new ParametrosArgon2()));
}
else
{
    builder.Services.AddSingleton<ICofre>(sp => CofreDoServidor.Carregar(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
    builder.Services.AddSingleton<IEnvelopesDaDek>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var ambiente = sp.GetRequiredService<IHostEnvironment>();
        return new EnvelopesDaDek(() => CofreDoServidor.LerDek(config, ambiente), new ParametrosArgon2());
    });
}

builder.Services
    .AddIdentidade(conexao)
    .AddContas(conexao)
    .AddLancamentos(conexao)
    .AddOrcamento(conexao)
    .AddBoletos(conexao)
    .AddInvestimentos(conexao)
    .AddIntegracoes(conexao)
    .AddAssistente(conexao)
    .AddAuditoria(conexao);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Só o Caddy (rede Docker interna) pode informar o IP real do cliente.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var proxy in builder.Configuration.GetSection("Rede:ProxiesConfiaveis").Get<string[]>() ?? [])
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(proxy));
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.MapOpenApi("/openapi/{documentName}.json");

var api = app.MapGroup("/api/v1");

api.MapGet("/saude", () => TypedResults.Ok(new SaudeDto("ok", DateTimeOffset.UtcNow)))
    .WithName("Saude")
    .WithTags("Sistema")
    .WithSummary("Verificação simples de que a API está de pé.");

api.MapIdentidade()
   .MapContas()
   .MapLancamentos()
   .MapOrcamento()
   .MapBoletos()
   .MapInvestimentos()
   .MapIntegracoes()
   .MapAssistente()
   .MapAuditoria();

app.Run();
