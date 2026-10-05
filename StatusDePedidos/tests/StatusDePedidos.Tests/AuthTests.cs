using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using StatusDePedidos.Api;

namespace StatusDePedidos.Tests;

/// <summary>Sobe a API com autenticação ativa e uma chave de assinatura local (sem Entra ID).</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://login.test/tenant/v2.0";
    public const string Audience = "api://statusdepedidos";
    public static readonly SymmetricSecurityKey Key = new(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()) { KeyId = "test-key" };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:Authority", Issuer);
        builder.UseSetting("Auth:Audience", Audience);
        builder.UseSetting("Logs:Caminho", Path.Combine(Path.GetTempPath(), $"eventos-{Guid.NewGuid():N}.jsonl"));
        builder.ConfigureServices(s => s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            var cfg = new OpenIdConnectConfiguration { Issuer = Issuer };
            cfg.SigningKeys.Add(Key);
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(cfg); // sem buscar metadados na internet
            o.TokenValidationParameters.ValidIssuer = Issuer;
        }));
    }

    public static string Token(string[]? roles = null, string audience = Audience, DateTime? expires = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = "app-1" };
        if (roles is not null) claims["roles"] = roles;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            NotBefore = expires is null ? null : expires.Value.AddMinutes(-20),
            IssuedAt = expires is null ? null : expires.Value.AddMinutes(-20),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)
        });
    }
}

public class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly object Doc = new
    {
        messageId = "t-1",
        fornecedor = new { nome = "Alfa", cnpj = "11222333000181" },
        numeroPedido = "PED-1001",
        valorTotal = 1500.00,
        itens = new[] { new { descricao = "Cabo", quantidade = 10, valorUnitario = 150 } },
        confiancaExtracao = 0.97
    };

    private HttpClient Cliente(string? token)
    {
        var c = factory.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Health_e_openapi_sao_publicos()
    {
        var c = Cliente(null);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/openapi.json")).StatusCode);
    }

    [Fact]
    public async Task Sem_token_retorna_401()
    {
        var r = await Cliente(null).PostAsJsonAsync("/pedidos/processar", Doc);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Token_sem_role_retorna_403()
    {
        var r = await Cliente(ApiFactory.Token(roles: [])).PostAsJsonAsync("/pedidos/processar", Doc);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Role_de_leitura_nao_pode_processar()
    {
        var r = await Cliente(ApiFactory.Token([Roles.Ler])).PostAsJsonAsync("/pedidos/processar", Doc);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Token_com_role_correta_processa()
    {
        var r = await Cliente(ApiFactory.Token([Roles.Processar])).PostAsJsonAsync("/pedidos/processar", Doc);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Audience_errada_retorna_401()
    {
        var r = await Cliente(ApiFactory.Token([Roles.Processar], audience: "api://outra")).PostAsJsonAsync("/pedidos/processar", Doc);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Token_expirado_retorna_401()
    {
        var token = ApiFactory.Token([Roles.Processar], expires: DateTime.UtcNow.AddHours(-1));
        var r = await Cliente(token).PostAsJsonAsync("/pedidos/processar", Doc);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Metricas_aceita_role_de_leitura_e_exige_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Cliente(null).GetAsync("/metricas")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Cliente(ApiFactory.Token([Roles.Ler])).GetAsync("/metricas")).StatusCode);
    }
}
