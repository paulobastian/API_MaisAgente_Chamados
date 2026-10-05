# StatusDePedidos

Automação de recebimento de documentos de pedido por e-mail.

```
E-mail com anexo
   │  (Power Automate)
   ▼
Azure AI Document Intelligence ── extrai fornecedor, nº do pedido, itens e valores
   │
   ▼
API C# (src/StatusDePedidos.Api) ── valida regras ── grava no ERP
   │                                   │
   │ Gravado                           │ Divergente
   ▼                                   ▼
Log (JSONL)                 Teams: responsável + sugestão de ação (IA)
   │
   ▼
Fabric (Lakehouse) ──► Power BI: volume, taxa de automação, erros
```

## API

| Endpoint | Uso |
|---|---|
| `POST /pedidos/processar` | 🔒 role `Pedidos.Processar`. Recebe os campos extraídos, valida, grava no ERP ou devolve divergências + sugestão |
| `GET /metricas` | 🔒 role `Pedidos.Ler` ou `Pedidos.Processar`. Volume, gravados, divergentes, erros, taxa de automação |
| `GET /health` | Verificação de saúde (público) |

Regras de validação (em `Services.cs`, `ValidadorPedido`): confiança da extração, fornecedor (CNPJ cadastrado), pedido (existe e pertence ao fornecedor), valor (soma dos itens = total; total = valor do pedido no ERP, com tolerância).

Idempotência: o mesmo `messageId` não é gravado duas vezes (retorna `Duplicado`).

### Rodar e testar
```
cd src/StatusDePedidos.Api
dotnet run --urls http://localhost:5090
# em Development sem Auth:Authority a API fica aberta; com autenticação, envie -H "Authorization: Bearer <token>"
curl -X POST localhost:5090/pedidos/processar -H "Content-Type: application/json" -d @../../samples/ok.json
curl localhost:5090/metricas
```
Payloads de exemplo em [samples/](samples/): `ok.json`, `divergente_valor.json`, `divergente_fornecedor.json`.

### Configuração (appsettings / variáveis de ambiente)
| Chave | Padrão | Descrição |
|---|---|---|
| `Auth:Authority` | vazio | Emissor OAuth2 (`https://login.microsoftonline.com/{tenant}/v2.0`). Obrigatório fora de Development |
| `Auth:Audience` | vazio | Application ID URI da API (ex.: `api://statusdepedidos`) |
| `Validacao:ToleranciaValor` | 0.01 | Diferença de valor aceita |
| `Validacao:ConfiancaMinima` | 0.80 | Abaixo disso, vira divergência |
| `Notificacao:ResponsavelPadrao` | vazio | Destinatário quando o ERP não indica responsável |
| `Logs:Caminho` | `logs/eventos.jsonl` | Arquivo de eventos |

## Pontos a trocar para produção
- `ErpSimulado` → cliente real do ERP (interface `IErpClient`).
- `SugestaoPorRegras` → Azure OpenAI (interface `ISugestaoService`), enviando as divergências e devolvendo um texto curto de ação.
- Idempotência em memória → tabela/Redis (reinicia a API e perde o histórico).
- Log em arquivo → Eventstream do Fabric ou escrita direta no Lakehouse.

## Montagem das demais peças
Passo a passo em [docs/](docs/):
0. [00-oauth2-entra-id.md](docs/00-oauth2-entra-id.md): autenticação OAuth2 (Entra ID), roles e como obter token
1. [01-power-automate.md](docs/01-power-automate.md): fluxo do e-mail até a API e o Teams
2. [02-document-intelligence.md](docs/02-document-intelligence.md): modelo de extração
3. [03-fabric-powerbi.md](docs/03-fabric-powerbi.md): logs, modelo semântico e medidas
