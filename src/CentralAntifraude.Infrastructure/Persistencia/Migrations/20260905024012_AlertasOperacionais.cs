using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class AlertasOperacionais : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alertas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    avaliacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decisao = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    prioridade = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    versao_da_politica = table.Column<int>(type: "integer", nullable: false),
                    evento_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_de_correlacao = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    avaliada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alertas", x => x.id);
                    table.ForeignKey(
                        name: "fk_alertas_avaliacoes_de_risco_avaliacao_id",
                        column: x => x.avaliacao_id,
                        principalTable: "avaliacoes_de_risco",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_alertas_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_alertas_transacoes_transacao_id",
                        column: x => x.transacao_id,
                        principalTable: "transacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alertas_avaliacao",
                table: "alertas",
                column: "avaliacao_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_alertas_organizacao_status_criado",
                table: "alertas",
                columns: new[] { "organizacao_id", "status", "criado_em" });

            migrationBuilder.CreateIndex(
                name: "ix_alertas_transacao_id",
                table: "alertas",
                column: "transacao_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alertas");
        }
    }
}
