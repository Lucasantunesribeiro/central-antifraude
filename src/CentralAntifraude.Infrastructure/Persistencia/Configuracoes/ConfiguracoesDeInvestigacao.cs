using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Transacoes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

/// <summary>
/// O caso, sua timeline e suas notas.
///
/// Tres decisoes de esquema sustentam a fase:
///
/// 1. **<c>versao</c> e token de concorrencia.** O <c>UPDATE</c> passa a
///    carregar <c>WHERE versao = @lida</c>, e duas gravacoes simultaneas nao
///    se sobrescrevem: a segunda atinge zero linhas e falha. E a camada que a
///    checagem na aplicacao sozinha nao consegue dar, porque duas requisicoes
///    podem passar juntas por ela.
///
/// 2. **Timeline e notas com <c>Cascade</c>.** Sao partes do caso, e nao
///    entidades de vida propria: nao existe timeline sem caso. Os alertas, ao
///    contrario, existem antes e continuam existindo depois.
///
/// 3. **Sem colecao de navegacao no caso.** A timeline e append-only e nenhuma
///    regra precisa le-la; o repositorio grava as entradas novas
///    explicitamente. Deixar o EF descobri-las por navegacao produzia um
///    defeito silencioso — com a chave gerada pelo dominio, ele concluia que a
///    linha ja existia e emitia <c>UPDATE</c> em vez de <c>INSERT</c>.
/// </summary>
public sealed class ConfiguracaoDeCaso : IEntityTypeConfiguration<Caso>
{
    public void Configure(EntityTypeBuilder<Caso> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("casos");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.OrganizacaoId).IsRequired();
        builder.Property(c => c.AbertoPorId).IsRequired();
        builder.Property(c => c.AbertoEm).IsRequired();
        builder.Property(c => c.AtualizadoEm).IsRequired();

        builder.Property(c => c.Titulo)
            .HasMaxLength(Caso.TamanhoMaximoDoTitulo)
            .IsRequired();

        // Enums como texto: uma consulta manual durante uma investigacao
        // precisa dizer "EmAnalise", e nao "2".
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.Resultado).HasConversion<string>().HasMaxLength(20);

        // O token de concorrencia. Sem ele, dois analistas resolvendo o mesmo
        // caso ao mesmo tempo gravariam resultados diferentes e o ultimo
        // venceria em silencio.
        builder.Property(c => c.Versao).IsRequired().IsConcurrencyToken();
        builder.Property(c => c.TotalDeEventos).IsRequired();

        // O eixo da tela: casos do tenant, por situacao, do mais recentemente
        // tocado para o mais antigo.
        builder.HasIndex(c => new { c.OrganizacaoId, c.Status, c.AtualizadoEm })
            .HasDatabaseName("ix_casos_organizacao_status_atualizado");

        // A fila pessoal: "meus casos".
        builder.HasIndex(c => new { c.OrganizacaoId, c.ResponsavelId })
            .HasDatabaseName("ix_casos_organizacao_responsavel");

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(c => c.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);

        // O autor e o responsavel citam usuarios reais, e o banco recusa
        // apagar quem aparece na historia de uma investigacao.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(c => c.AbertoPorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(c => c.ResponsavelId)
            .OnDelete(DeleteBehavior.Restrict);

        // As entradas de timeline e as notas nao aparecem como colecao aqui:
        // a ligacao e declarada do lado do filho, que e quem carrega a chave.
        builder.Ignore(c => c.NovosEventos);
        builder.Ignore(c => c.NovasNotas);
    }
}

/// <summary>
/// A timeline.
///
/// Somente insercao: nao ha metodo de dominio que altere um evento e nao ha
/// rota que o faca. O indice e por caso e ordem cronologica, que e a unica
/// forma como ela e lida.
/// </summary>
public sealed class ConfiguracaoDeEventoDoCaso : IEntityTypeConfiguration<EventoDoCaso>
{
    public void Configure(EntityTypeBuilder<EventoDoCaso> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("eventos_do_caso");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrganizacaoId).IsRequired();
        builder.Property(e => e.CasoId).IsRequired();
        builder.Property(e => e.AutorId).IsRequired();
        builder.Property(e => e.OcorridoEm).IsRequired();
        builder.Property(e => e.Sequencia).IsRequired();

