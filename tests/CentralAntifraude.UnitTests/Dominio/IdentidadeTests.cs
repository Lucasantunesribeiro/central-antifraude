using CentralAntifraude.Domain;
using CentralAntifraude.Domain.Identidade;
using CentralAntifraude.Domain.Primitivos;

namespace CentralAntifraude.UnitTests.Dominio;

public sealed class EmailTests
{
    [Fact]
    public void Normaliza_para_minusculas_e_sem_espacos()
    {
        // Sem normalizacao, "Ana@Empresa.com" e "ana@empresa.com" seriam dois
        // usuarios diferentes apesar da restricao unica no banco.
        var email = Email.De("  Ana.Silva@Empresa.COM  ");

        Assert.Equal("ana.silva@empresa.com", email.Valor);
    }

    [Fact]
    public void Enderecos_que_diferem_so_por_maiuscula_sao_iguais()
    {
        Assert.Equal(Email.De("ANA@EMPRESA.COM"), Email.De("ana@empresa.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("semarroba.com")]
    [InlineData("@dominio.com")]
    [InlineData("usuario@")]
    [InlineData("usuario@dominiosemponto")]
    [InlineData("usuario@.com")]
    [InlineData("usuario@dominio.")]
    [InlineData("dois@arrobas@dominio.com")]
    [InlineData("com espaco@dominio.com")]
    public void Recusa_endereco_malformado(string valor)
    {
        Assert.False(Email.TentarCriar(valor, out _, out var erro));
        Assert.NotEmpty(erro);
    }

    [Fact]
    public void Recusa_endereco_maior_que_o_limite_da_coluna()
    {
        var enorme = new string('a', Email.TamanhoMaximo) + "@dominio.com";

        Assert.False(Email.TentarCriar(enorme, out _, out _));
    }

    [Fact]
    public void Instancia_padrao_nao_e_valida()
    {
        Assert.False(default(Email).EhValido);
    }
}

public sealed class OrganizacaoTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nasce_ativa_com_codigo_normalizado()
    {
        var organizacao = Organizacao.Criar("Banco Fictício S.A.", "  Banco-Ficticio  ", Agora);

        Assert.True(organizacao.Ativa);
        Assert.Equal("banco-ficticio", organizacao.Codigo);
        Assert.True(Identificador.EhDoFormatoAdotado(organizacao.Id));
    }

    [Theory]
    [InlineData("com espaco")]
    [InlineData("com_underscore")]
    [InlineData("-comeca-com-hifen")]
    [InlineData("termina-com-hifen-")]
    [InlineData("MAIUSCULA/BARRA")]
    [InlineData("")]
    public void Recusa_codigo_fora_do_conjunto_permitido(string codigo)
    {
        Assert.Throws<ViolacaoDeInvariante>(() => Organizacao.Criar("Nome", codigo, Agora));
    }

    [Fact]
    public void Desativar_e_reativar_sao_reversiveis()
    {
        var organizacao = Organizacao.Criar("Nome", "codigo", Agora);

        organizacao.Desativar();
        Assert.False(organizacao.Ativa);

        organizacao.Reativar();
        Assert.True(organizacao.Ativa);
    }
}

public sealed class UsuarioTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organizacao = Identificador.Novo();

    private static Usuario Criar(PerfilDeUsuario perfil = PerfilDeUsuario.AnalistaDeFraude) =>
        Usuario.Criar(Organizacao, Email.De("ana@empresa.com"), "Ana", "hash", perfil, Agora);

    [Fact]
    public void Nasce_ativo_e_vinculado_a_organizacao()
    {
        var usuario = Criar();

        Assert.True(usuario.Ativo);
        Assert.Equal(Organizacao, usuario.OrganizacaoId);
        Assert.Equal(Agora, usuario.CriadoEm);
    }

    [Fact]
    public void Recusa_usuario_sem_organizacao()
    {
        Assert.Throws<ViolacaoDeInvariante>(() =>
            Usuario.Criar(Guid.Empty, Email.De("a@b.com"), "Ana", "hash", PerfilDeUsuario.Auditor, Agora));
    }

    [Fact]
    public void Recusa_usuario_sem_senha_definida()
    {
        Assert.Throws<ViolacaoDeInvariante>(() =>
            Usuario.Criar(Organizacao, Email.De("a@b.com"), "Ana", "  ", PerfilDeUsuario.Auditor, Agora));
    }

    [Fact]
    public void Recusa_perfil_fora_do_enum()
    {
        // Um inteiro qualquer convertido para o enum nao pode virar perfil:
        // e assim que uma escalada de privilegio entraria pela borda.
        Assert.Throws<ViolacaoDeInvariante>(() =>
            Usuario.Criar(Organizacao, Email.De("a@b.com"), "Ana", "hash", (PerfilDeUsuario)99, Agora));
    }

    [Fact]
    public void Alterar_perfil_atualiza_o_carimbo_de_tempo()
    {
        var usuario = Criar(PerfilDeUsuario.Auditor);
        var depois = Agora.AddHours(1);

        usuario.AlterarPerfil(PerfilDeUsuario.Administrador, depois);

        Assert.Equal(PerfilDeUsuario.Administrador, usuario.Perfil);
        Assert.Equal(depois, usuario.AtualizadoEm);
    }

    [Fact]
    public void Alterar_para_o_mesmo_perfil_nao_mexe_no_carimbo()
    {
        var usuario = Criar(PerfilDeUsuario.Auditor);

        usuario.AlterarPerfil(PerfilDeUsuario.Auditor, Agora.AddHours(1));

        Assert.Equal(Agora, usuario.AtualizadoEm);
    }

    [Fact]
    public void Desativar_e_idempotente()
    {
        var usuario = Criar();
        var primeiraVez = Agora.AddHours(1);

        usuario.Desativar(primeiraVez);
        usuario.Desativar(Agora.AddHours(5));

        Assert.False(usuario.Ativo);
        Assert.Equal(primeiraVez, usuario.AtualizadoEm);
    }
}

public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Validade = TimeSpan.FromDays(14);
    private static readonly Guid Organizacao = Identificador.Novo();
    private static readonly Guid UsuarioId = Identificador.Novo();

