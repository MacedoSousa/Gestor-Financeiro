using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GestorFinanceiro.IntegrationTests.Infra;

/// <summary>Registra em memória o texto de cada log (mensagem formatada e exceção), para os testes conferirem o que vazou.</summary>
internal sealed class LogsCapturados : ILoggerProvider
{
    public ConcurrentQueue<string> Linhas { get; } = new();

    public ILogger CreateLogger(string categoria) => new Registro(this);

    public void Dispose() { }

    private sealed class Registro(LogsCapturados dono) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel nivel) => true;

        public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? excecao, Func<TState, Exception?, string> formatador) =>
            dono.Linhas.Enqueue($"{nivel}: {formatador(estado, excecao)}{(excecao is null ? "" : " | " + excecao)}");
    }
}
