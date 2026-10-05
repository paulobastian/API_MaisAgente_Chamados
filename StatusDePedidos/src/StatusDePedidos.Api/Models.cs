namespace StatusDePedidos.Api;

public record Fornecedor(string? Nome, string? Cnpj);
public record ItemPedido(string Descricao, decimal Quantidade, decimal ValorUnitario);

/// <summary>Campos extraídos pelo Azure AI Document Intelligence (enviados pelo Power Automate).</summary>
public record DocumentoExtraido(
    string MessageId,
    string? RemetenteEmail,
    Fornecedor Fornecedor,
    string? NumeroPedido,
    decimal? ValorTotal,
    List<ItemPedido>? Itens,
    double? ConfiancaExtracao);

public enum StatusProcessamento { Gravado, Divergente, Duplicado, Erro }

public record Divergencia(string Regra, string Detalhe);

public record ResultadoProcessamento(
    string CorrelationId,
    StatusProcessamento Status,
    string? NumeroPedido,
    List<Divergencia> Divergencias,
    string? SugestaoAcao,
    string? ResponsavelEmail);

public record PedidoErp(string Numero, string CnpjFornecedor, decimal ValorEsperado, string ResponsavelEmail, bool Recebido);

public record EventoLog(
    DateTime Timestamp,
    string CorrelationId,
    string MessageId,
    string Status,
    string? NumeroPedido,
    string? Fornecedor,
    decimal? Valor,
    int QtdDivergencias,
    string? Regras,
    long DuracaoMs,
    string? Erro);

/// <summary>App roles definidos no app registration da API (Entra ID → App roles).</summary>
public static class Roles
{
    public const string Processar = "Pedidos.Processar";
    public const string Ler = "Pedidos.Ler";
}

public static class Politicas
{
    public const string Processar = "Processar";
    public const string Ler = "Ler";
}
