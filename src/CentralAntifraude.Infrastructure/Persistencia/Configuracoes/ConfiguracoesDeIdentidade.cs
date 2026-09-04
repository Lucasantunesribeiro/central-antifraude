using CentralAntifraude.Domain.Auditoria;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CentralAntifraude.Infrastructure.Persistencia.Configuracoes;

public sealed class ConfiguracaoDeOrganizacao : IEntityTypeConfiguration<Organizacao>
{
    public void Configure(EntityTypeBuilder<Organizacao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("organizacoes");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Nome)
            .HasMaxLength(Organizacao.TamanhoMaximoDoNome)
            .IsRequired();

        builder.Property(o => o.Codigo)
            .HasMaxLength(Organizacao.TamanhoMaximoDoCodigo)
            .IsRequired();

        builder.Property(o => o.Ativa).IsRequired();
        builder.Property(o => o.CriadaEm).IsRequired();

        // O codigo aparece em log e em suporte; duas organizacoes com o mesmo
        // codigo tornariam essa referencia ambigua.
        builder.HasIndex(o => o.Codigo).IsUnique();
    }
}

public sealed class ConfiguracaoDeUsuario : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("usuarios");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.OrganizacaoId).IsRequired();

        builder.Property(u => u.Email)
            .HasConversion(
                email => email.Valor,
                valor => Email.De(valor))
            .HasMaxLength(Email.TamanhoMaximo)
            .IsRequired();

        builder.Property(u => u.NomeCompleto)
            .HasMaxLength(Usuario.TamanhoMaximoDoNome)
            .IsRequired();

        builder.Property(u => u.HashDaSenha)
            .HasMaxLength(500)
            .IsRequired();

        // Enum como texto: uma coluna com "AnalistaDeFraude" continua legivel
        // numa investigacao no psql, e reordenar o enum no codigo nao muda em
        // silencio o significado das linhas ja gravadas.
        builder.Property(u => u.Perfil)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(u => u.Ativo).IsRequired();
        builder.Property(u => u.CriadoEm).IsRequired();
        builder.Property(u => u.AtualizadoEm).IsRequired();

        // Unico GLOBAL, e nao por organizacao: o e-mail e a chave de login, e
        // o login acontece antes de existir tenant (docs/adr/0005).
        builder.HasIndex(u => u.Email).IsUnique();

        // Listagem administrativa: sempre filtra por tenant e ordena por nome.
        builder.HasIndex(u => new { u.OrganizacaoId, u.NomeCompleto });

        builder.HasOne<Organizacao>()
            .WithMany()
            .HasForeignKey(u => u.OrganizacaoId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ConfiguracaoDeRefreshToken : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.OrganizacaoId).IsRequired();
        builder.Property(t => t.UsuarioId).IsRequired();
        builder.Property(t => t.FamiliaId).IsRequired();

        // 64 caracteres: SHA-256 em hexadecimal.
        builder.Property(t => t.HashDoToken)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(t => t.CriadoEm).IsRequired();
        builder.Property(t => t.ExpiraEm).IsRequired();
        builder.Property(t => t.UsadoEm);
        builder.Property(t => t.RevogadoEm);

        builder.Property(t => t.MotivoDaRevogacao)
            .HasConversion<string>()
            .HasMaxLength(40);

        // A busca do refresh e sempre por hash exato, e o valor precisa ser
        // unico: duas linhas com o mesmo hash tornariam a rotacao ambigua.
        builder.HasIndex(t => t.HashDoToken).IsUnique();

        // Revogacao em bloco na deteccao de reuso e no logout.
        builder.HasIndex(t => t.FamiliaId);

        // Revogacao de todo o acesso de um usuario desativado.
        builder.HasIndex(t => t.UsuarioId);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(t => t.UsuarioId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ConfiguracaoDeRegistroDeAuditoria : IEntityTypeConfiguration<RegistroDeAuditoria>
{
    public void Configure(EntityTypeBuilder<RegistroDeAuditoria> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("registros_de_auditoria");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrganizacaoId).IsRequired();

        builder.Property(r => r.Operacao)
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(r => r.AutorId);
        builder.Property(r => r.AutorDescricao).HasMaxLength(200);
        builder.Property(r => r.Entidade).HasMaxLength(100).IsRequired();
        builder.Property(r => r.EntidadeId).HasMaxLength(100);
        builder.Property(r => r.Detalhe).HasMaxLength(RegistroDeAuditoria.TamanhoMaximoDoDetalhe);
        builder.Property(r => r.IdDeCorrelacao).HasMaxLength(64);
        builder.Property(r => r.OcorridoEm).IsRequired();

        // Consulta da Fase 10: por tenant, mais recentes primeiro.
        builder.HasIndex(r => new { r.OrganizacaoId, r.OcorridoEm });

        // Sem chave estrangeira para Usuario de proposito: a trilha precisa
        // sobreviver a exclusao de um usuario, e o nome do autor ja esta
        // copiado em AutorDescricao. Uma FK aqui transformaria a trilha em
        // obstaculo para operacoes legitimas de dados.
    }
}
