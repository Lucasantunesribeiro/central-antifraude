using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ConsoleOperacional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "id_de_correlacao",
                table: "transacoes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_transacoes_organizacao_id_ocorrida_em",
                table: "transacoes",
                columns: new[] { "organizacao_id", "ocorrida_em" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transacoes_organizacao_id_ocorrida_em",
                table: "transacoes");

            migrationBuilder.DropColumn(
                name: "id_de_correlacao",
                table: "transacoes");
        }
    }
}
