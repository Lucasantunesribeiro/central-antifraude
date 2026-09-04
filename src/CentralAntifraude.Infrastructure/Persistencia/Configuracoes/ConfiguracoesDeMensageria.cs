using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Operacao;
using CentralAntifraude.Infrastructure.Mensageria;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

/// <summary>
/// A Inbox.
///
/// A restricao unica de (consumidor, evento) e o mecanismo inteiro: e ela que
/// decide se o efeito acontece, e nao uma consulta previa. Duas entregas
/// simultaneas da mesma mensagem competem no INSERT, e uma perde.
/// </summary>
public sealed class ConfiguracaoDeEventoProcessado : IEntityTypeConfiguration<EventoProcessado>
{
    /// <summary>Nome citado no diagnostico de conflito.</summary>
    public const string RestricaoDeUnicidade = "ix_eventos_processados_consumidor_evento";

    public void Configure(EntityTypeBuilder<EventoProcessado> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("eventos_processados");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrganizacaoId).IsRequired();
        builder.Property(e => e.EventoId).IsRequired();
        builder.Property(e => e.ProcessadoEm).IsRequired();

        builder.Property(e => e.Consumidor)
            .HasMaxLength(EventoProcessado.TamanhoMaximoDoConsumidor)
            .IsRequired();

        builder.Property(e => e.TipoDoEvento)
            .HasMaxLength(EventoDeSaida.TamanhoMaximoDoTipo)
            .IsRequired();

        builder.Property(e => e.IdDeCorrelacao)
            .HasMaxLength(EventoDeSaida.TamanhoMaximoDaCorrelacao)
            .IsRequired();

        // A chave e (consumidor, evento), e nao so o evento: dois consumidores
        // precisam tratar a mesma mensagem, cada um uma vez. Marcar so pelo
        // evento faria o segundo achar que o trabalho dele ja foi feito.
        builder.HasIndex(e => new { e.Consumidor, e.EventoId })
            .IsUnique()
            .HasDatabaseName(RestricaoDeUnicidade);

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(e => e.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Projecao operacional: decisoes por dia, por organizacao.</summary>
public sealed class ConfiguracaoDeResumoDiario : IEntityTypeConfiguration<ResumoDiarioDeDecisoes>
{
    /// <summary>Chave do upsert do worker. Citada no SQL do incremento.</summary>
    public const string RestricaoDeUnicidade = "ix_resumo_diario_organizacao_dia_decisao";

    public void Configure(EntityTypeBuilder<ResumoDiarioDeDecisoes> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("resumo_diario_de_decisoes");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrganizacaoId).IsRequired();
        builder.Property(r => r.Dia).IsRequired();
        builder.Property(r => r.Quantidade).IsRequired();
        builder.Property(r => r.AtualizadoEm).IsRequired();

        builder.Property(r => r.Decisao)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Sem esta restricao o `ON CONFLICT` do worker nao teria alvo, e dois
        // workers simultaneos criariam duas linhas para o mesmo dia — cada uma
        // com metade da contagem.
        builder.HasIndex(r => new { r.OrganizacaoId, r.Dia, r.Decisao })
            .IsUnique()
            .HasDatabaseName(RestricaoDeUnicidade);

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(r => r.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// A fila operacional.
///
/// Nao tem organizacao e nao tem filtro de tenant, de proposito: e a
/// simulacao de um servico externo, e o SQS tambem nao sabe o que e um tenant.
/// O isolamento acontece no consumidor, que confere o tenant declarado contra
/// o dado antes de aplicar qualquer efeito.
/// </summary>
public sealed class ConfiguracaoDeMensagemDaFila : IEntityTypeConfiguration<MensagemDaFila>
{
    public void Configure(EntityTypeBuilder<MensagemDaFila> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("fila_de_mensagens");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Fila).HasMaxLength(60).IsRequired();

        // Texto, e nao `jsonb`. O corpo de uma mensagem de fila e uma cadeia
        // de bytes opaca — para o SQS tambem. Guardar como `jsonb` deixaria a
        // fila mais rigida do que o servico que ela simula em duas frentes:
        // um corpo corrompido seria recusado pelo BANCO em vez de virar
        // mensagem envenenada para o consumidor tratar, e um corpo valido
        // voltaria reordenado, escondendo qualquer teste sobre o formato de
        // fio exato.
        builder.Property(m => m.Corpo).IsRequired();

        builder.Property(m => m.DisponivelEm).IsRequired();
        builder.Property(m => m.Recebimentos).IsRequired();
        builder.Property(m => m.InseridaEm).IsRequired();
        builder.Property(m => m.Recibo);

        // A consulta de recebimento: por fila, o que ja esta disponivel,
        // mais antigo primeiro.
        builder.HasIndex(m => new { m.Fila, m.DisponivelEm });

        // O recibo identifica a ENTREGA. Unico para que um recibo antigo nao
        // possa apagar a entrega atual de outra mensagem.
        builder.HasIndex(m => m.Recibo).IsUnique().HasFilter("recibo IS NOT NULL");
    }
}

/// <summary>
/// A fila de mortas.
///
/// Guardar, e nao descartar: uma mensagem que falhou cinco vezes e a evidencia
/// de um defeito, e apagar em silencio destruiria justamente o que permite
/// entender o que aconteceu (CLAUDE.md secao 72).
/// </summary>
public sealed class ConfiguracaoDeMensagemMorta : IEntityTypeConfiguration<MensagemMorta>
{
    public void Configure(EntityTypeBuilder<MensagemMorta> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("mensagens_mortas");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Fila).HasMaxLength(60).IsRequired();

        // Texto, pela mesma razao da fila viva - e com um motivo a mais: o que
        // chega aqui costuma ser exatamente o corpo que NAO era JSON valido.
        builder.Property(m => m.Corpo).IsRequired();

        builder.Property(m => m.Recebimentos).IsRequired();
        builder.Property(m => m.InseridaEm).IsRequired();
        builder.Property(m => m.MovidaEm).IsRequired();
        builder.Property(m => m.Motivo).HasMaxLength(500).IsRequired();

        builder.HasIndex(m => new { m.Fila, m.MovidaEm });
    }
}
