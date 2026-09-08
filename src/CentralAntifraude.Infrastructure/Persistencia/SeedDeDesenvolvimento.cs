using CentralAntifraude.Application.Identidade;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Tempo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CentralAntifraude.Infrastructure.Persistencia;

/// <summary>
/// Cria a organizacao e os usuarios necessarios para operar o sistema em
/// desenvolvimento.
///
/// Tres regras de seguranca:
///
/// 1. **Nunca roda sozinho.** So executa se
///    <c>Seed:SenhaPadrao</c> estiver configurado. Sem isso nao ha seed e nao
///    ha usuario — falha fechada, em vez de criar contas com senha conhecida.
///
/// 2. **A senha nunca esta no codigo.** Ela vem de variavel de ambiente ou
///    user-secrets, do mesmo jeito que a string de conexao.
///
/// 3. **E idempotente.** Rodar de novo nao duplica nem sobrescreve senha de
///    usuario existente.
///
/// A partir da Fase 13 ele tambem chama o <see cref="SeedNarrativo"/>, que
/// cria as historias da demonstracao. A ordem importa: nao ha como abrir um
/// caso sem um analista a quem atribui-lo.
/// </summary>
public static partial class SeedDeDesenvolvimento
{
    public const string ChaveDaSenha = "Seed:SenhaPadrao";

    public const string CodigoDaOrganizacao = "demo";

    private static readonly (string Email, string Nome, PerfilDeUsuario Perfil)[] Usuarios =
    [
        ("admin@demo.local", "Administradora Demo", PerfilDeUsuario.Administrador),
        ("supervisor@demo.local", "Supervisor Demo", PerfilDeUsuario.SupervisorDeFraude),
        ("analista@demo.local", "Analista Demo", PerfilDeUsuario.AnalistaDeFraude),
        ("auditor@demo.local", "Auditor Demo", PerfilDeUsuario.Auditor),
    ];

    public static async Task ExecutarAsync(
        IServiceProvider provedor,
        IConfiguration configuracao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provedor);
        ArgumentNullException.ThrowIfNull(configuracao);

        var log = provedor.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SeedDeDesenvolvimento));
        var senha = configuracao[ChaveDaSenha];

        if (string.IsNullOrWhiteSpace(senha))
        {
            RegistrarSeedIgnorado(log, ChaveDaSenha);
            return;
        }

        var contexto = provedor.GetRequiredService<CentralAntifraudeDbContext>();
        var hash = provedor.GetRequiredService<IHashDeSenha>();
        var relogio = provedor.GetRequiredService<IRelogio>();
        var agora = relogio.Agora;

        var organizacao = await contexto.Organizacoes
            .FirstOrDefaultAsync(o => o.Codigo == CodigoDaOrganizacao, cancellationToken);

        if (organizacao is null)
        {
            organizacao = Organizacao.Criar("Organizacao Demo", CodigoDaOrganizacao, agora);
            contexto.Organizacoes.Add(organizacao);
            await contexto.SaveChangesAsync(cancellationToken);

            RegistrarOrganizacaoCriada(log, CodigoDaOrganizacao);
        }

        // Fora do `if`: uma organizacao criada antes da Fase 3 existe sem
        // perfil de risco, e sem perfil nenhuma transacao dela pode ser
        // avaliada. A chamada e idempotente, entao rodar sempre e seguro.
        if (await ProvisionamentoDeRisco.GarantirCatalogoAsync(
                contexto,
                organizacao.Id,
                agora,
                cancellationToken))
        {
            await contexto.SaveChangesAsync(cancellationToken);

            RegistrarCatalogoProvisionado(log, CatalogoPadraoDeRisco.Definicoes.Count);
        }

        var criados = 0;
        var identidades = new Dictionary<PerfilDeUsuario, (Guid Id, string Nome)>();

        foreach (var (email, nome, perfil) in Usuarios)
        {
            var enderecoNormalizado = Email.De(email);

            // Ignora o filtro de tenant: o seed roda fora de uma requisicao,
            // entao nao ha identidade e o filtro devolveria vazio para tudo.
            var existente = await contexto.Usuarios
                .IgnoreQueryFilters([CentralAntifraudeDbContext.FiltroDeTenant])
                .FirstOrDefaultAsync(u => u.Email == enderecoNormalizado, cancellationToken);

            if (existente is not null)
            {
                identidades[perfil] = (existente.Id, existente.NomeCompleto);
                continue;
            }

            var usuario = Usuario.Criar(
                organizacao.Id,
                enderecoNormalizado,
                nome,
                hash.Gerar(senha),
                perfil,
                agora);

            contexto.Usuarios.Add(usuario);
            identidades[perfil] = (usuario.Id, usuario.NomeCompleto);

            criados++;
        }

        if (criados > 0)
        {
            await contexto.SaveChangesAsync(cancellationToken);

            // A senha nao entra no log. Quem rodou o seed a definiu e ja a tem.
            RegistrarUsuariosCriados(log, criados);
        }

        // As historias da demonstracao. Idempotente por conta propria, entao
        // roda sempre: uma base criada antes da Fase 13 ganha a narrativa na
        // primeira subida depois dela.
        var transacoes = await SeedNarrativo.ExecutarAsync(
            contexto,
            organizacao.Id,
            identidades,
            provedor.GetRequiredService<MotorDeRisco>(),
            agora,
            cancellationToken);

        if (transacoes > 0)
        {
            RegistrarNarrativaCriada(log, transacoes);
        }
    }

    [LoggerMessage(
        EventId = 104,
        Level = LogLevel.Information,
        Message = "Seed narrativo: {Transacoes} transacoes de demonstracao criadas.")]
    private static partial void RegistrarNarrativaCriada(ILogger logger, int transacoes);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Information,
        Message = "Seed de desenvolvimento ignorado: {Chave} nao configurado.")]
    private static partial void RegistrarSeedIgnorado(ILogger logger, string chave);

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Information,
        Message = "Organizacao de desenvolvimento criada: {Codigo}.")]
    private static partial void RegistrarOrganizacaoCriada(ILogger logger, string codigo);

    [LoggerMessage(
        EventId = 102,
        Level = LogLevel.Information,
        Message = "Seed de desenvolvimento criou {Quantidade} usuario(s).")]
    private static partial void RegistrarUsuariosCriados(ILogger logger, int quantidade);

    [LoggerMessage(
        EventId = 103,
        Level = LogLevel.Information,
        Message = "Catalogo de risco provisionado com {Quantidade} regra(s).")]
    private static partial void RegistrarCatalogoProvisionado(ILogger logger, int quantidade);
}