        builder.Property(e => e.Tipo).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(e => e.AutorDescricao)
            .HasMaxLength(EventoDoCaso.TamanhoMaximoDoAutor)
            .IsRequired();

        builder.Property(e => e.Descricao)
            .HasMaxLength(EventoDoCaso.TamanhoMaximoDaDescricao)
            .IsRequired();

        // Unico: a sequencia e a ordem da trilha, e duas entradas na mesma
        // posicao significariam que a historia se bifurcou.
        builder.HasIndex(e => new { e.CasoId, e.Sequencia })
            .IsUnique()
            .HasDatabaseName("ix_eventos_do_caso_caso_sequencia");

        builder.HasOne<Caso>()
            .WithMany()
            .HasForeignKey(e => e.CasoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// As notas de investigacao.
///
/// Nao ha coluna de "editado em" nem de "apagado em", e a ausencia e a
/// politica: nota publicada nao muda. Uma correcao relevante e uma nota nova,
/// que a timeline registra na ordem em que aconteceu.
/// </summary>
public sealed class ConfiguracaoDeNotaDoCaso : IEntityTypeConfiguration<NotaDoCaso>
{
    public void Configure(EntityTypeBuilder<NotaDoCaso> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notas_do_caso");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.OrganizacaoId).IsRequired();
        builder.Property(n => n.CasoId).IsRequired();
        builder.Property(n => n.AutorId).IsRequired();
        builder.Property(n => n.CriadaEm).IsRequired();

        builder.Property(n => n.AutorDescricao)
            .HasMaxLength(EventoDoCaso.TamanhoMaximoDoAutor)
            .IsRequired();

        // Texto puro, com teto. Nunca renderizado como HTML — a defesa contra
        // XSS e a saida, e nao uma limpeza que alteraria o que o analista
        // escreveu.
        builder.Property(n => n.Conteudo)
            .HasMaxLength(NotaDoCaso.TamanhoMaximoDoConteudo)
            .IsRequired();

        builder.HasIndex(n => new { n.CasoId, n.CriadaEm })
            .HasDatabaseName("ix_notas_do_caso_caso_criada");

        builder.HasOne<Caso>()
            .WithMany()
            .HasForeignKey(n => n.CasoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// O veredito humano por transacao.
///
/// **Uma transacao, um veredito.** A restricao unica e o que impede a mesma
/// transacao de contar duas vezes numa estatistica de backtest — e a Fase 9 vai
/// ler esta tabela como verdade apurada.
/// </summary>
public sealed class ConfiguracaoDeResultadoDeInvestigacao
    : IEntityTypeConfiguration<ResultadoDeInvestigacaoDaTransacao>
{
    /// <summary>Nome citado no diagnostico de conflito e nos testes.</summary>
    public const string RestricaoDeUnicidade = "ix_resultados_de_investigacao_transacao";

    public void Configure(EntityTypeBuilder<ResultadoDeInvestigacaoDaTransacao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("resultados_de_investigacao");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrganizacaoId).IsRequired();
        builder.Property(r => r.TransacaoId).IsRequired();
        builder.Property(r => r.CasoId).IsRequired();
        builder.Property(r => r.RegistradoPorId).IsRequired();
        builder.Property(r => r.RegistradoEm).IsRequired();

        builder.Property(r => r.Resultado).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(r => r.TransacaoId)
            .IsUnique()
            .HasDatabaseName(RestricaoDeUnicidade);

        builder.HasOne<Transacao>()
            .WithMany()
            .HasForeignKey(r => r.TransacaoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Caso>()
            .WithMany()
            .HasForeignKey(r => r.CasoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
