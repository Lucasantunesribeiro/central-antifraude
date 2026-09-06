using CentralAntifraude.Domain.Alertas;
using CentralAntifraude.Domain.Eventos;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Investigacao;
using CentralAntifraude.Domain.Risco;
using CentralAntifraude.Domain.Transacoes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

/// <summary>
/// A fila operacional de alertas.
///
/// Duas decisoes de esquema sustentam a fase inteira:
///
/// 1. **`avaliacao_id` e unico.** E a invariante propria do efeito que o
///    `CLAUDE.md` secao 42 exige alem da Inbox — "a idempotencia deve existir
///    em mais de uma camada quando o dominio justificar". Aqui o dominio
///    justifica: um alerta duplicado nao e um numero errado num painel, e um
///    analista investigando duas vezes o mesmo caso.
///
/// 2. **Chaves estrangeiras com `RESTRICT` para avaliacao e transacao.** O
///    banco recusa apagar aquilo que o alerta cita. Sem isso, uma limpeza de
///    dados antigos deixaria alertas apontando para o vazio, e a navegacao
///    "alerta -> transacao" quebraria em silencio.
/// </summary>
public sealed class ConfiguracaoDeAlerta : IEntityTypeConfiguration<Alerta>
{
    /// <summary>Nome citado no diagnostico de conflito e nos testes.</summary>
    public const string RestricaoDeUnicidade = "ix_alertas_avaliacao";

    /// <summary>Indice que sustenta a consulta padrao da fila.</summary>
    public const string IndiceDaFila = "ix_alertas_organizacao_status_criado";

    public void Configure(EntityTypeBuilder<Alerta> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("alertas");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.OrganizacaoId).IsRequired();
        builder.Property(a => a.AvaliacaoId).IsRequired();
        builder.Property(a => a.TransacaoId).IsRequired();
        builder.Property(a => a.EventoId).IsRequired();
        builder.Property(a => a.Score).IsRequired();
        builder.Property(a => a.VersaoDaPolitica).IsRequired();
        builder.Property(a => a.AvaliadaEm).IsRequired();
        builder.Property(a => a.CriadoEm).IsRequired();

        // Enums como texto, e nao como numero: uma consulta manual no banco
        // durante uma investigacao precisa dizer "Bloquear", nao "3". Vale o
        // mesmo criterio ja usado na projecao diaria.
        builder.Property(a => a.Decisao).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.Prioridade).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(a => a.IdDeCorrelacao)
            .HasMaxLength(EventoDeSaida.TamanhoMaximoDaCorrelacao)
            .IsRequired();

        // Um alerta por avaliacao. E o banco quem decide, e nao uma consulta
        // previa do worker: duas entregas simultaneas da mesma mensagem
        // competem no INSERT, e uma perde.
        builder.HasIndex(a => a.AvaliacaoId)
            .IsUnique()
            .HasDatabaseName(RestricaoDeUnicidade);

        // O eixo da tela: alertas do tenant, por situacao, do mais recente
        // para o mais antigo (`CLAUDE.md` secao 46). E o unico indice criado
        // alem da restricao — os demais eixos de filtro so ganham indice
        // quando uma consulta real pedir.
        builder.HasIndex(a => new { a.OrganizacaoId, a.Status, a.CriadoEm })
            .HasDatabaseName(IndiceDaFila);

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(a => a.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AvaliacaoDeRisco>()
            .WithMany()
            .HasForeignKey(a => a.AvaliacaoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Transacao>()
            .WithMany()
            .HasForeignKey(a => a.TransacaoId)
            .OnDelete(DeleteBehavior.Restrict);

        // O caso que levou o alerta (Fase 7). Nulo enquanto o alerta esta na
        // fila. `Restrict` porque apagar um caso deixaria alertas apontando
        // para uma investigacao que nao existe mais.
        builder.HasOne<Caso>()
            .WithMany()
            .HasForeignKey(a => a.CasoId)
            .OnDelete(DeleteBehavior.Restrict);

        // A consulta "alertas deste caso", usada no workspace.
        builder.HasIndex(a => a.CasoId).HasDatabaseName("ix_alertas_caso");
    }
}
