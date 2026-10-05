using System.Collections.Concurrent;
using System.Diagnostics;
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

        // Botão "Authorize" no Swagger para o header X-Api-Key
        doc.Components ??= new();
        doc.Components.SecuritySchemes = new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>
        {
            ["ApiKey"] = new Microsoft.OpenApi.OpenApiSecurityScheme
            {
                Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
                Name = "X-Api-Key",
                In = Microsoft.OpenApi.ParameterLocation.Header,
                Description = "Chave de API (necessária só se 'ApiKey' estiver configurada)."
            }
        };
        doc.Security = [new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("ApiKey", doc)] = []
        }];
        return Task.CompletedTask;
    });
});
builder.Services.AddSingleton<IErpClient, ErpSimulado>();
builder.Services.AddSingleton<ISugestaoService, SugestaoPorRegras>();
builder.Services.AddSingleton<ValidadorPedido>();
builder.Services.AddSingleton<EventoLogger>();
builder.Services.AddSingleton<ConcurrentDictionary<string, ResultadoProcessamento>>(); // idempotência por MessageId

var app = builder.Build();

// Chave de API (header X-Api-Key). Se "ApiKey" não estiver configurada, a API fica aberta (somente dev).
var apiKey = app.Configuration["ApiKey"];
app.MapOpenApi("/openapi.json");
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi.json", "API StatusDePedidos");
    o.RoutePrefix = "swagger";
});
app.Use(async (ctx, next) =>
{
    var livre = ctx.Request.Path == "/health"
        || ctx.Request.Path.StartsWithSegments("/openapi.json")
        || ctx.Request.Path.StartsWithSegments("/swagger");
    if (!string.IsNullOrEmpty(apiKey) && !livre && ctx.Request.Headers["X-Api-Key"] != apiKey)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithName("Health").WithSummary("Verificação de saúde");

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
.WithName("ObterMetricas").WithSummary("Indicadores de processamento").WithDescription("Volume, gravados, divergentes, erros, taxa de automação e duração média.");

app.Run();

static EventoLog Evento(string corr, DocumentoExtraido d, string status, List<Divergencia> divs, Stopwatch sw, string? erro) =>
    new(DateTime.UtcNow, corr, d.MessageId, status, d.NumeroPedido, d.Fornecedor.Nome, d.ValorTotal,
        divs.Count, divs.Count == 0 ? null : string.Join(",", divs.Select(x => x.Regra)), sw.ElapsedMilliseconds, erro);
