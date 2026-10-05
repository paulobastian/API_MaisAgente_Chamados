# 1. Fluxo no Power Automate

Tipo: fluxo de nuvem automatizado. Nome sugerido: `StatusDePedidos - Processar e-mail`.

## Pré-requisitos
- API publicada com URL HTTPS (Azure App Service / Container Apps). Para teste local: `devtunnel host -p 5090 --allow-anonymous`.
- Recurso Azure AI Document Intelligence criado ([02](02-document-intelligence.md)).
- Conexões: Outlook/Office 365, Teams, HTTP.

## Passos do fluxo

1. **Gatilho: Quando um novo e-mail chega (V3)**: Pasta `Entrada/Pedidos`, *Somente com anexo = Sim*, *Incluir anexos = Sim*.
2. **Aplicar a cada** nos `Anexos` do e-mail, filtrando `.pdf`, `.png`, `.jpg` (condição `endsWith(toLower(items('Anexo')?['name']), '.pdf')`).
3. **Extrair** com Document Intelligence:
   - Ação *Analisar documento* (conector Azure AI Document Intelligence) com o modelo (prebuilt-invoice ou modelo personalizado).
   - Conteúdo: `base64ToBinary(items('Anexo')?['contentBytes'])`.
4. **Compor o payload** (ação *Compor*), mapeando para o contrato da API:
   ```json
   {
     "messageId": "@{triggerOutputs()?['body/internetMessageId']}|@{items('Anexo')?['name']}",
     "remetenteEmail": "@{triggerOutputs()?['body/from']}",
     "fornecedor": { "nome": "<VendorName>", "cnpj": "<VendorTaxId>" },
     "numeroPedido": "<PurchaseOrder>",
     "valorTotal": <InvoiceTotal>,
     "itens": [ { "descricao": "<Description>", "quantidade": <Quantity>, "valorUnitario": <UnitPrice> } ],
     "confiancaExtracao": <menor confiança entre os campos>
   }
   ```
   O `messageId` inclui o nome do anexo para que um e-mail com vários anexos gere processamentos distintos.
5. **HTTP**: `POST https://<sua-api>/pedidos/processar`, header `Content-Type: application/json` e `X-Api-Key: <segredo>` (guarde a chave em variável de ambiente da solução, não no fluxo). Configure *Política de nova tentativa* para 3 tentativas exponenciais (a API é idempotente).
6. **Analisar JSON** da resposta com o esquema do `ResultadoProcessamento` (`status`, `numeroPedido`, `divergencias[]`, `sugestaoAcao`, `responsavelEmail`).
7. **Condição**: `status` é igual a `Divergente`?
   - **Sim**: ação *Postar cartão adaptável e aguardar resposta* no Teams (usuário = `responsavelEmail`):
     - Título: `Divergência no pedido <numeroPedido>`
     - Lista das `divergencias[].detalhe`
     - **Sugestão de ação (IA):** `sugestaoAcao`
     - Botões: *Aprovar mesmo assim*, *Rejeitar*, *Pedir correção ao fornecedor*
     - Depois da resposta, responda ao fornecedor por e-mail ou registre a decisão.
   - **Não**: segue (já gravado no ERP ou duplicado).
8. **Tratamento de erro**: escopo *Try* com os passos 3 a 7 e escopo *Catch* (executar após *falhou/expirou*) que notifica a TI no Teams com o link da execução. A API também registra o erro no log.

## Alternativa: agente no Copilot Studio
Em vez do cartão adaptável, um agente pode receber a divergência e conversar com o responsável (ex.: "aprove o pedido PED-1002"). Nesse caso, o fluxo chama o agente pelo conector do Copilot Studio e a API pode ganhar um endpoint `POST /pedidos/{numero}/decisao`. Ainda não existe na API.
