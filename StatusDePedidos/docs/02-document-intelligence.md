# 2. Azure AI Document Intelligence

## Criar o recurso
1. Portal Azure → *Criar recurso* → **Document Intelligence**.
2. Região próxima (ex.: Brazil South) e SKU **S0** (o F0 limita a 2 páginas e 500 chamadas/mês).
3. Guarde *Endpoint* e *Chave* (Key Vault). O conector do Power Automate pede os dois.

## Escolher o modelo
- **Comece com `prebuilt-invoice`**: já extrai `VendorName`, `VendorTaxId`, `InvoiceTotal`, `PurchaseOrder` e `Items` (descrição, quantidade, preço unitário).
- Se os documentos forem pedidos com layout próprio, treine um **modelo personalizado** no Document Intelligence Studio:
  1. Reúna 5 a 10 documentos por layout.
  2. Rotule os campos: `fornecedor_nome`, `fornecedor_cnpj`, `numero_pedido`, `valor_total` e a tabela de itens.
  3. Treine e teste. Use o ID do modelo na ação do fluxo.

## Confiança
Cada campo vem com `confidence` (0 a 1). O fluxo envia o **menor valor** em `confiancaExtracao`; abaixo de 0,80 (configurável na API) o documento vira divergência para revisão humana, o que evita gravar dados mal lidos no ERP.

## Pontos de atenção
- CNPJ: a API remove pontuação, então `11.222.333/0001-81` e `11222333000181` são equivalentes.
- Valores em formato brasileiro (`1.500,00`): confira se o campo vem como número. Se vier como texto, converta no fluxo antes de enviar.
- Dados pessoais: os documentos podem conter dados sensíveis. Defina retenção e acesso conforme a LGPD.
