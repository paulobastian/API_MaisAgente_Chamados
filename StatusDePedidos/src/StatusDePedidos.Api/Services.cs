using System.Collections.Concurrent;
using System.Text.Json;

namespace StatusDePedidos.Api;

/// <summary>Abstração do ERP. Troque a implementação simulada pela integração real (SAP, Protheus, Dynamics...).</summary>
public interface IErpClient
{
    PedidoErp? ObterPedido(string numero);
    bool FornecedorAtivo(string cnpj);
    void GravarRecebimento(string numeroPedido, decimal valor);
}

public class ErpSimulado : IErpClient
{
    private readonly ConcurrentDictionary<string, PedidoErp> _pedidos = new()
    {
        ["PED-1001"] = new("PED-1001", "11222333000181", 1500.00m, "compras@empresa.com", false),
        ["PED-1002"] = new("PED-1002", "44555666000172", 980.50m, "compras@empresa.com", false),
        ["PED-1003"] = new("PED-1003", "11222333000181", 250.00m, "financeiro@empresa.com", false),
    };
    private readonly HashSet<string> _fornecedores = ["11222333000181", "44555666000172"];

    public PedidoErp? ObterPedido(string numero) => _pedidos.GetValueOrDefault(numero);
    public bool FornecedorAtivo(string cnpj) => _fornecedores.Contains(cnpj);

    public void GravarRecebimento(string numeroPedido, decimal valor) =>
        _pedidos.AddOrUpdate(numeroPedido,
            _ => throw new KeyNotFoundException(numeroPedido),
            (_, p) => p with { Recebido = true });
}

public class ValidadorPedido(IErpClient erp, IConfiguration cfg)
{
    private readonly decimal _tolerancia = cfg.GetValue("Validacao:ToleranciaValor", 0.01m);
    private readonly double _confiancaMinima = cfg.GetValue("Validacao:ConfiancaMinima", 0.80);

    public (List<Divergencia> divergencias, PedidoErp? pedido) Validar(DocumentoExtraido doc)
    {
        var d = new List<Divergencia>();
        var cnpj = SomenteDigitos(doc.Fornecedor.Cnpj);

        if (doc.ConfiancaExtracao is { } c && c < _confiancaMinima)
            d.Add(new("ConfiancaBaixa", $"Confiança da extração {c:P0} abaixo do mínimo {_confiancaMinima:P0}."));

        // Fornecedor
        if (string.IsNullOrEmpty(cnpj))
            d.Add(new("FornecedorAusente", "CNPJ do fornecedor não foi extraído."));
        else if (!erp.FornecedorAtivo(cnpj))
            d.Add(new("FornecedorDesconhecido", $"CNPJ {cnpj} não cadastrado ou inativo no ERP."));

        // Pedido
        PedidoErp? pedido = null;
        if (string.IsNullOrWhiteSpace(doc.NumeroPedido))
            d.Add(new("PedidoAusente", "Número do pedido não foi extraído."));
        else
        {
            pedido = erp.ObterPedido(doc.NumeroPedido.Trim().ToUpperInvariant());
            if (pedido is null)
                d.Add(new("PedidoInexistente", $"Pedido {doc.NumeroPedido} não existe no ERP."));
            else if (!string.IsNullOrEmpty(cnpj) && pedido.CnpjFornecedor != cnpj)
                d.Add(new("PedidoOutroFornecedor", $"Pedido {pedido.Numero} pertence ao CNPJ {pedido.CnpjFornecedor}, não a {cnpj}."));
        }

        // Valores
        if (doc.ValorTotal is null)
            d.Add(new("ValorAusente", "Valor total não foi extraído."));
        else
        {
            if (doc.Itens is { Count: > 0 })
            {
                var soma = doc.Itens.Sum(i => i.Quantidade * i.ValorUnitario);
                if (Math.Abs(soma - doc.ValorTotal.Value) > _tolerancia)
                    d.Add(new("TotalInconsistente", $"Soma dos itens ({soma:N2}) difere do total ({doc.ValorTotal:N2})."));
            }
            if (pedido is not null && Math.Abs(pedido.ValorEsperado - doc.ValorTotal.Value) > _tolerancia)
                d.Add(new("ValorDivergentePedido", $"Valor do documento ({doc.ValorTotal:N2}) difere do pedido no ERP ({pedido.ValorEsperado:N2})."));
        }
        return (d, pedido);
    }

    private static string SomenteDigitos(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
}

/// <summary>Gera a sugestão de ação para o responsável. Implementação por regras; substitua por Azure OpenAI se desejar.</summary>
public interface ISugestaoService
{
    Task<string> GerarAsync(DocumentoExtraido doc, IReadOnlyList<Divergencia> divergencias, CancellationToken ct);
}

public class SugestaoPorRegras : ISugestaoService
{
    public Task<string> GerarAsync(DocumentoExtraido doc, IReadOnlyList<Divergencia> divs, CancellationToken ct)
    {
        var acoes = divs.Select(d => d.Regra switch
        {
            "ValorDivergentePedido" => "Contatar o fornecedor para confirmar o valor e solicitar nota corrigida ou aprovar a diferença.",
            "TotalInconsistente" => "Conferir os itens do documento; possível erro de leitura ou de emissão.",
            "FornecedorDesconhecido" => "Verificar o cadastro do fornecedor antes de prosseguir (risco de fraude).",
            "PedidoInexistente" or "PedidoAusente" => "Confirmar com o solicitante qual pedido originou este documento.",
            "PedidoOutroFornecedor" => "Pedido não pertence a este fornecedor: rejeitar ou corrigir o vínculo.",
            "ConfiancaBaixa" => "Revisar o documento manualmente; a extração automática foi pouco confiável.",
            _ => "Revisar manualmente."
        }).Distinct();
        return Task.FromResult(string.Join(" ", acoes));
    }
}

/// <summary>Log estruturado em JSON Lines (um evento por linha), pronto para ingestão no Fabric (Lakehouse/Eventstream) e Power BI.</summary>
public class EventoLogger(IConfiguration cfg)
{
    private readonly string _path = cfg["Logs:Caminho"] ?? Path.Combine("logs", "eventos.jsonl");
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Registrar(EventoLog e)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        lock (_lock) File.AppendAllText(_path, JsonSerializer.Serialize(e, Json) + Environment.NewLine);
    }

    public List<EventoLog> Ler()
    {
        if (!File.Exists(_path)) return [];
        lock (_lock)
            return File.ReadLines(_path).Where(l => l.Length > 0)
                .Select(l => JsonSerializer.Deserialize<EventoLog>(l, Json)!).ToList();
    }
}
