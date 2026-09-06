using System.Text.Json;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

/// <summary>
/// Serializa e reconstroi a configuracao tipada de uma regra.
///
/// **Isto nao e uma DSL.** O JSON gravado contem apenas numeros e o nome do
/// tipo — nunca expressao a ser interpretada. Na leitura, o campo
/// <c>tipo</c> escolhe qual record concreto desserializar, e o <c>switch</c>
/// abaixo e a lista fechada do que pode existir: um valor desconhecido falha
/// alto, em vez de virar algo executavel (CLAUDE.md secao 21).
///
/// O tipo viaja DENTRO do JSON porque o conversor do EF Core so enxerga a
/// propria coluna — ele nao pode consultar a coluna vizinha para decidir.
/// </summary>
public static class SerializadorDeConfiguracaoDeRegra
{
    private const string CampoDoTipo = "tipo";

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serializar(ConfiguracaoDeRegra configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        var corpo = JsonSerializer.SerializeToNode(configuracao, configuracao.GetType(), Opcoes)!
            .AsObject();

        corpo[CampoDoTipo] = configuracao.Tipo.ToString();

        return corpo.ToJsonString(Opcoes);
    }

    public static ConfiguracaoDeRegra Desserializar(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var documento = JsonDocument.Parse(json);

        if (!documento.RootElement.TryGetProperty(CampoDoTipo, out var tipoBruto) ||
            !Enum.TryParse<TipoDeRegra>(tipoBruto.GetString(), ignoreCase: false, out var tipo) ||
            !Enum.IsDefined(tipo))
        {
            throw new InvalidOperationException(
                "Configuracao de regra sem tipo reconhecido. O catalogo de tipos e fechado.");
        }

        // Lista fechada. Um tipo novo exige codigo novo aqui - que e
        // exatamente a barreira que impede configuracao vinda do banco de
        // virar comportamento arbitrario.
        ConfiguracaoDeRegra? configuracao = tipo switch
        {
            TipoDeRegra.VelocidadePorCliente =>
                JsonSerializer.Deserialize<ConfiguracaoDeVelocidade>(json, Opcoes),
            TipoDeRegra.NovoDispositivo =>
                JsonSerializer.Deserialize<ConfiguracaoDeNovoDispositivo>(json, Opcoes),
            TipoDeRegra.ValorAcimaDoHistorico =>
                JsonSerializer.Deserialize<ConfiguracaoDeValorAcimaDoHistorico>(json, Opcoes),
            TipoDeRegra.DivergenciaGeografica =>
                JsonSerializer.Deserialize<ConfiguracaoDeDivergenciaGeografica>(json, Opcoes),
            _ => null,
        };

        return configuracao
            ?? throw new InvalidOperationException(
                $"Nao ha contrato de configuracao para o tipo de regra {tipo}.");
    }
}

public sealed class ConfiguracaoDeRegraEntidade : IEntityTypeConfiguration<Regra>
{
    /// <summary>Nome unico por organizacao — citado no diagnostico de conflito.</summary>
    public const string RestricaoDeNomeUnico = "ix_regras_organizacao_nome";

    public void Configure(EntityTypeBuilder<Regra> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("regras");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrganizacaoId).IsRequired();
        builder.Property(r => r.Nome).HasMaxLength(Regra.TamanhoMaximoDoNome).IsRequired();
        builder.Property(r => r.CriadaEm).IsRequired();
        builder.Property(r => r.AtualizadaEm).IsRequired();
        builder.Property(r => r.Ativa).IsRequired();
        builder.Property(r => r.NumeroDaUltimaVersao).IsRequired();

        builder.Property(r => r.Tipo)
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        // O rascunho mora na regra, e nao numa tabela propria: ele e um
        // estado da regra — no maximo um por vez — e nao uma entidade com
        // vida propria. Uma tabela separada exigiria join em toda leitura
        // administrativa para responder "tem rascunho?".
        builder.Property(r => r.ConfiguracaoEmRascunho)
            .HasConversion(
                configuracao => SerializadorDeConfiguracaoDeRegra.Serializar(configuracao!),
                json => SerializadorDeConfiguracaoDeRegra.Desserializar(json))
            .HasColumnType("jsonb");

        builder.Property(r => r.PontosEmRascunho);

        // Token de concorrencia administrativa (ROADMAP 8.6). O UPDATE sai
        // com `WHERE versao = @lida`: dois supervisores editando a mesma
        // regra ao mesmo tempo nao se sobrescrevem em silencio.
        builder.Property(r => r.Versao).IsRequired().IsConcurrencyToken();

