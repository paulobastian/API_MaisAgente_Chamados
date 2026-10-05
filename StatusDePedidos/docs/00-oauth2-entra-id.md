# 0. Autenticação OAuth2 (Microsoft Entra ID)

A API valida **access tokens JWT** emitidos pelo Microsoft Entra ID. O Power Automate (ou qualquer sistema) se autentica como **aplicação**, com o fluxo *client credentials*, e não em nome de um usuário.

```
Power Automate ──(client id + secret)──► Entra ID ──► access token (JWT)
Power Automate ──(Authorization: Bearer <token>)──► API StatusDePedidos
```

## Autorização (app roles)

| Role | Permite |
|---|---|
| `Pedidos.Processar` | `POST /pedidos/processar` e `GET /metricas` |
| `Pedidos.Ler` | somente `GET /metricas` |

`/health`, `/openapi.json` e `/swagger` são públicos. Os nomes das roles estão em `Roles` ([Models.cs](../src/StatusDePedidos.Api/Models.cs)).

## Configuração no Entra ID

### 1. App registration da API
1. Portal Azure → *Microsoft Entra ID* → *App registrations* → **New registration**, com o nome `StatusDePedidos API` (single tenant).
2. *Expose an API* → defina o **Application ID URI** (ex.: `api://statusdepedidos`).
3. *App roles* → **Create app role** (tipo *Applications*), duas vezes:
   - `Pedidos.Processar`, valor `Pedidos.Processar`
   - `Pedidos.Ler`, valor `Pedidos.Ler`
4. *Manifest*: confira `"requestedAccessTokenVersion": 2`, para tokens v2.0 (issuer `.../{tenant}/v2.0`).

### 2. App registration do cliente (Power Automate)
1. **New registration**: `StatusDePedidos Cliente`.
2. *Certificates & secrets* → **New client secret**. Copie o valor na hora e guarde no Key Vault. Prefira certificado em produção.
3. *API permissions* → **Add a permission** → *My APIs* → `StatusDePedidos API` → **Application permissions** → marque `Pedidos.Processar` → **Grant admin consent**.

## Configuração da API

| Chave | Valor |
|---|---|
| `Auth:Authority` | `https://login.microsoftonline.com/{tenantId}/v2.0` |
| `Auth:Audience` | O Application ID URI ou o client id da API (ex.: `api://statusdepedidos`) |

Em produção use variáveis de ambiente (`Auth__Authority`, `Auth__Audience`) ou App Settings do Azure.

**Regras de segurança do código**
- Fora de `Development`, sem `Auth:Authority` a API **não sobe**. Ela nunca fica aberta por engano.
- Em `Development` sem `Auth:Authority`, a API fica aberta para facilitar testes locais.
- O `issuer`, o `audience`, a assinatura e a validade do token são validados. Sem token o resultado é 401; com token mas sem a role, 403.

## Obter um token (teste manual)
```powershell
$body = @{
  grant_type    = "client_credentials"
  client_id     = "<client id do StatusDePedidos Cliente>"
  client_secret = "<secret>"
  scope         = "api://statusdepedidos/.default"
}
$t = Invoke-RestMethod -Method Post -Body $body `
  -Uri "https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token"
Invoke-RestMethod http://localhost:5090/metricas -Headers @{ Authorization = "Bearer $($t.access_token)" }
```
Para decodificar o token e conferir `aud`, `iss` e `roles`, use https://jwt.ms.

## Swagger
Em `/swagger`, clique em **Authorize**, cole o `access_token` (sem o prefixo `Bearer`) e execute as chamadas.

## No Power Automate
Na ação **HTTP**, em *Autenticação* escolha **Active Directory OAuth**:
- Authority: `https://login.microsoftonline.com`
- Tenant: `<tenantId>`
- Audience: `api://statusdepedidos`
- Client ID e Secret: os do app `StatusDePedidos Cliente` (use variáveis de ambiente da solução ligadas ao Key Vault)
- Tipo de credencial: Secret

O Power Automate obtém e renova o token sozinho.

## Testes automatizados
[tests/StatusDePedidos.Tests](../tests/StatusDePedidos.Tests) sobe a API com uma chave de assinatura local e cobre: rota pública, sem token (401), token sem role (403), role de leitura tentando processar (403), role correta (200), audience errada (401) e token expirado (401).
```
dotnet test tests/StatusDePedidos.Tests
```
