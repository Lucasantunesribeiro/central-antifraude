using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class Backtests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "execucoes_de_backtest",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    descricao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    regra_candidata_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidato = table.Column<string>(type: "jsonb", nullable: false),
                    versao_de_perfil_vigente_id = table.Column<Guid>(type: "uuid", nullable: false),
                    numero_da_versao_de_perfil_vigente = table.Column<int>(type: "integer", nullable: false),
                    inicio = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fim = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resultado = table.Column<string>(type: "jsonb", nullable: true),
                    mensagem_de_erro = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    solicitada_por_id = table.Column<Guid>(type: "uuid", nullable: false),
                    solicitada_por_descricao = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    solicitada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    iniciada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    concluida_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    versao = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_execucoes_de_backtest", x => x.id);
                    table.ForeignKey(
                        name: "fk_execucoes_de_backtest_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_execucoes_de_backtest_versoes_de_perfil_de_risco_versao_de_",
                        column: x => x.versao_de_perfil_vigente_id,
                        principalTable: "versoes_de_perfil_de_risco",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_execucoes_de_backtest_organizacao_id_solicitada_em",
                table: "execucoes_de_backtest",
                columns: new[] { "organizacao_id", "solicitada_em" });

            migrationBuilder.CreateIndex(
                name: "ix_execucoes_de_backtest_organizacao_id_status",
                table: "execucoes_de_backtest",
                columns: new[] { "organizacao_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_execucoes_de_backtest_versao_de_perfil_vigente_id",
                table: "execucoes_de_backtest",
                column: "versao_de_perfil_vigente_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "execucoes_de_backtest");
        }
    }
}
