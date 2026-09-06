using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <summary>
    /// Fase 8 — a regra ganha rascunho, estado e token de concorrencia.
    ///
    /// **As colunas nascem anulaveis, sao preenchidas e so entao viram
    /// obrigatorias.** O scaffold do EF sugeriu `ativa` com `DEFAULT FALSE`, o
    /// que desativaria todas as regras ja provisionadas — o motor de cada
    /// organizacao existente ficaria sem regra nenhuma na primeira publicacao
    /// de perfil. Preencher explicitamente e o que preserva os dados
    /// existentes (CLAUDE.md secao 47), e o passo em tres etapas evita deixar
    /// um `DEFAULT` no banco que o modelo nao declara.
    ///
    /// O indice unico de nome substitui o de tipo: desde esta fase o
    /// Supervisor pode ter duas regras do mesmo tipo com configuracoes
    /// diferentes, e o nome passa a ser o que as distingue.
    /// </summary>
    public partial class GestaoDeRegras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "ix_regras_organizacao_id_tipo",
                table: "regras");

            migrationBuilder.RenameIndex(
                name: "ix_versoes_de_regra_regra_id_numero",
                table: "versoes_de_regra",
                newName: "ix_versoes_de_regra_regra_numero");

            migrationBuilder.RenameIndex(
                name: "ix_versoes_de_perfil_de_risco_perfil_id_numero",
                table: "versoes_de_perfil_de_risco",
                newName: "ix_versoes_de_perfil_perfil_numero");

            migrationBuilder.AddColumn<bool>(
                name: "ativa",
                table: "regras",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<System.DateTimeOffset>(
                name: "atualizada_em",
                table: "regras",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "numero_da_ultima_versao",
                table: "regras",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "versao",
                table: "regras",
                type: "integer",
                nullable: true);

            // O rascunho comeca vazio em toda regra existente: o que estava no
            // ar continua sendo a ultima versao publicada.
            migrationBuilder.AddColumn<string>(
                name: "configuracao_em_rascunho",
                table: "regras",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pontos_em_rascunho",
                table: "regras",
                type: "integer",
                nullable: true);

            // Backfill. Toda regra provisionada ate aqui esta ativa, foi
            // publicada e nunca foi editada depois de criada.
            migrationBuilder.Sql("""
                UPDATE regras
                SET ativa = TRUE,
                    atualizada_em = criada_em,
                    versao = 1,
                    numero_da_ultima_versao = COALESCE(
                        (SELECT MAX(v.numero)
                         FROM versoes_de_regra v
                         WHERE v.regra_id = regras.id),
                        0);
                """);

            migrationBuilder.AlterColumn<bool>(
                name: "ativa",
                table: "regras",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.AlterColumn<System.DateTimeOffset>(
                name: "atualizada_em",
                table: "regras",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(System.DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "numero_da_ultima_versao",
                table: "regras",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "versao",
                table: "regras",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_regras_organizacao_nome",
                table: "regras",
                columns: ["organizacao_id", "nome"],
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "ix_regras_organizacao_nome",
                table: "regras");

            migrationBuilder.DropColumn(name: "ativa", table: "regras");
            migrationBuilder.DropColumn(name: "atualizada_em", table: "regras");
            migrationBuilder.DropColumn(name: "configuracao_em_rascunho", table: "regras");
            migrationBuilder.DropColumn(name: "numero_da_ultima_versao", table: "regras");
            migrationBuilder.DropColumn(name: "pontos_em_rascunho", table: "regras");
            migrationBuilder.DropColumn(name: "versao", table: "regras");

            migrationBuilder.RenameIndex(
                name: "ix_versoes_de_regra_regra_numero",
                table: "versoes_de_regra",
                newName: "ix_versoes_de_regra_regra_id_numero");

            migrationBuilder.RenameIndex(
                name: "ix_versoes_de_perfil_perfil_numero",
                table: "versoes_de_perfil_de_risco",
                newName: "ix_versoes_de_perfil_de_risco_perfil_id_numero");

            // A volta so e possivel se nao houver duas regras do mesmo tipo na
            // mesma organizacao — o que e exatamente a liberdade que esta fase
            // introduziu. Reverter depois de criar a segunda regra de um tipo
            // exige decidir qual delas fica.
            migrationBuilder.CreateIndex(
                name: "ix_regras_organizacao_id_tipo",
                table: "regras",
                columns: ["organizacao_id", "tipo"],
                unique: true);
        }
    }
}
