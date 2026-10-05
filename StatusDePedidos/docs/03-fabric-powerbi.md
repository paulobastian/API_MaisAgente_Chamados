# 3. Logs no Fabric e indicadores no Power BI

## Formato do log
A API grava um evento por processamento em JSON Lines (`logs/eventos.jsonl`):

| Campo | Descrição |
|---|---|
| `timestamp` | UTC |
| `correlationId` | Id do processamento (liga API, fluxo e Teams) |
| `messageId` | Id do e-mail + anexo |
| `status` | `Gravado`, `Divergente`, `Duplicado`, `Erro` |
| `numeroPedido`, `fornecedor`, `valor` | Dados do documento |
| `qtdDivergencias`, `regras` | Quantidade e lista de regras violadas (separadas por vírgula) |
| `duracaoMs` | Tempo de processamento |
| `erro` | Mensagem, quando `status = Erro` |

## Caminho até o Fabric
1. **Simples (início):** a API roda em Azure e o arquivo vai para o **OneLake/Lakehouse** (pasta `Files/logs`), por exemplo via *Data pipeline* agendado ou Azure Storage com atalho (*shortcut*) no Lakehouse.
2. **Quase em tempo real (recomendado):** trocar `EventoLogger` por um envio ao **Fabric Eventstream** (endpoint Event Hubs/custom endpoint) e gravar em uma tabela do Lakehouse ou KQL Database.
3. No Lakehouse, crie a tabela `eventos_pedidos` a partir do JSONL (notebook ou *Load to table*).

## Modelo semântico e medidas (DAX)
Tabela `eventos_pedidos` + tabela de datas.

```DAX
Volume = CALCULATE(COUNTROWS(eventos_pedidos), eventos_pedidos[status] <> "Duplicado")

Gravados = CALCULATE([Volume], eventos_pedidos[status] = "Gravado")

Divergentes = CALCULATE([Volume], eventos_pedidos[status] = "Divergente")

Erros = CALCULATE([Volume], eventos_pedidos[status] = "Erro")

Taxa de Automação = DIVIDE([Gravados], [Volume])

Taxa de Erro = DIVIDE([Erros], [Volume])

Tempo Médio (ms) = AVERAGE(eventos_pedidos[duracaoMs])
```

## Visuais sugeridos
- Cartões: Volume, Taxa de Automação, Taxa de Erro, Tempo Médio.
- Linha: Volume por dia, com Gravados vs Divergentes.
- Barras: principais regras de divergência. Para isso, divida `regras` em linhas no Power Query (*Dividir coluna por delimitador → em linhas*).
- Tabela: últimos erros (`timestamp`, `messageId`, `erro`).
- Segmentações: fornecedor e período.

## Alertas
No Fabric (Activator) ou no Power BI, crie um alerta quando a `Taxa de Erro` passar de 5% ou a `Taxa de Automação` cair abaixo da meta.
