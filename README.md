# API de Chamados + Agente no Copilot Studio

Mini-projeto: API mínima em .NET 10 (chamados de suporte) consumida como ferramenta REST por um agente do Copilot Studio.

## Rodar
```
cd ChamadosApi
dotnet run --urls http://localhost:5092
```
- `GET /chamados[?status=]`, `GET /chamados/{id}`, `POST /chamados`
- OpenAPI 3.0 em `/openapi.json`
- Chave opcional: defina `ApiKey` na configuração para exigir o header `X-Api-Key`.

## Copilot Studio
1. Exponha a API: `devtunnel host -p 5080 --allow-anonymous`
2. Baixe `<URL>/openapi.json` e confirme que `servers.url` aponta para a URL pública.
3. No agente: Tools → Add a tool → New tool → REST API → upload do JSON.

## Swagger
 - incluso documentação swagger http://localhost:5092/swagger
 
