using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Integracoes;
using CentralAntifraude.Domain.Transacoes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

public sealed class ConfiguracaoDeIntegracao : IEntityTypeConfiguration<Integracao>
{
    public void Configure(EntityTypeBuilder<Integracao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("integracoes");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.OrganizacaoId).IsRequired();
        builder.Property(i => i.Nome).HasMaxLength(Integracao.TamanhoMaximoDoNome).IsRequired();
        builder.Property(i => i.Ativa).IsRequired();
        builder.Property(i => i.CriadaEm).IsRequired();
        builder.Property(i => i.AtualizadaEm).IsRequired();

        // Listagem administrativa: sempre por tenant, ordenada por nome.
        builder.HasIndex(i => new { i.OrganizacaoId, i.Nome });

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(i => i.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ConfiguracaoDeCredencialDeIntegracao
    : IEntityTypeConfiguration<CredencialDeIntegracao>
{
    public void Configure(EntityTypeBuilder<CredencialDeIntegracao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("credenciais_de_integracao");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.OrganizacaoId).IsRequired();
        builder.Property(c => c.IntegracaoId).IsRequired();

        builder.Property(c => c.IdentificadorPublico)
            .HasMaxLength(CredencialDeIntegracao.TamanhoDoIdentificadorPublico)
            .IsRequired();

        // 64 caracteres: SHA-256 em hexadecimal.
        builder.Property(c => c.HashDoSegredo).HasMaxLength(64).IsRequired();

        builder.Property(c => c.CriadaEm).IsRequired();
        builder.Property(c => c.UsadaPelaUltimaVezEm);
        builder.Property(c => c.RevogadaEm);

        builder.Property(c => c.MotivoDaRevogacao)
            .HasConversion<string>()
            .HasMaxLength(40);

        // A autenticacao busca sempre por este valor, e ele precisa ser unico
        // globalmente: duas credenciais com o mesmo identificador publico
        // tornariam a autenticacao ambigua entre tenants.
        builder.HasIndex(c => c.IdentificadorPublico).IsUnique();

        builder.HasIndex(c => c.IntegracaoId);

        builder.HasOne<Integracao>()
            .WithMany()
            .HasForeignKey(c => c.IntegracaoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ConfiguracaoDeTransacao : IEntityTypeConfiguration<Transacao>
{
    /// <summary>
    /// Nome da restricao que garante uma transacao por chave de idempotencia.
    /// Citado no codigo que traduz a violacao, para que o diagnostico nao
    /// dependa de comparar mensagem do banco.
    /// </summary>
    public const string RestricaoDeIdempotencia = "ix_transacoes_idempotencia";

    /// <summary>Restricao de negocio: uma transacao por identificador de origem.</summary>
    public const string RestricaoDeIdentificadorExterno = "ix_transacoes_identificador_externo";

    public void Configure(EntityTypeBuilder<Transacao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("transacoes");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.OrganizacaoId).IsRequired();
        builder.Property(t => t.IntegracaoId).IsRequired();

        builder.Property(t => t.IdentificadorExterno)
            .HasMaxLength(Transacao.TamanhoMaximoDeIdentificadorExterno)
            .IsRequired();

        // Dinheiro como tipo complexo: duas colunas na mesma tabela, sem join
        // e sem identidade propria - que e exatamente a semantica de um valor.
        builder.ComplexProperty(t => t.Valor, valor =>
        {
            valor.Property(v => v.Valor).HasColumnName("valor").HasPrecision(18, 4).IsRequired();
            valor.Property(v => v.Moeda).HasColumnName("moeda").HasMaxLength(3).IsRequired();
        });

        builder.Property(t => t.OcorridaEm).IsRequired();
        builder.Property(t => t.RecebidaEm).IsRequired();

        builder.Property(t => t.ClienteExternoId)
            .HasMaxLength(Transacao.TamanhoMaximoDeIdentificadorExterno)
            .IsRequired();

        builder.Property(t => t.ReferenciaDoInstrumento)
            .HasMaxLength(Transacao.TamanhoMaximoDeIdentificadorExterno)
            .IsRequired();

        builder.Property(t => t.FingerprintDoDispositivo)
            .HasMaxLength(Transacao.TamanhoMaximoDeFingerprint);

        // HMAC-SHA256 em hexadecimal. O endereco IP nunca tem coluna.
        builder.Property(t => t.FingerprintDoIp).HasMaxLength(64);

        builder.Property(t => t.PaisDeOrigem).HasMaxLength(2);

        builder.Property(t => t.ChaveDeIdempotencia)
            .HasMaxLength(Transacao.TamanhoMaximoDeIdentificadorExterno)
            .IsRequired();

        builder.Property(t => t.FingerprintDoPayload)
            .HasMaxLength(Transacao.TamanhoMaximoDeFingerprint)
            .IsRequired();

        // ------------------------------------------------------------------
        // As duas barreiras de idempotencia (CLAUDE.md secao 33).
        //
        // Elas vivem NO BANCO, e nao apenas no codigo, porque sao a unica
        // garantia que sobrevive a concorrencia. Duas requisicoes simultaneas
        // passam as duas pela consulta previa; so uma vence o INSERT.
        // ------------------------------------------------------------------
        builder.HasIndex(t => new { t.OrganizacaoId, t.IntegracaoId, t.ChaveDeIdempotencia })
            .IsUnique()
            .HasDatabaseName(RestricaoDeIdempotencia);

        builder.HasIndex(t => new { t.OrganizacaoId, t.IntegracaoId, t.IdentificadorExterno })
            .IsUnique()
            .HasDatabaseName(RestricaoDeIdentificadorExterno);

        // Consulta operacional da Fase 3 em diante: historico de um cliente
        // dentro de uma janela de tempo. E a forma da regra de velocidade.
        builder.HasIndex(t => new { t.OrganizacaoId, t.ClienteExternoId, t.OcorridaEm });

        builder.Property(t => t.IdDeCorrelacao)
            .HasMaxLength(Transacao.TamanhoMaximoDeCorrelacao);

        // Listagem por tenant, mais recentes primeiro.
        builder.HasIndex(t => new { t.OrganizacaoId, t.RecebidaEm });

        // Console de transacoes da Fase 10: o filtro de periodo padrao e por
        // OcorridaEm, e sem este indice ele varre a tabela do tenant inteiro.
        // Nao e indice preventivo — e a consulta que a tela faz em todo
        // carregamento (CLAUDE.md secao 46).
        builder.HasIndex(t => new { t.OrganizacaoId, t.OcorridaEm });

        builder.HasOne<Integracao>()
            .WithMany()
            .HasForeignKey(t => t.IntegracaoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
