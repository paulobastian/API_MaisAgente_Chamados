using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using StatusDePedidos.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0;
    options.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "API StatusDePedidos";
        doc.Info.Description = "Valida documentos de pedido extraídos por IA e grava no ERP.";

        // Botão "Authorize" no Swagger: cole o access token (OAuth2 client credentials) obtido no Entra ID
        doc.Components ??= new();
        doc.Components.SecuritySchemes = new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>
        {
            ["Bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
            {
                Type = Microsoft.OpenApi.SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Access token OAuth2 (Microsoft Entra ID). Cole só o token, sem o prefixo 'Bearer'."
            }
        };
        doc.Security = [new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", doc)] = []
        }];
        return Task.CompletedTask;
    });
});

// --- Autenticação OAuth2 (Microsoft Entra ID, JWT Bearer) ---
// Auth:Authority  = https://login.microsoftonline.com/{tenantId}/v2.0
// Auth:Audience   = Application ID URI ou client id do app registration da API
// Fora de Development a configuração é obrigatória (a API não sobe aberta).
var authority = builder.Configuration["Auth:Authority"];
var authAtiva = !string.IsNullOrWhiteSpace(authority);
if (!authAtiva && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Auth:Authority não configurado. A API não pode rodar sem autenticação fora de Development.");

if (!authAtiva)
    builder.Services.AddAuthentication(); // Development sem Auth:Authority: sem esquema, políticas liberadas
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.Authority = authority;
            o.TokenValidationParameters.ValidAudiences = builder.Configuration.GetSection("Auth:Audience").Get<string[]>()
                ?? [builder.Configuration["Auth:Audience"] ?? ""];
            o.MapInboundClaims = false; // mantém o nome original das claims ("roles", não o URI longo)
            o.TokenValidationParameters.RoleClaimType = "roles"; // app roles do Entra ID
        });
}
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Politicas.Processar, p => { if (authAtiva) p.RequireRole(Roles.Processar); else p.RequireAssertion(_ => true); })
    .AddPolicy(Politicas.Ler, p => { if (authAtiva) p.RequireRole(Roles.Ler, Roles.Processar); else p.RequireAssertion(_ => true); });

builder.Services.AddSingleton<IErpClient, ErpSimulado>();
builder.Services.AddSingleton<ISugestaoService, SugestaoPorRegras>();
builder.Services.AddSingleton<ValidadorPedido>();
builder.Services.AddSingleton<EventoLogger>();
builder.Services.AddSingleton<ConcurrentDictionary<string, ResultadoProcessamento>>(); // idempotência por MessageId

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/openapi.json").AllowAnonymous();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi.json", "API StatusDePedidos");
    o.RoutePrefix = "swagger";
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().WithName("Health").WithSummary("Verificação de saúde");

// Chamado pelo Power Automate com os campos extraídos pelo Document Intelligence.
app.MapPost("/pedidos/processar", async (
    DocumentoExtraido doc, ValidadorPedido validador, IErpClient erp, ISugestaoService sugestao,
    EventoLogger log, ConcurrentDictionary<string, ResultadoProcessamento> processados, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var correlationId = Guid.NewGuid().ToString("N");

    if (string.IsNullOrWhiteSpace(doc.MessageId))
        return Results.BadRequest("MessageId é obrigatório (usado para idempotência).");

    if (processados.TryGetValue(doc.MessageId, out var anterior))
    {
        var dup = anterior with { CorrelationId = correlationId, Status = StatusProcessamento.Duplicado };
        log.Registrar(Evento(correlationId, doc, "Duplicado", [], sw, null));
        return Results.Ok(dup);
    }

    try
    {
        var (divs, pedido) = validador.Validar(doc);
        ResultadoProcessamento resultado;

        if (divs.Count == 0 && pedido is not null)
        {
            erp.GravarRecebimento(pedido.Numero, doc.ValorTotal!.Value);
            resultado = new(correlationId, StatusProcessamento.Gravado, pedido.Numero, [], null, null);
        }
        else
        {
            var texto = await sugestao.GerarAsync(doc, divs, ct);
            resultado = new(correlationId, StatusProcessamento.Divergente, doc.NumeroPedido, divs, texto,
                pedido?.ResponsavelEmail ?? app.Configuration["Notificacao:ResponsavelPadrao"]);
        }

        processados[doc.MessageId] = resultado;
        log.Registrar(Evento(correlationId, doc, resultado.Status.ToString(), divs, sw, null));
        return Results.Ok(resultado);
    }
    catch (Exception ex)
    {
        log.Registrar(Evento(correlationId, doc, "Erro", [], sw, ex.Message));
        return Results.Problem(ex.Message, statusCode: 500);
    }
})
.RequireAuthorization(Politicas.Processar)
.WithName("ProcessarPedido").WithSummary("Processa um documento de pedido").WithDescription("Valida fornecedor, pedido e valores. Grava no ERP se estiver tudo certo; caso contrário devolve as divergências e uma sugestão de ação.");

// Indicadores simples (o dashboard completo fica no Power BI sobre os logs no Fabric).
app.MapGet("/metricas", (EventoLogger log) =>
{
    var ev = log.Ler().Where(e => e.Status != "Duplicado").ToList();
    var total = ev.Count;
    return new
    {
        volume = total,
        gravados = ev.Count(e => e.Status == "Gravado"),
        divergentes = ev.Count(e => e.Status == "Divergente"),
        erros = ev.Count(e => e.Status == "Erro"),
        taxaAutomacao = total == 0 ? 0 : Math.Round((double)ev.Count(e => e.Status == "Gravado") / total, 4),
        duracaoMediaMs = total == 0 ? 0 : Math.Round(ev.Average(e => e.DuracaoMs), 1)
    };
})
.RequireAuthorization(Politicas.Ler)
.WithName("ObterMetricas").WithSummary("Indicadores de processamento").WithDescription("Volume, gravados, divergentes, erros, taxa de automação e duração média.");

app.Run();

static EventoLog Evento(string corr, DocumentoExtraido d, string status, List<Divergencia> divs, Stopwatch sw, string? erro) =>
    new(DateTime.UtcNow, corr, d.MessageId, status, d.NumeroPedido, d.Fornecedor.Nome, d.ValorTotal,
        divs.Count, divs.Count == 0 ? null : string.Join(",", divs.Select(x => x.Regra)), sw.ElapsedMilliseconds, erro);

/// <summary>Necessário para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
