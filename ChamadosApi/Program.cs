using System.Collections.Concurrent;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi(options =>
{
    // Copilot Studio lida melhor com OpenAPI 3.0 do que com 3.1
    options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
    options.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "API de Chamados";
        doc.Info.Description = "Consulta e abertura de chamados de suporte.";
        return Task.CompletedTask;
    });
});

builder.Services.AddSingleton<ChamadoStore>();

var app = builder.Build();

app.MapOpenApi("/openapi.json");
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi.json", "API de Chamados");
    o.RoutePrefix = "swagger";
});

// Chave simples de API (opcional): defina "ApiKey" em appsettings para exigir o header X-Api-Key
var apiKey = app.Configuration["ApiKey"];
app.Use(async (ctx, next) =>
{
    if (!string.IsNullOrEmpty(apiKey) && !ctx.Request.Path.StartsWithSegments("/openapi.json")
        && !ctx.Request.Path.StartsWithSegments("/swagger")
        && ctx.Request.Headers["X-Api-Key"] != apiKey)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/chamados", (ChamadoStore store, string? status) =>
        store.Listar(status))
    .WithName("ListarChamados")
    .WithSummary("Lista chamados")
    .WithDescription("Retorna os chamados de suporte. Filtre por status: Aberto, EmAndamento ou Resolvido.");

app.MapGet("/chamados/{id:int}", (ChamadoStore store, int id) =>
        store.Obter(id) is { } c ? Results.Ok(c) : Results.NotFound())
    .WithName("ObterChamado")
    .WithSummary("Consulta um chamado pelo número")
    .WithDescription("Retorna os detalhes e o status de um chamado a partir do seu id.");

app.MapPost("/chamados", (ChamadoStore store, NovoChamado novo) =>
    {
        if (string.IsNullOrWhiteSpace(novo.Titulo))
            return Results.BadRequest("Titulo é obrigatório.");
        var c = store.Criar(novo);
        return Results.Created($"/chamados/{c.Id}", c);
    })
    .WithName("AbrirChamado")
    .WithSummary("Abre um novo chamado")
    .WithDescription("Cria um chamado de suporte com título, descrição e solicitante. Retorna o chamado criado com seu número.");

app.Run();

public record Chamado(int Id, string Titulo, string Descricao, string Solicitante, string Status, DateTime CriadoEm);
public record NovoChamado(string Titulo, string? Descricao, string? Solicitante);

public class ChamadoStore
{
    private readonly ConcurrentDictionary<int, Chamado> _dados = new();
    private int _seq;

    public ChamadoStore()
    {
        Criar(new("Impressora não imprime", "Fila travada no 2º andar", "ana@empresa.com"));
        Criar(new("Acesso à VPN", "Erro de autenticação", "joao@empresa.com"));
        _dados[2] = _dados[2] with { Status = "EmAndamento" };
    }

    public Chamado Criar(NovoChamado n)
    {
        var id = Interlocked.Increment(ref _seq);
        var c = new Chamado(id, n.Titulo, n.Descricao ?? "", n.Solicitante ?? "", "Aberto", DateTime.UtcNow);
        _dados[id] = c;
        return c;
    }

    public Chamado? Obter(int id) => _dados.GetValueOrDefault(id);

    public IEnumerable<Chamado> Listar(string? status) =>
        _dados.Values.Where(c => status is null || c.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c.Id);
}