    private static RefreshToken Novo() =>
        RefreshToken.IniciarFamilia(Organizacao, UsuarioId, "hash-inicial", Agora, Validade);

    [Fact]
    public void Token_novo_pode_ser_usado()
    {
        var token = Novo();

        Assert.True(token.PodeSerUsado(Agora));
        Assert.False(token.JaFoiUsado);
        Assert.False(token.EstaRevogado);
    }

    [Fact]
    public void Sucessor_herda_a_familia_do_antecessor()
    {
        // A familia e o que permite derrubar a sessao inteira quando um token
        // vazado reaparece. Se ela nao fosse herdada, cada rotacao criaria uma
        // sessao orfa e a deteccao de reuso nao alcancaria nada.
        var token = Novo();

        var sucessor = token.Suceder("hash-2", Agora.AddMinutes(10), Validade);

        Assert.Equal(token.FamiliaId, sucessor.FamiliaId);
        Assert.Equal(token.UsuarioId, sucessor.UsuarioId);
        Assert.Equal(token.OrganizacaoId, sucessor.OrganizacaoId);
        Assert.NotEqual(token.Id, sucessor.Id);
    }

    [Fact]
    public void Token_usado_nao_pode_ser_usado_de_novo()
    {
        var token = Novo();

        token.MarcarComoUsado(Agora.AddMinutes(5));

        Assert.True(token.JaFoiUsado);
        Assert.False(token.PodeSerUsado(Agora.AddMinutes(6)));
        Assert.Equal(MotivoDeRevogacao.Rotacionado, token.MotivoDaRevogacao);
    }

    [Fact]
    public void Marcar_como_usado_duas_vezes_e_erro_de_programacao()
    {
        var token = Novo();
        token.MarcarComoUsado(Agora);

        Assert.Throws<ViolacaoDeInvariante>(() => token.MarcarComoUsado(Agora.AddMinutes(1)));
    }

    [Fact]
    public void Token_expirado_nao_pode_ser_usado()
    {
        var token = Novo();

        Assert.False(token.PodeSerUsado(Agora + Validade));
        Assert.False(token.PodeSerUsado(Agora + Validade + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Token_revogado_nao_pode_ser_usado()
    {
        var token = Novo();

        token.Revogar(MotivoDeRevogacao.Logout, Agora.AddMinutes(1));

        Assert.False(token.PodeSerUsado(Agora.AddMinutes(2)));
    }

    [Fact]
    public void Revogar_preserva_o_primeiro_motivo()
    {
        // O primeiro motivo e o que explica o que aconteceu. Se um logout
        // posterior sobrescrevesse "ReusoDetectado", a investigacao perderia
        // justamente o sinal de incidente.
        var token = Novo();

        token.Revogar(MotivoDeRevogacao.ReusoDetectado, Agora.AddMinutes(1));
        token.Revogar(MotivoDeRevogacao.Logout, Agora.AddMinutes(2));

        Assert.Equal(MotivoDeRevogacao.ReusoDetectado, token.MotivoDaRevogacao);
        Assert.Equal(Agora.AddMinutes(1), token.RevogadoEm);
    }

    [Fact]
    public void Recusa_validade_nao_positiva()
    {
        Assert.Throws<ViolacaoDeInvariante>(() =>
            RefreshToken.IniciarFamilia(Organizacao, UsuarioId, "hash", Agora, TimeSpan.Zero));
    }
}