        // Nome unico por organizacao. Desde a Fase 8 o tipo ja nao distingue
        // duas regras — o Supervisor pode ter duas velocidades com janelas
        // diferentes — entao o nome e o que resta para a lista, a auditoria e
        // a explicacao de um sinal nao ficarem ambiguas.
        builder.HasIndex(r => new { r.OrganizacaoId, r.Nome })
            .IsUnique()
            .HasDatabaseName(RestricaoDeNomeUnico);

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(r => r.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ConfiguracaoDeVersaoDeRegra : IEntityTypeConfiguration<VersaoDeRegra>
{
    /// <summary>Um numero de versao por regra — citado no diagnostico de conflito.</summary>
    public const string RestricaoDeNumeroUnico = "ix_versoes_de_regra_regra_numero";

    public void Configure(EntityTypeBuilder<VersaoDeRegra> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("versoes_de_regra");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.OrganizacaoId).IsRequired();
        builder.Property(v => v.RegraId).IsRequired();
        builder.Property(v => v.Numero).IsRequired();
        builder.Property(v => v.Pontos).IsRequired();
        builder.Property(v => v.PublicadaEm).IsRequired();

        builder.Property(v => v.Tipo)
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(v => v.Configuracao)
            .HasConversion(
                configuracao => SerializadorDeConfiguracaoDeRegra.Serializar(configuracao),
                json => SerializadorDeConfiguracaoDeRegra.Desserializar(json))
            .HasColumnType("jsonb")
            .IsRequired();

        // Uma versao publicada nao muda (CLAUDE.md secao 23). O numero e
        // unico dentro da regra: publicar duas vezes a "versao 2" tornaria a
        // referencia historica ambigua.
        //
        // Desde a Fase 8 este indice tambem arbitra concorrencia: duas
        // publicacoes simultaneas da mesma regra calculam o mesmo numero, e
        // quem perde esbarra aqui.
        builder.HasIndex(v => new { v.RegraId, v.Numero })
            .IsUnique()
            .HasDatabaseName(RestricaoDeNumeroUnico);

        builder.HasOne<Regra>()
            .WithMany()
            .HasForeignKey(v => v.RegraId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ConfiguracaoDePerfilDeRisco : IEntityTypeConfiguration<PerfilDeRisco>
{
    public void Configure(EntityTypeBuilder<PerfilDeRisco> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("perfis_de_risco");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.OrganizacaoId).IsRequired();
        builder.Property(p => p.Nome).HasMaxLength(PerfilDeRisco.TamanhoMaximoDoNome).IsRequired();
        builder.Property(p => p.CriadoEm).IsRequired();

        builder.HasIndex(p => p.OrganizacaoId);

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(p => p.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ConfiguracaoDeVersaoDePerfil : IEntityTypeConfiguration<VersaoDePerfilDeRisco>
{
    /// <summary>Um numero de versao por perfil — citado no diagnostico de conflito.</summary>
    public const string RestricaoDeVersaoDePerfilUnica = "ix_versoes_de_perfil_perfil_numero";

    public void Configure(EntityTypeBuilder<VersaoDePerfilDeRisco> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("versoes_de_perfil_de_risco");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.OrganizacaoId).IsRequired();
        builder.Property(v => v.PerfilId).IsRequired();
        builder.Property(v => v.Numero).IsRequired();
        builder.Property(v => v.LimiarDeRevisao).IsRequired();
        builder.Property(v => v.LimiarDeBloqueio).IsRequired();
        builder.Property(v => v.PublicadaEm).IsRequired();

        // Muitos-para-muitos: a mesma versao de regra pode participar de
        // varias versoes de perfil. E o que permite publicar um perfil novo
        // reaproveitando regras que nao mudaram.
        builder.HasMany(v => v.VersoesDeRegra)
            .WithMany()
            .UsingEntity(juncao => juncao.ToTable("versoes_de_perfil_regras"));

        // Mesma funcao dupla do indice de versao de regra: garante a
        // referencia historica e arbitra duas publicacoes simultaneas.
        builder.HasIndex(v => new { v.PerfilId, v.Numero })
            .IsUnique()
            .HasDatabaseName(RestricaoDeVersaoDePerfilUnica);

        // Busca da versao vigente: a mais recente do tenant.
        builder.HasIndex(v => new { v.OrganizacaoId, v.PublicadaEm });

        builder.HasOne<PerfilDeRisco>()
            .WithMany()
            .HasForeignKey(v => v.PerfilId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ConfiguracaoDeAvaliacaoDeRisco : IEntityTypeConfiguration<AvaliacaoDeRisco>
{
    /// <summary>Uma avaliacao por transacao — nome citado no diagnostico de conflito.</summary>
    public const string RestricaoDeAvaliacaoUnica = "ix_avaliacoes_de_risco_transacao";

    public void Configure(EntityTypeBuilder<AvaliacaoDeRisco> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("avaliacoes_de_risco");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.OrganizacaoId).IsRequired();
        builder.Property(a => a.TransacaoId).IsRequired();
        builder.Property(a => a.VersaoDePerfilId).IsRequired();
        builder.Property(a => a.NumeroDaVersaoDePerfil).IsRequired();
        builder.Property(a => a.Score).IsRequired();
        builder.Property(a => a.AvaliadaEm).IsRequired();

        builder.Property(a => a.Decisao)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(a => a.VersaoDoMotorUsada).HasMaxLength(20).IsRequired();

        builder.HasMany(a => a.Sinais)
            .WithOne()
            .HasForeignKey(s => s.AvaliacaoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(a => a.Sinais).AutoInclude();

        // Uma transacao tem UMA avaliacao. Duas avaliacoes da mesma transacao
        // significariam duas decisoes concorrentes, e nenhuma tela saberia
        // qual mostrar. A garantia esta no banco, e nao so no codigo: e ela
        // que sobrevive a concorrencia.
        builder.HasIndex(a => a.TransacaoId)
            .IsUnique()
            .HasDatabaseName(RestricaoDeAvaliacaoUnica);

        // Painel da Fase 10: decisoes por periodo, dentro do tenant.
        builder.HasIndex(a => new { a.OrganizacaoId, a.Decisao, a.AvaliadaEm });

        builder.HasOne<Transacao>()
            .WithMany()
            .HasForeignKey(a => a.TransacaoId)
            .OnDelete(DeleteBehavior.Cascade);

        // A versao de perfil usada e amarrada no banco, com Restrict.
        //
        // Isto e a explicabilidade historica virando restricao (CLAUDE.md
        // secao 17): enquanto existir uma avaliacao que aponta para esta
        // versao, o banco recusa apaga-la. Sem a chave estrangeira, uma
        // limpeza futura poderia deixar avaliacoes apontando para uma versao
        // que nao existe mais - e a pergunta "por que esta decisao foi tomada"
        // ficaria sem resposta.
        builder.HasOne<VersaoDePerfilDeRisco>()
            .WithMany()
            .HasForeignKey(a => a.VersaoDePerfilId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ConfiguracaoDeSinalDeRisco : IEntityTypeConfiguration<SinalDeRisco>
{
    private static readonly JsonSerializerOptions OpcoesDaEvidencia = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Configure(EntityTypeBuilder<SinalDeRisco> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sinais_de_risco");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizacaoId).IsRequired();
        builder.Property(s => s.AvaliacaoId).IsRequired();
        builder.Property(s => s.RegraId).IsRequired();
        builder.Property(s => s.VersaoDeRegraId).IsRequired();
        builder.Property(s => s.NumeroDaVersaoDeRegra).IsRequired();
        builder.Property(s => s.Pontos).IsRequired();

        builder.Property(s => s.Tipo)
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(s => s.Explicacao)
            .HasMaxLength(SinalDeRisco.TamanhoMaximoDaExplicacao)
            .IsRequired();

        // Evidencia como jsonb: pares nome/valor que sustentam a explicacao.
        // O comparador de valor e explicito porque dicionario e tipo mutavel -
        // sem ele, o EF nao detectaria mudanca nem saberia clonar.
        builder.Property(s => s.DadosDaEvidencia)
            .HasConversion(
                dados => JsonSerializer.Serialize(dados, OpcoesDaEvidencia),
                json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, OpcoesDaEvidencia)!,
                new ValueComparer<IReadOnlyDictionary<string, string>>(
                    (esquerda, direita) => JsonSerializer.Serialize(esquerda, OpcoesDaEvidencia)
                        == JsonSerializer.Serialize(direita, OpcoesDaEvidencia),
                    dados => JsonSerializer.Serialize(dados, OpcoesDaEvidencia).GetHashCode(StringComparison.Ordinal),
                    dados => JsonSerializer.Deserialize<Dictionary<string, string>>(
                        JsonSerializer.Serialize(dados, OpcoesDaEvidencia), OpcoesDaEvidencia)!))
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasIndex(s => s.AvaliacaoId);

        // Mesma razao da versao de perfil: o sinal precisa continuar
        // explicavel pela versao de regra que o produziu, e o banco garante
        // que ela nao pode desaparecer. A regra em si nao ganha chave propria
        // porque a versao ja aponta para ela - dois indices para amarrar o
        // mesmo fato seriam indice preventivo (CLAUDE.md secao 46).
        builder.HasOne<VersaoDeRegra>()
            .WithMany()
            .HasForeignKey(s => s.VersaoDeRegraId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
